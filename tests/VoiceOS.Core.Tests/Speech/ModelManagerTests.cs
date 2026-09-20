using VoiceOS.Core.Speech;
using Xunit;

namespace VoiceOS.Core.Tests.Speech;

public class ModelManagerTests : IDisposable
{
    private readonly string _tempDir;

    public ModelManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"voiceos-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ValidateModelFiles_AllPresent_ReturnsValid()
    {
        foreach (var file in new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" })
            File.WriteAllBytes(Path.Combine(_tempDir, file), []);

        var (valid, missing) = ModelManager.ValidateModelFiles(_tempDir);

        Assert.True(valid);
        Assert.Empty(missing);
    }

    [Fact]
    public void ValidateModelFiles_SomeAbsent_ReturnsInvalidWithMissingList()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "encoder.int8.onnx"), []);
        File.WriteAllBytes(Path.Combine(_tempDir, "tokens.txt"), []);

        var (valid, missing) = ModelManager.ValidateModelFiles(_tempDir);

        Assert.False(valid);
        Assert.Contains("decoder.int8.onnx", missing);
        Assert.Contains("joiner.int8.onnx", missing);
        Assert.DoesNotContain("encoder.int8.onnx", missing);
        Assert.DoesNotContain("tokens.txt", missing);
    }

    [Fact]
    public void ValidateModelFiles_EmptyDir_ReturnsAllMissing()
    {
        var (valid, missing) = ModelManager.ValidateModelFiles(_tempDir);

        Assert.False(valid);
        Assert.Equal(4, missing.Length);
    }

    [Fact]
    public void GetDefaultModelDirectory_ContainsExpectedSubpath()
    {
        var dir = ModelManager.GetDefaultModelDirectory();
        Assert.Contains("parakeet-tdt-0.6b-v2-int8", dir);
        Assert.Contains("models", dir);
    }

    [Fact]
    public void DownloadInstructions_ContainsUrl()
    {
        Assert.Contains("sherpa-onnx", ModelManager.DownloadInstructions);
        Assert.Contains("parakeet", ModelManager.DownloadInstructions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
