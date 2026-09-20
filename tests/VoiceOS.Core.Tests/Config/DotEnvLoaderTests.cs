using VoiceOS.Core.Config;
using Xunit;

namespace VoiceOS.Core.Tests.Config;

public class DotEnvLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _keysToCleanup = new();

    public DotEnvLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dotenv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    private string WriteEnvFile(string content)
    {
        var path = Path.Combine(_tempDir, ".env");
        File.WriteAllText(path, content);
        return _tempDir;
    }

    [Fact]
    public void ParsesKeyValuePairs()
    {
        var key = $"TEST_VAR_{Guid.NewGuid():N}";
        _keysToCleanup.Add(key);
        WriteEnvFile($"{key}=hello_world");

        DotEnvLoader.Load(_tempDir);

        Assert.Equal("hello_world", Environment.GetEnvironmentVariable(key));
    }

    [Fact]
    public void SkipsCommentLines()
    {
        var key = $"COMMENT_VAR_{Guid.NewGuid():N}";
        _keysToCleanup.Add(key);
        WriteEnvFile($"# this is a comment\n{key}=value");

        DotEnvLoader.Load(_tempDir);

        Assert.Equal("value", Environment.GetEnvironmentVariable(key));
    }

    [Fact]
    public void SkipsEmptyLines()
    {
        var key = $"EMPTY_LINE_VAR_{Guid.NewGuid():N}";
        _keysToCleanup.Add(key);
        WriteEnvFile($"\n\n{key}=abc\n\n");

        DotEnvLoader.Load(_tempDir);

        Assert.Equal("abc", Environment.GetEnvironmentVariable(key));
    }

    [Fact]
    public void DoesNotOverwriteExistingEnvVar()
    {
        var key = $"EXISTING_VAR_{Guid.NewGuid():N}";
        _keysToCleanup.Add(key);
        Environment.SetEnvironmentVariable(key, "original");
        WriteEnvFile($"{key}=overwritten");

        DotEnvLoader.Load(_tempDir);

        Assert.Equal("original", Environment.GetEnvironmentVariable(key));
    }

    [Fact]
    public void HandlesValueWithEqualsSign()
    {
        var key = $"EQUALS_VAR_{Guid.NewGuid():N}";
        _keysToCleanup.Add(key);
        WriteEnvFile($"{key}=base64==");

        DotEnvLoader.Load(_tempDir);

        Assert.Equal("base64==", Environment.GetEnvironmentVariable(key));
    }

    [Fact]
    public void DoesNothingWhenNoEnvFile()
    {
        // Should not throw
        var emptyDir = Path.Combine(_tempDir, "no-env-file");
        Directory.CreateDirectory(emptyDir);
        DotEnvLoader.Load(emptyDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        foreach (var key in _keysToCleanup)
            Environment.SetEnvironmentVariable(key, null);
    }
}
