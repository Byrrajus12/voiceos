namespace VoiceOS.Core.Speech;

public static class ModelManager
{
    private static readonly string[] RequiredFiles =
    [
        "encoder.int8.onnx",
        "decoder.int8.onnx",
        "joiner.int8.onnx",
        "tokens.txt",
    ];

    public static (bool Valid, string[] MissingFiles) ValidateModelFiles(string modelDir)
    {
        var missing = RequiredFiles
            .Where(f => !File.Exists(Path.Combine(modelDir, f)))
            .ToArray();
        return (missing.Length == 0, missing);
    }

    public static string GetDefaultModelDirectory()
        => Path.Combine(AppContext.BaseDirectory, "models", "parakeet-tdt-0.6b-v2-int8");

    public static string DownloadInstructions =>
        "Download from: https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2\n" +
        "Extract to: models/parakeet-tdt-0.6b-v2-int8/ relative to the VoiceOS executable.";
}
