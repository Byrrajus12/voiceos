using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Activation;
using VoiceOS.Core.Audio;
using VoiceOS.Core.Config;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Speech;

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

        var hookLogger = loggerFactory.CreateLogger<GlobalKeyboardHook>();
        var audioLogger = loggerFactory.CreateLogger<AudioCaptureService>();
        var writerLogger = loggerFactory.CreateLogger<RecordingDebugWriter>();
        var orchestratorLogger = loggerFactory.CreateLogger<ActivationOrchestrator>();

        var vkCode = ResolveVirtualKey(config.ActivationKey);
        var hook = new GlobalKeyboardHook(vkCode, hookLogger);
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
        var apiKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var jevLogger = loggerFactory.CreateLogger<TypeSafeJevDecisionEngine>();
            decisionEngine = new TypeSafeJevDecisionEngine(
                apiKey,
                new HttpClient(),
                config.TypeSafeModel,
                config.JevCommandThreshold,
                config.JevActionThreshold,
                jevLogger);
        }
        else
        {
            var startupLogger = loggerFactory.CreateLogger("VoiceOS.Startup");
            startupLogger.LogWarning("TYPESAFE_API_KEY not set — Jev decision engine disabled.");
        }

        var orchestrator = new ActivationOrchestrator(
            hook, audio, debugWriter, config, orchestratorLogger,
            speechRecognizer, decisionEngine);

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
        _ => 0xA3                 // Default to Right Ctrl
    };
}
