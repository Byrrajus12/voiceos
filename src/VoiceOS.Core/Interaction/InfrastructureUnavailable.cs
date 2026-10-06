namespace VoiceOS.Core.Interaction;

public enum UnavailableReason { IntentService, BrowserGoalService, ChromeCompanion }

public sealed class InfrastructureUnavailableException(UnavailableReason reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public UnavailableReason Reason { get; } = reason;
}
