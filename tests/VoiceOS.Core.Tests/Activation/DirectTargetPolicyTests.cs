using VoiceOS.Core.Activation;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using Xunit;

namespace VoiceOS.Core.Tests.Activation;

public sealed class DirectTargetPolicyTests
{
    private static readonly AppEntry Chrome = new("google-chrome", "Google Chrome", "chrome",
        AppLaunchKind.Win32, @"C:\Program Files\Google\Chrome\Application\chrome.exe");
    private static readonly AppEntry YouTubeMusicPwa = new("youtube-music", "YouTube Music", "chrome_proxy",
        AppLaunchKind.Win32, @"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe",
        "--profile-directory=Default --app-id=cinhimbnkkaeohfgghhklpknlkffjgod");
    private static readonly AppEntry Spotify = new("spotify", "Spotify", "Spotify",
        AppLaunchKind.Win32, @"C:\Users\x\AppData\Roaming\Spotify\Spotify.exe");

    private static readonly IAppCatalog Catalog = new FakeCatalog([Chrome, YouTubeMusicPwa, Spotify]);

    private static CommandRouteDecision Route(RequestedEntityKind entity,
        SemanticDestinationKind destination = SemanticDestinationKind.None,
        SurfacePreference preference = SurfacePreference.Unspecified, string? service = null)
        => new(CommandRoute.DirectCapability, .9, DestinationKind: destination,
            DestinationName: service ?? (destination == SemanticDestinationKind.KnownService ? "Spotify" : null),
            SurfacePreference: preference, RequestedEntity: entity);

    private static (VoicePlan, VoiceProgram) Open(string appId)
    {
        var plan = new VoicePlan(VoiceAction.OpenApp, AppCandidateId: appId, Confidence: .9);
        return (plan, new VoiceProgram([new OpenAppStep("s1", new AppTarget(appId))]));
    }

    [Theory]
    [InlineData(RequestedEntityKind.NamedEntity, SemanticDestinationKind.KnownService, SurfacePreference.Unspecified, DirectTargetVerdict.RerouteToBrowser)]
    [InlineData(RequestedEntityKind.Uncertain, SemanticDestinationKind.KnownService, SurfacePreference.Unspecified, DirectTargetVerdict.RerouteToBrowser)]
    [InlineData(RequestedEntityKind.NamedEntity, SemanticDestinationKind.None, SurfacePreference.Browser, DirectTargetVerdict.RerouteToBrowser)]
    // An unregistered entity has no typed web representation: not installed, never web discovery.
    [InlineData(RequestedEntityKind.NamedEntity, SemanticDestinationKind.None, SurfacePreference.Unspecified, DirectTargetVerdict.NativeUnavailable)]
    public void GenericBrowserHostDoesNotSatisfyAnotherRequestedEntity(RequestedEntityKind entity,
        SemanticDestinationKind destination, SurfacePreference preference, DirectTargetVerdict expected)
    {
        var (plan, program) = Open(Chrome.Id);
        Assert.Equal(expected,
            DirectTargetPolicy.Evaluate(Route(entity, destination, preference), plan, program, Catalog));
    }

    [Fact]
    public void ExplicitNativeRequestNeverFallsBackToChrome()
    {
        var (plan, program) = Open(Chrome.Id);
        Assert.Equal(DirectTargetVerdict.NativeUnavailable, DirectTargetPolicy.Evaluate(
            Route(RequestedEntityKind.NamedEntity, preference: SurfacePreference.Native), plan, program, Catalog));
    }

    [Theory]
    [InlineData(RequestedEntityKind.BrowserItself)]
    [InlineData(RequestedEntityKind.Uncertain)]
    [InlineData(RequestedEntityKind.None)]
    public void OpeningTheBrowserItselfIsUnchanged(RequestedEntityKind entity)
    {
        var (plan, program) = Open(Chrome.Id);
        Assert.Equal(DirectTargetVerdict.Proceed,
            DirectTargetPolicy.Evaluate(Route(entity), plan, program, Catalog));
    }

