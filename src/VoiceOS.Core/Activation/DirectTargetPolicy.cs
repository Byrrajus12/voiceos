using VoiceOS.Core.Apps;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;

namespace VoiceOS.Core.Activation;

public enum DirectTargetVerdict { Proceed, RerouteToBrowser, NativeUnavailable, NativeMismatch }

/// <summary>
/// Keeps the requested entity and the execution surface separate. A direct program may only
/// claim an "open X" request when it opens X itself: opening the generic browser host, an
/// unrelated app, or failing to resolve any app leaves a named entity unsatisfied.
/// A registered service must be represented by the opened app whatever the stated preference;
/// otherwise its web representation owns it (or, under explicit Native, it clarifies). An
/// unregistered entity with no installed app clarifies as not installed: nothing typed
/// identifies a web representation, so web discovery needs an explicit Browser preference.
/// </summary>
public static class DirectTargetPolicy
{
    // Browser executables, not their PWA proxies (chrome_proxy/msedge_proxy host installed web apps).
    private static readonly HashSet<string> BrowserHostProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };

    public static bool IsGenericBrowserHost(AppEntry? app)
        => app?.ProcessName is { } process && BrowserHostProcesses.Contains(process)
            && app.LaunchArguments?.Contains("--app-id", StringComparison.OrdinalIgnoreCase) != true;

    /// <summary>An installed app (including a PWA) that is the service itself, not its browser host.</summary>
    public static bool Represents(ServiceDescriptor service, AppEntry? app)
        => app is not null && !IsGenericBrowserHost(app) && ServiceResolver.IsRepresentedBy(service, app.DisplayName);

    public static DirectTargetVerdict Evaluate(CommandRouteDecision route, VoicePlan plan,
        VoiceProgram? program, IAppCatalog? catalog)
    {
        // Explicit tab wording addresses a browser surface. No offered native capability creates
        // or selects a tab, so when the direct lane produced nothing to execute the browser lane
        // owns it. A direct program (e.g. a new browser window) is left untouched.
        if (program is null && route.TabDisposition != TabDisposition.Unspecified
            && route.SurfacePreference != SurfacePreference.Native)
            return DirectTargetVerdict.RerouteToBrowser;
        if (!route.RequestsNamedEntity) return DirectTargetVerdict.Proceed;
        var opened = program?.Steps.OfType<OpenAppStep>()
            .Select(step => catalog?.FindById(step.App.AppCandidateId)).ToArray() ?? [];
        var hostSubstituted = opened.Any(IsGenericBrowserHost);
        var openUnresolved = program is null && plan.Action == VoiceAction.OpenApp;
        // A registered entity carries a typed name: an opened app must be one of its installed
        // representations regardless of surface preference. Unregistered entities carry no typed
        // name to check against, so an arbitrary installed app stays with the direct decision
        // (which may answer none).
        var service = route.DestinationKind == SemanticDestinationKind.KnownService
            ? ServiceResolver.Resolve(route.DestinationName) : null;
        var unrelated = service is not null && opened.Length > 0
            && !opened.Any(app => Represents(service, app));
        if (!hostSubstituted && !openUnresolved && !unrelated) return DirectTargetVerdict.Proceed;
        if (route.SurfacePreference == SurfacePreference.Native)
            return service is not null && catalog?.GetAll().Any(app => Represents(service, app)) == true
                ? DirectTargetVerdict.NativeMismatch : DirectTargetVerdict.NativeUnavailable;
        // A registered service has a trusted web representation; an explicit web preference
        // requests web discovery. Otherwise the named entity is simply not installed.
        return service is not null || route.SurfacePreference == SurfacePreference.Browser
            ? DirectTargetVerdict.RerouteToBrowser : DirectTargetVerdict.NativeUnavailable;
    }
}
