using VoiceOS.Core.Activation;

namespace VoiceOS.Core.Config;

public class VoiceOSConfig
{
    public string ActivationKey { get; set; } = "RControlKey";
    public ActivationMode ActivationMode { get; set; } = ActivationMode.HoldOrToggle;
    public int HoldThresholdMs { get; set; } = 300;
    public int GracePeriodMs { get; set; } = 50;
    public bool DebugOutputEnabled { get; set; } = true;

    public string ModelDirectory { get; set; } = "models/parakeet-tdt-0.6b-v2-int8";
    public double JevCommandThreshold { get; set; } = 0.35;
    public double JevActionThreshold { get; set; } = 0.40;
    public string TypeSafeModel { get; set; } = "jev-latest";

    public TimeSpan HoldThreshold => TimeSpan.FromMilliseconds(HoldThresholdMs);
    public TimeSpan GracePeriod => TimeSpan.FromMilliseconds(GracePeriodMs);
}
