using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Candidates;

namespace VoiceOS.Core.Execution;


/// <summary>
/// Real IWindowAwareLauncher: focuses an existing window or launches and polls for the resulting window.
///
/// After launching, polls at a fixed interval until a window matching app identity appears that was
/// NOT present before the launch. Never returns a pre-existing window as the launch result.
/// Never assumes the foreground window is the result.
/// Validate with physical testing; do not unit-test the polling loop directly.
/// </summary>
public sealed class WindowAwareLauncher : IWindowAwareLauncher
{
    // Internal for test overrides — tests can shrink these to avoid real waits.
    internal TimeSpan LaunchTimeout = TimeSpan.FromSeconds(8);
    internal TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly IAppCatalog _catalog;
    private readonly IWindowService _windows;
    private readonly ILogger<WindowAwareLauncher> _logger;
    private readonly Func<IReadOnlyList<WindowCandidate>> _getWindows;
    private readonly Action<ProcessStartInfo> _launch;
    private readonly INewInstanceArgsProvider? _newInstanceArgsProvider;

    public WindowAwareLauncher(
        IAppCatalog catalog,
        IWindowService windows,
        ILogger<WindowAwareLauncher> logger,
        Func<IReadOnlyList<WindowCandidate>>? getWindows = null,
        Action<ProcessStartInfo>? launch = null,
        INewInstanceArgsProvider? newInstanceArgsProvider = null)
    {
        _catalog = catalog;
        _windows = windows;
        _logger = logger;
        _getWindows = getWindows ?? CandidateBuilder.GetOpenWindows;
        _launch = launch ?? (psi => Process.Start(psi));
        _newInstanceArgsProvider = newInstanceArgsProvider;
    }

    public async Task<AppWindowResult> FocusOrLaunchAsync(
        AppTarget target,
        IReadOnlyList<WindowCandidate> snapshot,
        CancellationToken ct = default)
    {
        var entry = _catalog.FindById(target.AppCandidateId);
        if (entry == null)
            return AppWindowResult.Fail(ExecutionStatus.AppNotFound,
                $"App '{target.AppCandidateId}' not in catalog");

        var matching = AppWindowMatcher.FindMatching(entry.ProcessName, entry.AppUserModelId, snapshot);

        if (matching.Count == 1)
        {
            _logger.LogInformation("FocusOrLaunch: focusing '{App}'", entry.DisplayName);
            var r = _windows.Focus(matching[0].Id, snapshot);
            return r.Status == ExecutionStatus.Success
                ? AppWindowResult.Ok(matching[0], r.Detail)
                : AppWindowResult.Fail(r.Status, r.Detail ?? "Focus failed");
        }

        if (matching.Count > 1)
        {
            _logger.LogInformation("FocusOrLaunch: ambiguous — multiple '{App}' windows", entry.DisplayName);
            return AppWindowResult.Fail(ExecutionStatus.NoAction,
                $"Multiple '{entry.DisplayName}' windows open — ambiguous");
        }

        return await LaunchAndWaitAsync(entry, ct);
    }

    public async Task<AppWindowResult> LaunchNewAsync(AppTarget target, CancellationToken ct = default)
    {
        var entry = _catalog.FindById(target.AppCandidateId);
        if (entry == null)
            return AppWindowResult.Fail(ExecutionStatus.AppNotFound,
                $"App '{target.AppCandidateId}' not in catalog");

        // Resolve new-instance args via provider (e.g. Chrome reads profile.last_used dynamically),
        // falling back to any static NewInstanceArguments on the catalog entry.
        var newInstanceArgs = _newInstanceArgsProvider != null
            ? _newInstanceArgsProvider.GetNewInstanceArguments(entry)
            : entry.NewInstanceArguments;
        return await LaunchAndWaitAsync(entry, ct, argsOverride: newInstanceArgs);
    }

    private async Task<AppWindowResult> LaunchAndWaitAsync(
        AppEntry entry,
        CancellationToken ct,
        string? argsOverride = null)
    {
        // Snapshot HWNDs already matching this app before launch so we can identify the NEW window.
        var preLaunchHwnds = AppWindowMatcher
            .FindMatching(entry.ProcessName, entry.AppUserModelId, _getWindows())
            .Select(w => w.Hwnd)
            .ToHashSet();

        try
        {
            ProcessStartInfo psi = entry.LaunchKind == AppLaunchKind.PackagedApp
                ? new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{entry.LaunchTarget}",
                    UseShellExecute = false
                }
                : new ProcessStartInfo
                {
                    FileName = entry.LaunchTarget,
                    Arguments = argsOverride ?? entry.LaunchArguments ?? "",
                    UseShellExecute = true
                };
            _launch(psi);
            _logger.LogInformation("Launched '{App}' ({Args}) — polling for new window",
                entry.DisplayName, psi.Arguments);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch '{App}'", entry.DisplayName);
            return AppWindowResult.Fail(ExecutionStatus.PlatformError, ex.Message);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(LaunchTimeout);

        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, timeoutCts.Token).ConfigureAwait(false);
                var windows = _getWindows();
                var newWindow = AppWindowMatcher
                    .FindMatching(entry.ProcessName, entry.AppUserModelId, windows)
                    .FirstOrDefault(w => !preLaunchHwnds.Contains(w.Hwnd));
                if (newWindow != null)
                {
                    _logger.LogInformation("New window appeared for '{App}'", entry.DisplayName);
                    return AppWindowResult.Ok(newWindow, $"Launched {entry.DisplayName}");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Bounded timeout expired; fall through to failure return
        }

        return AppWindowResult.Fail(ExecutionStatus.WindowNotFound,
            $"Window for '{entry.DisplayName}' did not appear within {LaunchTimeout.TotalSeconds:F0}s");
    }
}
