namespace VoiceOS.UI;

/// <summary>Standalone Debug preview; no microphone, model, or backend connection.</summary>
internal sealed class GlowPreviewApplication : ApplicationContext
{
    private readonly ProductUiOverlay _ui = new();
    private CancellationTokenSource? _demo;
    private bool _started;

    public GlowPreviewApplication() => Application.Idle += OnFirstIdle;

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        if (_started) return;
        _started = true;
        Application.Idle -= OnFirstIdle;
        SynchronizationContext context = SynchronizationContext.Current
            ?? throw new InvalidOperationException("WinForms UI context unavailable.");
        Console.WriteLine("Alpha preview: D=full demo, H=Clarify path, X=Error path, L=Listening, U=Understanding, A=Acting with text, N=Acting without text, S=Success, C=Clarify, E=Error, I=Idle, F=save frame, Q=quit. Press Enter.");
        _ = Task.Run(() =>
        {
            while (true)
            {
                string? command = Console.ReadLine();
                if (command is null || command.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    context.Post(_ => ExitThread(), null);
                    return;
                }
                context.Post(_ => Execute(command.Trim().ToLowerInvariant()), null);
            }
        });
    }

    private void Execute(string command)
    {
        if (command != "f")
        {
            _demo?.Cancel();
            _demo?.Dispose();
            _demo = null;
        }
        switch (command)
        {
            case "d":
                _demo = new CancellationTokenSource();
                _ = RunDemoAsync(_demo.Token);
                break;
            case "h":
                _demo = new CancellationTokenSource();
                _ = RunOutcomeAsync(ProductUiState.Clarify, _demo.Token);
                break;
            case "x":
                _demo = new CancellationTokenSource();
                _ = RunOutcomeAsync(ProductUiState.Error, _demo.Token);
                break;
            case "l": _ui.SetState(ProductUiState.Listening); break;
            case "u": _ui.SetState(ProductUiState.Understanding); break;
            case "a":
                _ui.SetActingMessage("Moving window…");
                _ui.SetState(ProductUiState.Acting);
                break;
            case "n":
                _ui.SetActingMessage(null);
                _ui.SetState(ProductUiState.Acting);
                break;
            case "s": _ui.SetState(ProductUiState.Success); break;
            case "c":
                _ui.SetClarificationMessage("Which Chrome window?");
                _ui.SetState(ProductUiState.Clarify);
                break;
            case "e":
                _ui.SetErrorMessage("Couldn't find that window");
                _ui.SetState(ProductUiState.Error);
                break;
            case "i": _ui.SetState(ProductUiState.Idle); break;
#if DEBUG
            case "f":
                string path = Path.Combine(Environment.CurrentDirectory, "pill-frame.png");
                _ui.SavePillFrame(path);
                Console.WriteLine($"Saved {path}: {_ui.DescribePillFrame()}");
                break;
#endif
            default: Console.WriteLine("Unknown preview command."); break;
        }
    }

    private async Task RunDemoAsync(CancellationToken cancellation)
    {
        try
        {
            _ui.SetState(ProductUiState.Idle);
            await Task.Delay(450, cancellation);
            _ui.SetState(ProductUiState.Listening);
            await Task.Delay(3000, cancellation);
            _ui.SetState(ProductUiState.Understanding);
            await Task.Delay(4000, cancellation);
            _ui.SetActingMessage("Moving window…");
            _ui.SetState(ProductUiState.Acting);
            await Task.Delay(4000, cancellation);
            _ui.SetState(ProductUiState.Success);
            await Task.Delay(1500, cancellation);
            _ui.SetState(ProductUiState.Idle);
            Console.WriteLine("Full Alpha lifecycle complete.");
        }
        catch (TaskCanceledException) { }
    }

    private async Task RunOutcomeAsync(ProductUiState outcome, CancellationToken cancellation)
    {
        try
        {
            _ui.SetState(ProductUiState.Idle);
            await Task.Delay(450, cancellation);
            _ui.SetState(ProductUiState.Listening);
            await Task.Delay(3000, cancellation);
            _ui.SetState(ProductUiState.Understanding);
            await Task.Delay(3000, cancellation);
            if (outcome == ProductUiState.Clarify)
                _ui.SetClarificationMessage("Which Chrome window?");
            else
                _ui.SetErrorMessage("Couldn't find that window");
            _ui.SetState(outcome);
            await Task.Delay(6500, cancellation);
            Console.WriteLine($"{outcome} path complete.");
        }
        catch (TaskCanceledException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= OnFirstIdle;
            _demo?.Cancel();
            _demo?.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
