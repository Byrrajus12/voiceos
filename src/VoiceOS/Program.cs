using Microsoft.Extensions.Logging;
using VoiceOS.Core.Config;
using VoiceOS.UI;

namespace VoiceOS;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
#if DEBUG
        // The standalone UI proof can run beside the live tray app without touching its backend.
        if (args.Length == 1 && args[0] == "--glow-preview")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new GlowPreviewApplication());
            return;
        }
#endif

        using var mutex = new Mutex(initiallyOwned: true, VoiceOSProduct.SingleInstanceMutexName, out bool createdNew);
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

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var loggerFactory = BuildLoggerFactory();
        using var product = VoiceOSProduct.Build(loggerFactory);
        var orchestrator = product.Orchestrator;

        using var trayApp = new TrayApplication(orchestrator,
            loggerFactory.CreateLogger("VoiceOS.ProductUi"));
        Application.Run(trayApp);
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
}
