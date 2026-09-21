namespace VoiceOS.Core.Apps;

/// <summary>
/// Resolves the launch arguments to use when creating a new instance of an application.
/// Allows Chrome-specific logic (dynamic profile resolution) to live in the catalog layer
/// without coupling ProgramExecutor or WindowAwareLauncher to browser concerns.
/// </summary>
public interface INewInstanceArgsProvider
{
    string? GetNewInstanceArguments(AppEntry entry);
}
