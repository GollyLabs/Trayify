using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Trayify.Core;
using Trayify.Native;

namespace Trayify;

public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    public static int Main(string[] args)
    {
        var first = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        // Guardian (watchdog) mode: no UI at all.
        if (first == "--guardian") return Guardian.Run(args);

        // CLI control: Trayify.exe --cmd <command...>  (prints the reply of the running instance)
        if (first == "--cmd")
        {
            N.AttachConsole(-1);
            var reply = IpcClient.Send(string.Join(' ', args.Skip(1)));
            Console.WriteLine(reply ?? "Trayify is not running.");
            return reply == null ? 1 : 0;
        }

        // Manual recovery: Trayify.exe --recover (restores windows listed in hidden.json)
        if (first == "--recover") return Recovery.RestoreFromFile("manual") >= 0 ? 0 : 1;

        using var mutex = new Mutex(true, @"Local\Trayify.SingleInstance.v1", out bool createdNew);
        if (!createdNew)
        {
            // Already running: bring up its window instead.
            IpcClient.Send("show");
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception (process terminating)", e.ExceptionObject as Exception);
            AppCore.EmergencyRestore("crash");
        };

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(args);
        });
        GC.KeepAlive(mutex);
        return 0;
    }
}
