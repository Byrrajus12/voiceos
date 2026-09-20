using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoiceOS.Core.Speech;

namespace VoiceOS.Eval.Evaluation;

public static class SttEvaluationHarness
{
    public static async Task RunAsync(string recordingsDir, string modelDir, ILoggerFactory loggerFactory)
    {
        if (!Directory.Exists(recordingsDir))
        {
            Console.Error.WriteLine($"Recordings directory not found: {recordingsDir}");
            return;
        }

        var wavFiles = Directory.GetFiles(recordingsDir, "*.wav");
        if (wavFiles.Length == 0)
        {
            Console.Error.WriteLine($"No .wav files found in {recordingsDir}");
            return;
        }

        var (valid, missing) = ModelManager.ValidateModelFiles(modelDir);
        if (!valid)
        {
            Console.Error.WriteLine($"Model missing files: {string.Join(", ", missing)}");
            Console.Error.WriteLine(ModelManager.DownloadInstructions);
            return;
        }

        var logger = loggerFactory.CreateLogger<ParakeetSpeechRecognizer>();
        using var recognizer = new ParakeetSpeechRecognizer(modelDir, logger);

        // Wait for model to load
        Console.WriteLine("Loading model...");
        while (!recognizer.IsReady)
            await Task.Delay(100);

        Console.WriteLine($"{"File",-40} {"Transcript",-50} {"AudioMs",8} {"SttMs",7} {"RTF",6}");
        Console.WriteLine(new string('-', 120));

        foreach (var wavFile in wavFiles.OrderBy(f => f))
        {
            var (samples, audioDurationMs) = LoadWavAsFloat(wavFile);
            if (samples == null)
            {
                Console.WriteLine($"{Path.GetFileName(wavFile),-40} {"[failed to load]",-50}");
                continue;
            }

            var sw = Stopwatch.StartNew();
            var result = await recognizer.TranscribeAsync(samples.AsMemory());
            sw.Stop();

            double rtf = audioDurationMs > 0 ? result.TranscriptionDurationMs / audioDurationMs : 0;

            Console.WriteLine(
                $"{Truncate(Path.GetFileName(wavFile), 38),-40} {Truncate(result.Transcript, 48),-50} {audioDurationMs,8:F0} {result.TranscriptionDurationMs,7:F0} {rtf,6:F2}");

            if (!result.Success)
                Console.WriteLine($"  Error: {result.Error}");
        }
    }

    private static (float[]? Samples, double DurationMs) LoadWavAsFloat(string wavPath)
    {
        try
        {
            using var reader = new AudioFileReader(wavPath);
            var samples = new List<float>();
            var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                samples.AddRange(buffer.Take(read));

            // If stereo, average channels to mono
            float[] mono;
            if (reader.WaveFormat.Channels == 2)
            {
                mono = new float[samples.Count / 2];
                for (int i = 0; i < mono.Length; i++)
                    mono[i] = (samples[i * 2] + samples[i * 2 + 1]) * 0.5f;
            }
            else
            {
                mono = [.. samples];
            }

            double durationMs = mono.Length / (double)reader.WaveFormat.SampleRate * 1000.0;
            return (mono, durationMs);
        }
        catch
        {
            return (null, 0);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];
}
