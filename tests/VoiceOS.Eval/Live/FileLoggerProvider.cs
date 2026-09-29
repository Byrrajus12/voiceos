using Microsoft.Extensions.Logging;

namespace VoiceOS.Eval.Live;

/// <summary>Minimal file logger: writes Information+ product logs (with active scopes, e.g.
/// activation_id) to a single file, one line per entry. Not unit tested — a thin I/O sink.</summary>
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer = new(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };

    /// <summary>Opt-in Debug lines from the browser service category (bind diagnostics carry element labels).</summary>
    public static bool ProofDiagnostics { get; } = Environment.GetEnvironmentVariable("VOICEOS_EVAL_PROOF_DIAG") == "1";

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(string line)
    {
        lock (_gate) _writer.WriteLine(line);
    }

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => ScopeStack.Push(state?.ToString() ?? "");

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information
            || (ProofDiagnostics && logLevel == LogLevel.Debug && categoryName.EndsWith("BrowserInteractionService", StringComparison.Ordinal));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var scopes = ScopeStack.Current;
            var scopeText = scopes.Count == 0 ? "" : $" [{string.Join(" ", scopes)}]";
            var line = $"{DateTimeOffset.UtcNow:O} {logLevel} {categoryName}{scopeText}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            provider.Write(line);
        }
    }

    /// <summary>Per-thread scope stack; ambient like the framework's own scope providers.</summary>
    private static class ScopeStack
    {
        private static readonly AsyncLocal<Stack<string>?> Stack = new();
        public static IReadOnlyCollection<string> Current => Stack.Value ?? [];

        public static IDisposable Push(string scope)
        {
            Stack.Value ??= new Stack<string>();
            Stack.Value.Push(scope);
            return new Popper();
        }

        private sealed class Popper : IDisposable
        {
            public void Dispose() => Stack.Value?.Pop();
        }
    }
}
