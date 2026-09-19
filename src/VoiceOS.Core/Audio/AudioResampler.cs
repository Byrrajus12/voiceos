using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceOS.Core.Audio;

/// <summary>
/// Converts raw captured audio bytes to 16kHz mono 16-bit PCM using NAudio's built-in providers.
/// </summary>
public static class AudioResampler
{
    private static readonly WaveFormat TargetFormat = new WaveFormat(16000, 16, 1);

    public static (byte[] Pcm, WaveFormat Format) ResampleTo16kHzMono16Bit(
        byte[] rawData, WaveFormat sourceFormat)
    {
        if (rawData.Length == 0)
            return (Array.Empty<byte>(), TargetFormat);

        using var inputStream = new RawSourceWaveStream(rawData, 0, rawData.Length, sourceFormat);

        ISampleProvider samples = inputStream.ToSampleProvider();

        if (sourceFormat.Channels == 2)
            samples = new StereoToMonoSampleProvider(samples);

        if (samples.WaveFormat.SampleRate != 16000)
            samples = new WdlResamplingSampleProvider(samples, 16000);

        var pcm16 = new SampleToWaveProvider16(samples);

        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = pcm16.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, read);

        return (output.ToArray(), TargetFormat);
    }
}
