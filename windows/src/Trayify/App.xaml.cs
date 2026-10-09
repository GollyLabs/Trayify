using Microsoft.UI.Xaml;
using Trayify.Core;

namespace Trayify;

public partial class App : Application
{
    public static App Instance { get; private set; } = null!;
    public AppCore Core { get; private set; } = null!;
    private readonly string[] _args;
    private MainWindow? _window;
    private bool _quitting;

    public App(string[] args)
    {
        Instance = this;
        _args = args;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true; // keep running; a tray utility should not die on a UI glitch
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        Core = new AppCore(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread())
        {
            ShowWindowRequested = ShowMainWindow,
            QuitRequested = Quit,
        };
        Core.Start(spawnGuardian: !_args.Contains("--no-guardian", StringComparer.OrdinalIgnoreCase));

        // First run (no rules yet) or explicit --show: open the window. Otherwise start quietly in the tray.
        if (_args.Contains("--show", StringComparer.OrdinalIgnoreCase) ||
            (Core.Settings.Rules.Count == 0 && !_args.Contains("--startup", StringComparer.OrdinalIgnoreCase) && !_args.Contains("--tray", StringComparer.OrdinalIgnoreCase)))
            ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        if (_quitting) return;
        _window ??= new MainWindow(Core);
        _window.ShowAndActivate();
    }

    public string DescribeMainWindow() => _window?.Describe() ?? "window=not-created";

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        Log.Info("Quit requested");
        Core.Dispose();
        _window?.CloseForReal();
        Exit();
    }
}
