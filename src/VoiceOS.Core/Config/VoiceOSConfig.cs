using VoiceOS.Core.Activation;

namespace VoiceOS.Core.Config;

public class VoiceOSConfig
{
    public string ActivationKey { get; set; } = "RControlKey";
    public ActivationMode ActivationMode { get; set; } = ActivationMode.HoldOrToggle;
    public string DictationActivationKey { get; set; } = "F8";
    public ActivationMode DictationActivationMode { get; set; } = ActivationMode.PushToTalk;
    public int HoldThresholdMs { get; set; } = 300;
    public int GracePeriodMs { get; set; } = 50;
    public bool DebugOutputEnabled { get; set; } = true;

    public string ModelDirectory { get; set; } = "models/parakeet-tdt-0.6b-v2-int8";
    public double JevCommandThreshold { get; set; } = 0.35;
    public double JevActionThreshold { get; set; } = 0.40;
    public string TypeSafeModel { get; set; } = "jev-latest";

    public bool FrontDoorGrounding { get; set; } = true;
    public bool SpeculativeDirectDecision { get; set; } = true;
    public bool DirectRescue { get; set; } = true;
    /// <summary>How long after routing a confident browser route waits for the speculative direct decision to be ready.</summary>
    public int DirectFirstGraceMs { get; set; } = 400;
    /// <summary>
    /// On (default): a proof from an active family (Surface, Activate) completes the browser step at once, with the
    /// legacy completion path as fallback. Shadow evaluates and logs only; Off is exactly the legacy loop.
    /// Override with VOICEOS_BROWSER_PROOF=Off|Shadow|On.
    /// </summary>
    public Interaction.ProofMode BrowserProofMode { get; set; } = Interaction.ProofMode.On;

    public void ApplyEnvironmentOverrides()
    {
        static bool Read(string name, bool fallback)
            => bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
        FrontDoorGrounding = Read("VOICEOS_FRONT_DOOR_GROUNDING", FrontDoorGrounding);
        SpeculativeDirectDecision = Read("VOICEOS_SPECULATIVE_DIRECT_DECISION", SpeculativeDirectDecision);
        DirectRescue = Read("VOICEOS_DIRECT_RESCUE", DirectRescue);
        if (Enum.TryParse<Interaction.ProofMode>(Environment.GetEnvironmentVariable("VOICEOS_BROWSER_PROOF"), true, out var proof))
            BrowserProofMode = proof;
    }

    public TimeSpan HoldThreshold => TimeSpan.FromMilliseconds(HoldThresholdMs);
    public TimeSpan GracePeriod => TimeSpan.FromMilliseconds(GracePeriodMs);
}
