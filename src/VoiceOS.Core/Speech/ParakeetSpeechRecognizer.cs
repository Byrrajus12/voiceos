using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace VoiceOS.Core.Speech;

public sealed class ParakeetSpeechRecognizer : ISpeechRecognizer
{
    private readonly string _modelDir;
    private readonly ILogger<ParakeetSpeechRecognizer> _logger;
    private readonly Task _initTask;

    private OfflineRecognizer? _recognizer;
    private volatile bool _isReady;
    private bool _disposed;

    public bool IsReady => _isReady;

    public ParakeetSpeechRecognizer(string modelDir, ILogger<ParakeetSpeechRecognizer> logger)
    {
        _modelDir = modelDir;
        _logger = logger;
        _initTask = Task.Run(InitializeCore);
    }

    private void InitializeCore()
    {
        try
        {
            _logger.LogInformation("Loading Parakeet model from {Dir}...", _modelDir);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var config = new OfflineRecognizerConfig
            {
                FeatConfig = new FeatureConfig
                {
                    SampleRate = 16000,
                    FeatureDim = 80,
                },
                ModelConfig = new OfflineModelConfig
                {
                    Transducer = new OfflineTransducerModelConfig
                    {
                        Encoder = Path.Combine(_modelDir, "encoder.int8.onnx"),
                        Decoder = Path.Combine(_modelDir, "decoder.int8.onnx"),
                        Joiner = Path.Combine(_modelDir, "joiner.int8.onnx"),
                    },
                    Tokens = Path.Combine(_modelDir, "tokens.txt"),
                    NumThreads = 4,
                    Debug = 0,
                    Provider = "cpu",
                },
            };

            _recognizer = new OfflineRecognizer(config);
            _isReady = true;
            _logger.LogInformation("Parakeet model loaded in {ElapsedMs:F0}ms", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Parakeet model from {Dir}", _modelDir);
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _initTask.ConfigureAwait(false);

        if (_recognizer == null)
            return new TranscriptionResult("", 0, 0, "ParakeetSpeechRecognizer", false, "Model not loaded");

        ct.ThrowIfCancellationRequested();

        var sampleCount = audio.Length;
        double audioDurationMs = sampleCount / 16.0; // 16kHz → ms

        return await Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(sampleRate: 16000, samples: audio.ToArray());
                _recognizer.Decode(stream);
                var text = stream.Result.Text.Trim();
                sw.Stop();
                return new TranscriptionResult(
                    text,
                    audioDurationMs,
                    sw.Elapsed.TotalMilliseconds,
                    "ParakeetSpeechRecognizer",
                    true);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Transcription failed");
                return new TranscriptionResult("", audioDurationMs, sw.Elapsed.TotalMilliseconds,
                    "ParakeetSpeechRecognizer", false, ex.Message);
            }
        }, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recognizer?.Dispose();
    }
}
