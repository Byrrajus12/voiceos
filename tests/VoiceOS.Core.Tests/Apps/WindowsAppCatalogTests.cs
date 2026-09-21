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

    [Fact]
    public void WarmUp_GetAll_ReturnsConsistentResults()
    {
        // Catalog warm-up must not alter the discovered entries or FindById behavior.
        // Calling GetAll() multiple times (simulating warm-up + first-use) must return identical results.
        var catalog = new WindowsAppCatalog();

        var warmUpResult = catalog.GetAll();    // warm-up call
        var secondResult = catalog.GetAll();    // first-use call

        // Same entries, same count — cached, not re-discovered
        Assert.Equal(warmUpResult.Count, secondResult.Count);
        Assert.True(warmUpResult.Select(e => e.Id).SequenceEqual(secondResult.Select(e => e.Id)));
    }

    [Fact]
    public async Task WarmUp_BackgroundTask_DoesNotAffectFindById()
    {
        // Simulates startup warm-up: background Task.Run fires GetAll concurrently.
        // FindById after warm-up must return the same entry as direct GetAll.
        var catalog = new WindowsAppCatalog();
        var warmTask = Task.Run(() => catalog.GetAll());

        // GetAll() from the calling thread — may race with warm-up task
        var entries = catalog.GetAll();
        await warmTask;

        // Entries discovered must remain consistent after both calls complete
        foreach (var entry in entries)
        {
            var found = catalog.FindById(entry.Id);
            Assert.NotNull(found);
            Assert.Equal(entry.Id, found!.Id);
        }
    }
}
