namespace VoiceOS.Core.Candidates;

public record WindowCandidate(string Id, string ProcessName, string Title, bool IsForeground = false, nint Hwnd = 0);
