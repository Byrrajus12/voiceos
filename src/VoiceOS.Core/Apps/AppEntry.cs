namespace VoiceOS.Core.Apps;

public enum AppLaunchKind { Win32, PackagedApp }

/// <summary>Which process owns the windows a launch produces.</summary>
public enum AppHostKind
{
    /// <summary>The app's own process (or its AUMID) owns its windows.</summary>
    OwnProcess,
    /// <summary>A shell-namespace item (e.g. File Explorer) whose windows belong to the shell host.</summary>
    ShellNamespace,
    /// <summary>A console program whose window belongs to the terminal host.</summary>
    ConsoleHosted
}

public record AppEntry(
    string Id,
    string DisplayName,
    string? ProcessName,
    AppLaunchKind LaunchKind,
    string LaunchTarget,
    string? LaunchArguments = null,
    string? AppUserModelId = null,
    string? NewInstanceArguments = null,
    AppHostKind Host = AppHostKind.OwnProcess);
