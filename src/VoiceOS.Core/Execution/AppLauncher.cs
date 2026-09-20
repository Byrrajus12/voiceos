using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Apps;

namespace VoiceOS.Core.Execution;

/// <summary>
/// Launches apps using trusted metadata from the app catalog.
/// Never constructs shell commands from transcript or model output.
/// </summary>
public sealed class AppLauncher : IAppLauncher
{
    private readonly IAppCatalog _catalog;
    private readonly ILogger<AppLauncher> _logger;

    public AppLauncher(IAppCatalog catalog, ILogger<AppLauncher> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public ExecutionResult Launch(string? candidateId)
    {
        if (string.IsNullOrEmpty(candidateId))
            return ExecutionResult.Fail(ExecutionStatus.AppNotFound, "No app candidate ID");

        var entry = _catalog.FindById(candidateId);
        if (entry is null)
        {
            _logger.LogWarning("App not found in catalog: {Id}", candidateId);
            return ExecutionResult.Fail(ExecutionStatus.AppNotFound, $"App '{candidateId}' not in catalog");
        }

        try
        {
            var psi = entry.LaunchKind == AppLaunchKind.PackagedApp
                ? new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{entry.LaunchTarget}",
                    UseShellExecute = false
                }
                : new ProcessStartInfo
                {
                    FileName = entry.LaunchTarget,
                    UseShellExecute = true
                };
            Process.Start(psi);
            _logger.LogInformation("Launched: {DisplayName} ({Kind}:{Target})",
                entry.DisplayName, entry.LaunchKind, entry.LaunchTarget);
            return ExecutionResult.Ok($"Launched {entry.DisplayName}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch {DisplayName}", entry.DisplayName);
            return ExecutionResult.Fail(ExecutionStatus.PlatformError, ex.Message);
        }
    }
}
