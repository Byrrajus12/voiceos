namespace VoiceOS.Core.Monitors;

/// <summary>
/// Classification of a monitor's physical connection type.
/// Unknown is set when DisplayConfig enrichment fails — it does NOT imply External.
/// </summary>
public enum DisplayConnectionKind { Unknown, Internal, External }
