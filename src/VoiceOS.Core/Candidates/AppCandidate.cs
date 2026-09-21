namespace VoiceOS.Core.Candidates;

public record AppCandidate(string Id, string DisplayName, string? ProcessName = null, string? AppUserModelId = null);