    [Theory]
    [InlineData("youtube-music", "YouTube Music")]
    [InlineData("spotify", "Spotify")]
    public void InstalledAppOrPwaRepresentingTheEntityProceeds(string appId, string service)
    {
        var (plan, program) = Open(appId);
        Assert.Equal(DirectTargetVerdict.Proceed, DirectTargetPolicy.Evaluate(
            Route(RequestedEntityKind.NamedEntity, SemanticDestinationKind.KnownService, service: service),
            plan, program, Catalog));
    }

    [Theory]
    // Registered service with no installed app (e.g. "Open Spotify" → target_app=none): its web representation.
    [InlineData(SemanticDestinationKind.KnownService, SurfacePreference.Unspecified, DirectTargetVerdict.RerouteToBrowser)]
    [InlineData(SemanticDestinationKind.KnownService, SurfacePreference.Browser, DirectTargetVerdict.RerouteToBrowser)]
    [InlineData(SemanticDestinationKind.KnownService, SurfacePreference.Native, DirectTargetVerdict.NativeUnavailable)]
    // Unregistered entity with no installed app (e.g. "Open Final Cut Pro"): not installed.
    [InlineData(SemanticDestinationKind.None, SurfacePreference.Unspecified, DirectTargetVerdict.NativeUnavailable)]
    [InlineData(SemanticDestinationKind.None, SurfacePreference.Native, DirectTargetVerdict.NativeUnavailable)]
    [InlineData(SemanticDestinationKind.None, SurfacePreference.Browser, DirectTargetVerdict.RerouteToBrowser)]
    public void UnresolvedOpenAppForNamedEntity(SemanticDestinationKind destination,
        SurfacePreference preference, DirectTargetVerdict expected)
    {
        var plan = new VoicePlan(VoiceAction.OpenApp, RequiresClarification: true);
        Assert.Equal(expected, DirectTargetPolicy.Evaluate(
            Route(RequestedEntityKind.NamedEntity, destination, preference, "Gmail"), plan, null,
            new FakeCatalog([Chrome, More, VsCode])));
    }

    [Theory]
    [InlineData(SurfacePreference.Unspecified, DirectTargetVerdict.RerouteToBrowser)]
    [InlineData(SurfacePreference.Browser, DirectTargetVerdict.RerouteToBrowser)]
    [InlineData(SurfacePreference.Native, DirectTargetVerdict.NativeUnavailable)]
    public void RegisteredServiceRejectsUnrelatedAppWhateverThePreference(SurfacePreference preference,
        DirectTargetVerdict expected)
    {
        // "Open Spotify" where the forced choice picked a folder shortcut: never a substitute.
        var (plan, program) = Open(More.Id);
        Assert.Equal(expected, DirectTargetPolicy.Evaluate(
            Route(RequestedEntityKind.NamedEntity, SemanticDestinationKind.KnownService, preference, "Spotify"),
            plan, program, new FakeCatalog([Chrome, More, VsCode])));
    }

    [Theory]
    [InlineData("visual-studio-code")]
    [InlineData("more")]
    public void UnregisteredEntityKeepsTheDirectDecisionsInstalledApp(string appId)
    {
        // Unregistered entities carry no typed name; target_app (with its none option) decides.
        var (plan, program) = Open(appId);
        Assert.Equal(DirectTargetVerdict.Proceed, DirectTargetPolicy.Evaluate(
            Route(RequestedEntityKind.NamedEntity), plan, program, new FakeCatalog([Chrome, More, VsCode])));
    }

    [Fact]
    public void WindowOperationsOnBrowserWindowsAreUnaffected()
    {
        var plan = new VoicePlan(VoiceAction.MaximizeCurrentWindow, Confidence: .9);
        var program = new VoiceProgram([new MaximizeWindowStep("s1", new AppTarget(Chrome.Id))]);
        Assert.Equal(DirectTargetVerdict.Proceed, DirectTargetPolicy.Evaluate(
            Route(RequestedEntityKind.NamedEntity), plan, program, Catalog));
    }

