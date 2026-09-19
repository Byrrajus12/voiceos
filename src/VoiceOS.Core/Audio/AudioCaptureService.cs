using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceOS.Core.Audio;

/// <summary>
/// Captures audio from the default input device using WASAPI shared mode.
/// Thread-safe: DataAvailable fires on NAudio's capture thread; Stop/Get is called from the orchestrator.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    private readonly ILogger<AudioCaptureService> _logger;

    private WasapiCapture? _capture;
    private WaveFormat? _captureFormat;
    private readonly List<byte[]> _chunks = new();
    private readonly object _lock = new();
    private bool _isCapturing;
    private bool _disposed;

    public AudioCaptureService(ILogger<AudioCaptureService> logger)
    {
        _logger = logger;
    }

    /// <summary>Returns true if capture started successfully.</summary>
    public bool StartCapture()
    {
        if (_isCapturing) return true;

        try
        {
            lock (_lock)
                _chunks.Clear();

            _capture = new WasapiCapture();
            _captureFormat = _capture.WaveFormat;
            _capture.DataAvailable += OnDataAvailable;

            _capture.StartRecording();
            _isCapturing = true;

            _logger.LogDebug("WASAPI capture started: {Format}", _captureFormat);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start WASAPI capture");
            _capture?.Dispose();
            _capture = null;
            return false;
        }
    }

    /// <summary>Stops capture and returns resampled 16kHz mono 16-bit PCM.</summary>
    public (byte[] Pcm, WaveFormat Format) StopCaptureAndGetPcm()
    {
        if (!_isCapturing || _capture == null)
            return (Array.Empty<byte>(), new WaveFormat(16000, 16, 1));

        _isCapturing = false;

        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture.RecordingStopped += (_, _) => stopped.TrySetResult(true);
        _capture.StopRecording();
        stopped.Task.Wait(TimeSpan.FromSeconds(5));

        _capture.Dispose();
        var sourceFormat = _captureFormat!;
        _capture = null;

        byte[] combined;
        lock (_lock)
        {
            var totalBytes = _chunks.Sum(c => c.Length);
            combined = new byte[totalBytes];
            int offset = 0;
            foreach (var chunk in _chunks)
            {
                Buffer.BlockCopy(chunk, 0, combined, offset, chunk.Length);
                offset += chunk.Length;
            }
            _chunks.Clear();
        }

        _logger.LogDebug("Captured {Bytes} raw bytes, resampling...", combined.Length);

        return AudioResampler.ResampleTo16kHzMono16Bit(combined, sourceFormat);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);

        lock (_lock)
            _chunks.Add(chunk);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isCapturing)
        {
            _isCapturing = false;
            _capture?.StopRecording();
        }
        _capture?.Dispose();
    }
}
