using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Activation;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Audio;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Config;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Dictation;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Monitors;
using VoiceOS.Core.Speech;
using VoiceOS.Core.Windows;

namespace VoiceOS;

internal static class Program
{
    private const string MutexName = "Global\\VoiceOS-SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "VoiceOS is already running.",
                "VoiceOS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        DotEnvLoader.Load();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var config = LoadConfig();
        using var loggerFactory = BuildLoggerFactory();

        using var chromeCompanion = new ChromeCompanionTransport(
            loggerFactory.CreateLogger<ChromeCompanionTransport>());
        chromeCompanion.Start();

        var hookLogger = loggerFactory.CreateLogger<GlobalKeyboardHook>();
        var audioLogger = loggerFactory.CreateLogger<AudioCaptureService>();
        var writerLogger = loggerFactory.CreateLogger<RecordingDebugWriter>();
        var orchestratorLogger = loggerFactory.CreateLogger<ActivationOrchestrator>();

        // ── App catalog ───────────────────────────────────────────────────────────
        var catalogLogger = loggerFactory.CreateLogger<WindowsAppCatalog>();
        IAppCatalog catalog = new WindowsAppCatalog(catalogLogger);
        // Warm up the catalog in the background so the first utterance doesn't pay discovery cost.
        // Runs concurrently with Parakeet initialization below.
        Task.Run(() => catalog.GetAll());

        var commandVkCode = ResolveVirtualKey(config.ActivationKey);
        var dictationVkCode = ResolveVirtualKey(config.DictationActivationKey);
        if (commandVkCode == dictationVkCode)
            throw new InvalidOperationException("Command and dictation activation keys must be different.");

        var commandHook = new GlobalKeyboardHook(commandVkCode, hookLogger);
        var dictationHook = new GlobalKeyboardHook(dictationVkCode, hookLogger);
        var audio = new AudioCaptureService(audioLogger);
        var debugWriter = config.DebugOutputEnabled
            ? new RecordingDebugWriter(writerLogger)
            : null;

        ISpeechRecognizer? speechRecognizer = null;
        var modelDir = ResolvePath(config.ModelDirectory);
        var (modelValid, missingFiles) = ModelManager.ValidateModelFiles(modelDir);
        if (modelValid)
        {
            var sttLogger = loggerFactory.CreateLogger<ParakeetSpeechRecognizer>();
            speechRecognizer = new ParakeetSpeechRecognizer(modelDir, sttLogger);
        }
        else
        {
            var startupLogger = loggerFactory.CreateLogger("VoiceOS.Startup");
            startupLogger.LogWarning(
                "Parakeet model not found in {Dir} — missing: {Files}. STT disabled.\n{Instructions}",
                modelDir, string.Join(", ", missingFiles), ModelManager.DownloadInstructions);
        }

        IDecisionEngine? decisionEngine = null;
        ICommandRouter? commandRouter = null;
        IBrowserInteractionService? browserInteraction = null;
        var apiKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var jevHttp = new HttpClient();
            var jevLogger = loggerFactory.CreateLogger<TypeSafeJevDecisionEngine>();
            decisionEngine = new TypeSafeJevDecisionEngine(
                apiKey,
                jevHttp,
                config.TypeSafeModel,
                config.JevCommandThreshold,
                config.JevActionThreshold,
                jevLogger);
            var gateway = new TypeSafeJevGateway(
                apiKey, config.TypeSafeModel, jevHttp,
                loggerFactory.CreateLogger<TypeSafeJevGateway>());
            commandRouter = new TypeSafeCommandRouter(gateway,
                logger: loggerFactory.CreateLogger<TypeSafeCommandRouter>());
            browserInteraction = new BrowserInteractionService(chromeCompanion, gateway,
                new OpenRouterBrowserGoalNormalizer(new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
                    Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")),
                loggerFactory.CreateLogger<BrowserInteractionService>(),
                new BrowserTextValueResolver(new GroundedBrowserTextValueResolver(),
                    new OpenRouterBrowserTextValueResolver(new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
                        Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))));
        }
        else
        {
            var startupLogger = loggerFactory.CreateLogger("VoiceOS.Startup");
            startupLogger.LogWarning("TYPESAFE_API_KEY not set — Jev decision engine disabled.");
        }

        // ── Execution layer ───────────────────────────────────────────────────────
        var windows = new WindowService(loggerFactory.CreateLogger<WindowService>());
        var windowMover = new WindowMoveService(loggerFactory.CreateLogger<WindowMoveService>());
        IDisplayTopologyService topoService = new DisplayTopologyService(loggerFactory.CreateLogger<DisplayTopologyService>());
        var media = new MediaService(loggerFactory.CreateLogger<MediaService>());
        var volume = new VolumeService(loggerFactory.CreateLogger<VolumeService>());

        var windowAwareLauncher = new WindowAwareLauncher(
            catalog, windows, loggerFactory.CreateLogger<WindowAwareLauncher>(),
            newInstanceArgsProvider: new ChromeNewInstanceArgsProvider());

        var programExecutor = new ProgramExecutor(
            windowAwareLauncher, catalog, windows, media, volume, windowMover, topoService,
            loggerFactory.CreateLogger<ProgramExecutor>());

        // Literal dictation bypasses semantic interpretation and uses one trusted
        // foreground insertion service with exact target/focus verification.
        var foreground = new WindowsForegroundWindowService();
        var keyboard = new WindowsKeyboardInputService();
        using var clipboard = new WindowsClipboardService(
            loggerFactory.CreateLogger<WindowsClipboardService>());
        var textInsertion = new TextInsertionService([
            new ClipboardPasteTextInsertionBackend(clipboard, keyboard, foreground),
            new UnicodeKeyboardTextInsertionBackend(keyboard, foreground)
        ]);

        var orchestrator = new ActivationOrchestrator(
            commandHook, dictationHook, audio, debugWriter, config, orchestratorLogger,
            speechRecognizer, decisionEngine, programExecutor, catalog, topoService,
            foreground, textInsertion, commandRouter, browserInteraction, chromeCompanion,
            windowAwareLauncher);

        using var trayApp = new TrayApplication(orchestrator);
        Application.Run(trayApp);
    }

    private static string ResolvePath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    private static VoiceOSConfig LoadConfig()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        return configuration.GetSection("VoiceOS").Get<VoiceOSConfig>() ?? new VoiceOSConfig();
    }

    private static ILoggerFactory BuildLoggerFactory()
    {
        return LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddSimpleConsole(opts =>
                {
                    opts.SingleLine = true;
                    opts.IncludeScopes = true;
                    opts.TimestampFormat = "HH:mm:ss.fff ";
                });
        });
    }

    private static int ResolveVirtualKey(string keyName) => keyName switch
    {
        "RControlKey" => 0xA3,   // VK_RCONTROL
        "LControlKey" => 0xA2,   // VK_LCONTROL
        "RShiftKey"   => 0xA1,   // VK_RSHIFT
        "LShiftKey"   => 0xA0,   // VK_LSHIFT
        "RAltKey"     => 0xA5,   // VK_RMENU
        "LAltKey"     => 0xA4,   // VK_LMENU
        "F8"          => 0x77,   // VK_F8
        "F9"          => 0x78,   // VK_F9
        "F10"         => 0x79,   // VK_F10
        _ => 0xA3                 // Default to Right Ctrl
    };
}
