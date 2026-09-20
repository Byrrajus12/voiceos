namespace VoiceOS.Core.Speech;

public static class PcmToFloatConverter
{
    /// <summary>Converts 16-bit little-endian PCM bytes to normalized float samples.</summary>
    public static float[] Convert16BitPcmToFloat(byte[] pcm)
    {
        if (pcm.Length == 0) return [];

        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            samples[i] = s / 32768f;
        }
        return samples;
    }
}
