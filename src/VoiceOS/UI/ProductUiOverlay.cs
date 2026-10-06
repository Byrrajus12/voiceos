using Microsoft.Extensions.Logging;

namespace VoiceOS.UI;

/// <summary>The small UI-facing contract shared by the live shell and preview.</summary>
internal sealed class ProductUiOverlay : IProductUiSurface
{
    private readonly EdgeGlowOverlay _glow = new();
    private readonly StatePillOverlay _pill = new();
    private readonly System.Windows.Forms.Timer _terminalTimer = new();
    private readonly ILogger? _logger;
    private ProductUiState _state;
    private string? _actingMessage;
    private string? _clarificationMessage;
    private string? _errorMessage;
    private string? _lastLoggedMessage;
    private ProductUiState? _lastLoggedState;
    private long _terminalDueAt;

    public ProductUiOverlay(ILogger? logger = null)
    {
        _logger = logger;
        _terminalTimer.Tick += (_, _) =>
        {
            if (_state is not (ProductUiState.Success or ProductUiState.Error or ProductUiState.Clarify))
                return;
            long remaining = _terminalDueAt - Environment.TickCount64;
            if (remaining > 0)
            {
                _terminalTimer.Interval = (int)Math.Min(remaining, int.MaxValue);
                return;
            }
            SetState(ProductUiState.Idle);
        };
    }

    public void SetState(ProductUiState state)
    {
        _terminalTimer.Stop();
        _state = state;
        string? message = state switch
        {
            ProductUiState.Acting => _actingMessage,
            ProductUiState.Clarify => _clarificationMessage,
            ProductUiState.Error => _errorMessage,
            _ => null
        };
        if (_lastLoggedState != state || _lastLoggedMessage != message)
        {
            if (message is null) _logger?.LogInformation("Product UI state={State}", state);
            else _logger?.LogInformation("Product UI state={State} message=\"{Message}\"", state, message);
            _lastLoggedState = state;
            _lastLoggedMessage = message;
        }
        _glow.SetState(state);
        _pill.SetState(state);
        int dwell = state switch
        {
            ProductUiState.Success => 1300,
            ProductUiState.Error => 4000,
            ProductUiState.Clarify => 6000,
            _ => 0
        };
        if (dwell > 0)
        {
            _terminalDueAt = Environment.TickCount64 + dwell;
            _terminalTimer.Interval = dwell;
            _terminalTimer.Start();
        }
    }

    public void SetActingMessage(string? message)
    {
        _actingMessage = message;
        _pill.SetActingMessage(message);
    }
    public void SetClarificationMessage(string message)
    {
        _clarificationMessage = message;
        _pill.SetClarificationMessage(message);
    }
    public void SetErrorMessage(string message)
    {
        _errorMessage = message;
        _pill.SetErrorMessage(message);
    }

#if DEBUG
    public void SavePillFrame(string path) => _pill.SaveFrame(path);
    public string DescribePillFrame() => _pill.DescribeFrame();
#endif

    public void Dispose()
    {
        _terminalTimer.Dispose();
        _pill.Dispose();
        _glow.Dispose();
    }
}
