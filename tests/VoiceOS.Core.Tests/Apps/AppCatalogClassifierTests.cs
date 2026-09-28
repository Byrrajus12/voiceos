using VoiceOS.Core.Apps;
using Xunit;

namespace VoiceOS.Core.Tests.Apps;

/// <summary>Pure catalog classification: only launchable shortcuts become catalog apps.</summary>
public sealed class AppCatalogClassifierTests
{
    private const string ChromeProxy = @"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe";
    private const string YtMusicAppId = "cinhimbnkkaeohfgghhklpknlkffjgod";

    private static ShortcutFacts Exe(string path, string? args = null, bool console = false, string? aumid = null)
        => new(path, args, aumid, null, TargetIsFile: true, TargetIsDirectory: false, IsConsoleSubsystem: console);

    [Theory]
    // Regression controls: ordinary executable shortcuts.
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Microsoft VS Code\Code.exe")]
    [InlineData(@"C:\Program Files\Audacity\Audacity.exe")]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Obsidian\Obsidian.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    public void ExecutableShortcutsAreApps(string path)
        => Assert.Equal(ShortcutKind.Executable, AppCatalogClassifier.Classify(Exe(path)));

    [Fact]
    public void FolderShortcutIsRejected()
    {
        // "More....lnk" → C:\Program Files\Image-Line\Shared\Start (a folder).
        var facts = new ShortcutFacts(@"C:\Program Files\Image-Line\Shared\Start", null, null, null,
            TargetIsFile: false, TargetIsDirectory: true);
        Assert.Equal(ShortcutKind.Rejected, AppCatalogClassifier.Classify(facts));
    }

    [Theory]
    [InlineData(@"C:\Users\x\Python312\Doc\html\index.html")]
    [InlineData(@"C:\Users\x\7-Zip\7-zip.chm")]
    [InlineData(@"C:\Program Files (x86)\Windows Kits\10\Shortcuts\UWPAppsSamples.url")]
    public void DocumentShortcutsAreRejected(string path)
    {
        var facts = new ShortcutFacts(path, null, null, null, TargetIsFile: true, TargetIsDirectory: false);
        Assert.Equal(ShortcutKind.Rejected, AppCatalogClassifier.Classify(facts));
    }

    [Fact]
    public void MissingExecutableIsRejected()
    {
        var facts = new ShortcutFacts(@"C:\Gone\app.exe", null, null, null, TargetIsFile: false, TargetIsDirectory: false);
        Assert.Equal(ShortcutKind.Rejected, AppCatalogClassifier.Classify(facts));
    }

