using System.Windows;

namespace ConnectionChecker;

public partial class App : System.Windows.Application
{
    /// <summary>Start hidden in the tray (--minimized / /min).</summary>
    public static bool StartMinimized { get; private set; }

    /// <summary>1-based monitor for the alert overlay (--monitor N); null = primary.</summary>
    public static int? MonitorOverride { get; private set; }

    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance per user (Global\ + username spans sessions of the same user).
        _instanceMutex = new Mutex(true, $"Global\\PulseWatch.{Environment.UserName}", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "PULSE//WATCH is already running — look for the cyan ring in the system tray.",
                "PULSE//WATCH", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ParseArgs(e.Args);

        var window = new MainWindow();
        MainWindow = window; // keeps ShutdownMode=OnMainWindowClose working even when hidden
        if (!StartMinimized)
            window.Show();
    }

    private static void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].TrimStart('-', '/').ToLowerInvariant();

            if (arg is "minimized" or "minimised" or "min")
            {
                StartMinimized = true;
            }
            else if (arg.StartsWith("monitor"))
            {
                // Accept "--monitor 2", "--monitor=2", "/monitor:2"
                var value = arg.Length > "monitor".Length
                    ? arg["monitor".Length..].TrimStart('=', ':')
                    : (i + 1 < args.Length ? args[++i] : "");
                if (int.TryParse(value, out var n) && n >= 1)
                    MonitorOverride = n;
            }
        }
    }
}
