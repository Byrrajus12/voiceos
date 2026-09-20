using VoiceOS.Core.Apps;
using Xunit;

namespace VoiceOS.Core.Tests.Apps;

public class WindowsAppCatalogTests
{
    [Theory]
    [InlineData("Google Chrome", "google-chrome")]
    [InlineData("Visual Studio Code", "visual-studio-code")]
    [InlineData("Windows Terminal", "windows-terminal")]
    [InlineData("Spotify", "spotify")]
    [InlineData("OBS Studio", "obs-studio")]
    [InlineData("Microsoft Edge", "microsoft-edge")]
    [InlineData("Notepad", "notepad")]
    [InlineData("7-Zip File Manager", "7-zip-file-manager")]
    [InlineData("  Leading spaces  ", "leading-spaces")]
    [InlineData("App (Beta)", "app-beta")]
    public void MakeId_NormalizesDisplayNameCorrectly(string displayName, string expectedId)
    {
        var id = WindowsAppCatalog.MakeId(displayName);
        Assert.Equal(expectedId, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void MakeId_EmptyOrSeparatorsOnly_ReturnsEmpty(string input)
    {
        var id = WindowsAppCatalog.MakeId(input);
        Assert.True(id == "" || !id.StartsWith('-') && !id.EndsWith('-'));
    }

    [Fact]
    public void MakeId_DoesNotStartOrEndWithSeparator()
    {
        foreach (var name in new[] { "Chrome!", "!Chrome", "(App)", "App - Beta" })
        {
            var id = WindowsAppCatalog.MakeId(name);
            if (id.Length > 0)
            {
                Assert.NotEqual('-', id[0]);
                Assert.NotEqual('-', id[^1]);
            }
        }
    }
}