    [Theory]
    [InlineData($"--profile-directory=Default --app-id={YtMusicAppId}", ShortcutKind.WebApp)]
    [InlineData($"--app-id={YtMusicAppId} --profile-directory=Default", ShortcutKind.WebApp)]
    // Stale Sheets shortcut: the proxy without an app identity launches nothing.
    [InlineData(null, ShortcutKind.Rejected)]
    [InlineData("--profile-directory=Default", ShortcutKind.Rejected)]
    [InlineData("--app-id=", ShortcutKind.Rejected)]
    [InlineData("--app-id=not-a-real-id", ShortcutKind.Rejected)]
    public void BrowserProxyShortcutsNeedAValidAppId(string? args, ShortcutKind expected)
    {
        Assert.Equal(expected, AppCatalogClassifier.Classify(Exe(ChromeProxy, args)));
        Assert.Equal(expected, AppCatalogClassifier.Classify(
            Exe(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge_proxy.exe", args)));
    }

    [Fact]
    public void ConsoleExecutableIsTerminalHosted()
        => Assert.Equal(ShortcutKind.ConsoleExecutable, AppCatalogClassifier.Classify(
            Exe(@"C:\Users\x\Python312\python.exe", console: true)));

    [Theory]
    [InlineData("::{52205FD8-5DFB-447D-801A-D0B52F2E83E1}", null)]                         // File Explorer
    [InlineData("::{52205FD8-5DFB-447D-801A-D0B52F2E83E1}", "Microsoft.Windows.Explorer")]
    [InlineData("::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", null)]                         // Control Panel
    public void ShellFolderItemIsShellNamespaceEvenWithAumid(string parsingName, string? aumid)
    {
        var facts = new ShortcutFacts(null, null, aumid, parsingName, false, false);
        Assert.Equal(ShortcutKind.ShellNamespace, AppCatalogClassifier.Classify(facts));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://github.com/tesseract-ocr/tessdoc")]
    [InlineData(@"C:\Users\x\Documents")]
    [InlineData("::")]
    public void EmptyTargetWithoutShellFolderOrAumidIsRejected(string? parsingName)
    {
        var facts = new ShortcutFacts(null, null, null, parsingName, false, false);
        Assert.Equal(ShortcutKind.Rejected, AppCatalogClassifier.Classify(facts));
    }

    [Fact]
    public void AumidWithoutExecutableIsLaunchable()
    {
        Assert.Equal(ShortcutKind.Aumid, AppCatalogClassifier.Classify(
            new ShortcutFacts(null, null, "Contoso.App_abc!App", null, false, false)));
    }

    // ── PE subsystem ─────────────────────────────────────────────────────────

    private static byte[] Image(ushort subsystem, int peOffset = 0x80)
    {
        var image = new byte[peOffset + 4 + 20 + 96];
        image[0] = (byte)'M'; image[1] = (byte)'Z';
        BitConverter.GetBytes(peOffset).CopyTo(image, 0x3C);
        image[peOffset] = (byte)'P'; image[peOffset + 1] = (byte)'E';
        BitConverter.GetBytes(subsystem).CopyTo(image, peOffset + 4 + 20 + 68);
        return image;
    }

    [Fact]
    public void ReadsConsoleSubsystemFromPeHeader()
    {
        Assert.True(AppCatalogClassifier.IsConsoleSubsystem(Image(3)));   // WINDOWS_CUI
        Assert.False(AppCatalogClassifier.IsConsoleSubsystem(Image(2)));  // WINDOWS_GUI
        Assert.False(AppCatalogClassifier.IsConsoleSubsystem(new byte[8]));
        var truncated = Image(3)[..0x90];
        Assert.False(AppCatalogClassifier.IsConsoleSubsystem(truncated));
    }

    // ── Package manifests ────────────────────────────────────────────────────

    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Applications>
            <Application Id="App" Executable="PaintApp\mspaint.exe" EntryPoint="Windows.FullTrustApplication" />
            <Application Id="Helper" />
          </Applications>
        </Package>
        """;

    [Fact]
    public void ManifestDeclaresPackagedProcessName()
    {
        Assert.Equal("mspaint", AppCatalogClassifier.ManifestExecutableProcess(Manifest, "App"));
        Assert.Null(AppCatalogClassifier.ManifestExecutableProcess(Manifest, "Helper"));
        Assert.Null(AppCatalogClassifier.ManifestExecutableProcess(Manifest, "Missing"));
        Assert.Null(AppCatalogClassifier.ManifestExecutableProcess("<not xml", "App"));
    }

    // ── Deduplication by stable identity ─────────────────────────────────────

    [Fact]
    public void DeduplicatesByIdAndAumidInPrecedenceOrder()
    {
        var seededTerminal = new AppEntry("windows-terminal", "Windows Terminal", "WindowsTerminal",
            AppLaunchKind.PackagedApp, "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App");
        var packagedTerminal = new AppEntry("terminal", "Terminal", "WindowsTerminal",
            AppLaunchKind.PackagedApp, "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
            AppUserModelId: "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App");
        var calculator = new AppEntry("calculator", "Calculator", "CalculatorApp",
            AppLaunchKind.PackagedApp, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            AppUserModelId: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        var shortcutSpotify = new AppEntry("spotify", "Spotify", "Spotify", AppLaunchKind.Win32,
            @"C:\Users\x\AppData\Roaming\Spotify\Spotify.exe", AppUserModelId: "SpotifyAB.Spotify");
        var packagedSpotify = new AppEntry("spotify-music", "Spotify Music", null, AppLaunchKind.PackagedApp,
            "SpotifyAB.Spotify", AppUserModelId: "SpotifyAB.Spotify");
        var duplicateName = calculator with { LaunchTarget = "Other!App", AppUserModelId = "Other!App" };

        var entries = AppCatalogClassifier.Deduplicate(
            [shortcutSpotify, seededTerminal, packagedTerminal, calculator, packagedSpotify, duplicateName]);

        Assert.Equal(["spotify", "windows-terminal", "calculator"], entries.Select(e => e.Id));
        Assert.Same(seededTerminal, entries[1]);
    }
}