    [Fact]
    public void BrowserHostDetectionExcludesPwaProxies()
    {
        Assert.True(DirectTargetPolicy.IsGenericBrowserHost(Chrome));
        Assert.False(DirectTargetPolicy.IsGenericBrowserHost(YouTubeMusicPwa));
        Assert.False(DirectTargetPolicy.IsGenericBrowserHost(Spotify));
        Assert.False(DirectTargetPolicy.IsGenericBrowserHost(Chrome with
            { LaunchArguments = "--app-id=abc" }));
    }

    // ── Explicit Native: the opened app must represent the requested entity ──

    private static readonly AppEntry More = new("more", "More...", "SystemSettings",
        AppLaunchKind.PackagedApp, "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel");
    private static readonly AppEntry InstagramPwa = new("instagram", "Instagram", "chrome_proxy",
        AppLaunchKind.Win32, @"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe",
        "--profile-directory=Default --app-id=akpamiohjfcnimfljfndmaldlcfphjmp");
    private static readonly AppEntry VsCode = new("visual-studio-code", "Visual Studio Code", "Code",
        AppLaunchKind.Win32, @"C:\Users\x\AppData\Local\Programs\Microsoft VS Code\Code.exe");
    private static readonly AppEntry GitHubDesktop = new("github-desktop", "GitHub Desktop", "GitHubDesktop",
        AppLaunchKind.Win32, @"C:\Users\x\AppData\Local\GitHubDesktop\GitHubDesktop.exe");

    private static CommandRouteDecision NativeService(string service)
        => new(CommandRoute.DirectCapability, .9, DestinationKind: SemanticDestinationKind.KnownService,
            DestinationName: service, SurfacePreference: SurfacePreference.Native,
            RequestedEntity: RequestedEntityKind.NamedEntity, GoalShape: GoalShape.SurfaceOnly,
            EndState: SemanticEndState.SurfaceReady);

    [Fact]
    public void ExplicitNativeRejectsUnrelatedAppWhenEntityIsNotInstalled()
    {
        var (plan, program) = Open(More.Id);
        Assert.Equal(DirectTargetVerdict.NativeUnavailable, DirectTargetPolicy.Evaluate(
            NativeService("Instagram"), plan, program, new FakeCatalog([Chrome, More, VsCode])));
    }

    [Fact]
    public void ExplicitNativeRejectsUnrelatedAppEvenWhenEntityIsInstalled()
    {
        var (plan, program) = Open(More.Id);
        Assert.Equal(DirectTargetVerdict.NativeMismatch, DirectTargetPolicy.Evaluate(
            NativeService("Instagram"), plan, program, new FakeCatalog([Chrome, More, InstagramPwa])));
    }

    [Theory]
    [InlineData("Instagram", "instagram")]
    [InlineData("YouTube Music", "youtube-music")]
    [InlineData("Spotify", "spotify")]
    [InlineData("GitHub", "github-desktop")]
    public void ExplicitNativeAcceptsInstalledRepresentationIncludingChromeHostedPwa(string service, string appId)
    {
        var (plan, program) = Open(appId);
        Assert.Equal(DirectTargetVerdict.Proceed, DirectTargetPolicy.Evaluate(NativeService(service), plan, program,
            new FakeCatalog([Chrome, More, InstagramPwa, YouTubeMusicPwa, Spotify, GitHubDesktop])));
    }

    [Fact]
    public void ExplicitNativeDoesNotAcceptAMoreSpecificSiblingService()
    {
        var (plan, program) = Open(YouTubeMusicPwa.Id);
        Assert.Equal(DirectTargetVerdict.NativeUnavailable, DirectTargetPolicy.Evaluate(
            NativeService("YouTube"), plan, program, Catalog));
    }

