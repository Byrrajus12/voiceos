using VoiceOS.Core.Activation;

namespace VoiceOS.Core.Config;

public class VoiceOSConfig
{
    public string ActivationKey { get; set; } = "RControlKey";
    public ActivationMode ActivationMode { get; set; } = ActivationMode.HoldOrToggle;
    public int HoldThresholdMs { get; set; } = 300;
    public int GracePeriodMs { get; set; } = 50;
    public bool DebugOutputEnabled { get; set; } = true;

    public TimeSpan HoldThreshold => TimeSpan.FromMilliseconds(HoldThresholdMs);
    public TimeSpan GracePeriod => TimeSpan.FromMilliseconds(GracePeriodMs);
}
