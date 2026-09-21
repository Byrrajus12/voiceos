namespace VoiceOS.Core.Apps;

public enum AppLaunchKind { Win32, PackagedApp }

public record AppEntry(
    string Id,
    string DisplayName,
    string? ProcessName,
    AppLaunchKind LaunchKind,
    string LaunchTarget,
    string? LaunchArguments = null,
    string? AppUserModelId = null,
    string? NewInstanceArguments = null);