    [Fact]
    public void ExplicitNativeCompoundNeedsOneRepresentationAmongOpenedApps()
    {
        var catalog = new FakeCatalog([More, InstagramPwa, VsCode]);
        var plan = new VoicePlan(VoiceAction.OpenApp, Confidence: .9);
        VoiceProgram Program(string first) => new([new OpenAppStep("s1", new AppTarget(first)),
            new OpenAppStep("s2", new AppTarget(VsCode.Id))]);
        Assert.Equal(DirectTargetVerdict.Proceed,
            DirectTargetPolicy.Evaluate(NativeService("Instagram"), plan, Program(InstagramPwa.Id), catalog));
        Assert.Equal(DirectTargetVerdict.NativeMismatch,
            DirectTargetPolicy.Evaluate(NativeService("Instagram"), plan, Program(More.Id), catalog));
    }

    [Theory]
    [InlineData(SurfacePreference.Native)]
    [InlineData(SurfacePreference.Unspecified)]
    public void ArbitraryInstalledAppOutsideTheServiceRegistryStillOpens(SurfacePreference preference)
    {
        var (plan, program) = Open(VsCode.Id);
        Assert.Equal(DirectTargetVerdict.Proceed, DirectTargetPolicy.Evaluate(
            Route(RequestedEntityKind.NamedEntity, preference: preference), plan, program,
            new FakeCatalog([Chrome, VsCode])));
    }

    // ── Explicit tab wording: browser lane when the direct lane has nothing to execute ──

    private static CommandRouteDecision Tab(TabDisposition disposition,
        SurfacePreference preference = SurfacePreference.Browser,
        RequestedEntityKind entity = RequestedEntityKind.BrowserItself)
        => new(CommandRoute.DirectCapability, .9, TabDisposition: disposition, SurfacePreference: preference,
            RequestedEntity: entity, GoalShape: GoalShape.SurfaceOnly, EndState: SemanticEndState.SurfaceReady);

    [Theory]
    [InlineData(TabDisposition.NewTab, SurfacePreference.Browser)]
    [InlineData(TabDisposition.NewTab, SurfacePreference.Unspecified)]
    [InlineData(TabDisposition.ExistingNamedTab, SurfacePreference.Browser)]
    [InlineData(TabDisposition.CurrentTab, SurfacePreference.Browser)]
    public void TabRequestWithoutDirectProgramGoesToBrowserLane(TabDisposition disposition,
        SurfacePreference preference)
    {
        var plan = new VoicePlan(VoiceAction.None);
        Assert.Equal(DirectTargetVerdict.RerouteToBrowser,
            DirectTargetPolicy.Evaluate(Tab(disposition, preference), plan, null, Catalog));
    }

    [Fact]
    public void NewBrowserWindowDirectProgramIsNotStolenByTabDisposition()
    {
        var plan = new VoicePlan(VoiceAction.OpenApp, AppCandidateId: Chrome.Id, Confidence: .9);
        var program = new VoiceProgram([new OpenAppStep("s1", new AppTarget(Chrome.Id), AppActivationMode.NewInstance)]);
        Assert.Equal(DirectTargetVerdict.Proceed,
            DirectTargetPolicy.Evaluate(Tab(TabDisposition.NewTab), plan, program, Catalog));
    }

    [Fact]
    public void DirectFailureWithoutTabWordingIsUnchanged()
    {
        var plan = new VoicePlan(VoiceAction.None);
        Assert.Equal(DirectTargetVerdict.Proceed, DirectTargetPolicy.Evaluate(
            Tab(TabDisposition.Unspecified, SurfacePreference.Unspecified, RequestedEntityKind.None), plan, null, Catalog));
        Assert.Equal(DirectTargetVerdict.Proceed, DirectTargetPolicy.Evaluate(
            Tab(TabDisposition.NewTab, SurfacePreference.Native), plan, null, Catalog));
    }

    private sealed class FakeCatalog(IReadOnlyList<AppEntry> entries) : IAppCatalog
    {
        public IReadOnlyList<AppEntry> GetAll() => entries;
        public AppEntry? FindById(string id) => entries.FirstOrDefault(e => e.Id == id);
    }
}
