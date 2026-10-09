using System.Text;
using Microsoft.UI.Dispatching;
using Trayify.Native;

namespace Trayify.Core;

/// <summary>Wires settings, hooks, hidden-window bookkeeping, tray icons and the control pipe together.</summary>
public sealed class AppCore : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly CaptionButtonDetector _detector = new();
    private InputInterceptor? _interceptor;
    private TrayHost? _tray;
    private IpcServer? _ipc;
    private bool _shutDown;

    public AppSettings Settings { get; private set; } = SettingsStore.Load();
    public HiddenWindowManager Hidden { get; } = new();
    public HotkeyManager? Hotkeys { get; private set; }
    public event Action? SettingsChanged;

    public Action? ShowWindowRequested;
    public Action? QuitRequested;

    public AppCore(DispatcherQueue ui) { _ui = ui; }

    public void Start(bool spawnGuardian)
    {
        int recovered = Recovery.RestoreFromFile("startup");
        if (recovered > 0) Log.Warn($"Restored {recovered} window(s) left hidden by a previous run");

        _tray = new TrayHost
        {
            OpenRequested = () => ShowWindowRequested?.Invoke(),
            RestoreRequested = h => Hidden.Restore(h),
            RestoreAllRequested = () => Hidden.RestoreAll(),
            QuitRequested = () => QuitRequested?.Invoke(),
            GetRightClickEnabled = () => Settings.RightClickMinimize,
            RightClickToggled = on => Update(s => s.RightClickMinimize = on),
            GetHidden = () => Hidden.Hidden,
            Tick = Hidden.Prune,
        };
        Hotkeys = new HotkeyManager(_tray.Handle);
        _tray.HotkeyPressed = id => { if (Hotkeys.ExeForId(id) is { } exe) ToggleApp(exe); };
        Hidden.WindowHidden += hw => _tray.AddWindowIcon(hw);
        Hidden.WindowRemoved += hw => _tray.RemoveWindowIcon(hw);

        _interceptor = new InputInterceptor(_detector, (h, reason) => _ui.TryEnqueue(() => Hidden.Hide(h, reason)));
        ApplySettings();
        _interceptor.Start();

        _ipc = new IpcServer(HandleCommandAsync);
        _ipc.Start();

        if (spawnGuardian) Guardian.Spawn();
        Log.Info($"Trayify started (pid {Environment.ProcessId}, hooks={_interceptor.HooksInstalled}, rules={Settings.Rules.Count})");
    }

    private void ApplySettings()
    {
        if (_interceptor == null) return;
        _interceptor.SetRules(Settings.Rules.Select(r => r.Exe));
        _interceptor.RightClickMinimize = Settings.RightClickMinimize;
        _interceptor.AltF4ToTray = Settings.AltF4ToTray;
        _detector.UiaEnabled = Settings.UiaFallback;
        Hotkeys?.Apply(Settings.Rules);
    }

    public void Update(Action<AppSettings> change)
    {
        change(Settings);
        SettingsStore.Save(Settings);
        ApplySettings();
        SettingsChanged?.Invoke();
    }

    public bool HasRule(string exe) => Settings.Rules.Any(r => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));

    public void AddRule(string exe, string? path, string? displayName)
    {
        exe = exe.ToLowerInvariant();
        if (HasRule(exe)) return;
        Update(s => s.Rules.Add(new AppRule { Exe = exe, Path = path, DisplayName = displayName }));
        Log.Info($"Rule added: {exe}");
    }

    public void RemoveRule(string exe)
    {
        Update(s => s.Rules.RemoveAll(r => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)));
        Log.Info($"Rule removed: {exe}");
    }

    public void SetHotkey(string exe, Hotkey hk)
    {
        Update(s =>
        {
            foreach (var r in s.Rules.Where(r => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)))
            {
                r.HotkeyModifiers = hk.Modifiers;
                r.HotkeyKey = hk.Key;
            }
        });
        Log.Info($"Hotkey for {exe} set to '{hk}'");
    }

    /// <summary>
    /// Per-rule shortcut action:
    ///  - a window of the app is hidden in the tray  -> restore it (focused, prior position);
    ///  - the app's window is the active window      -> hide it to the tray;
    ///  - the app is open but minimized/behind others -> bring it to the front;
    ///  - the app isn't running                       -> nothing (logged).
    /// </summary>
    public string ToggleApp(string exe)
    {
        var hidden = Hidden.Hidden.LastOrDefault(h => h.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (hidden != null)
        {
            Hidden.Restore(hidden.Hwnd);
            return "restored";
        }

        var fg = N.GetForegroundWindow();
        var fgRoot = fg == IntPtr.Zero ? IntPtr.Zero : N.GetAncestor(fg, N.GA_ROOT);
        if (fgRoot != IntPtr.Zero && !N.IsIconic(fgRoot) && N.IsWindowVisible(fgRoot) &&
            exe.Equals(ProcessInfo.GetExeName(N.ProcessIdOf(fgRoot)), StringComparison.OrdinalIgnoreCase))
        {
            return Hidden.Hide(fgRoot, HideReason.Hotkey) != null ? "hidden" : "hide-failed";
        }

        var win = WindowUtil.GetAppWindows().FirstOrDefault(w => w.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (win != null)
        {
            if (N.IsIconic(win.Hwnd)) N.ShowWindow(win.Hwnd, N.SW_RESTORE);
            bool ok = WindowUtil.ForceForeground(win.Hwnd);
            Log.Info($"Hotkey: brought {exe} hwnd={win.Hwnd} to front ok={ok}");
            return "focused";
        }
        Log.Info($"Hotkey: {exe} has no open window");
        return "not-running";
    }

    private Task<T> OnUi<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } }))
            tcs.SetException(new InvalidOperationException("UI thread unavailable"));
        return tcs.Task;
    }

    /// <summary>Control/diagnostic commands (named pipe). See README "Control commands".</summary>
    private async Task<string> HandleCommandAsync(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        switch (cmd)
        {
            case "ping": return $"pong {Environment.ProcessId}";
            case "show": await OnUi(() => { ShowWindowRequested?.Invoke(); return 0; }); return "ok";
            case "quit": _ui.TryEnqueue(() => QuitRequested?.Invoke()); return "ok";
            case "list":
                return await OnUi(() => string.Join("\n", Hidden.Hidden.Select(h =>
                    $"{(long)h.Hwnd}\t{h.ExeName}\t{h.Reason}\t{h.Rect}\t{h.Title}")));
            case "restore":
                return await OnUi(() => Hidden.Restore((IntPtr)long.Parse(arg)) ? "ok" : "not hidden");
            case "restore-all": return await OnUi(() => $"restored {Hidden.RestoreAll()}");
            case "hide":
                return await OnUi(() => Hidden.Hide((IntPtr)long.Parse(arg), HideReason.Command) != null ? "ok" : "failed");
            case "pending":
                return $"close={_interceptor?.PendingClose ?? 0} min={_interceptor?.PendingMinimize ?? 0} intercepts={_interceptor?.Intercepts ?? 0}";
            case "rules": return await OnUi(() => string.Join("\n", Settings.Rules.Select(r => r.Exe)));
            case "add-rule": await OnUi(() => { AddRule(arg, null, null); return 0; }); return "ok";
            case "remove-rule": await OnUi(() => { RemoveRule(arg); return 0; }); return "ok";
            case "set":
            {
                var kv = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (kv.Length != 2) return "usage: set rightclick|altf4|uia on|off";
                bool on = kv[1] is "on" or "true" or "1";
                return await OnUi(() =>
                {
                    switch (kv[0].ToLowerInvariant())
                    {
                        case "rightclick": Update(s => s.RightClickMinimize = on); break;
                        case "altf4": Update(s => s.AltF4ToTray = on); break;
                        case "uia": Update(s => s.UiaFallback = on); break;
                        default: return "unknown setting";
                    }
                    return "ok";
                });
            }
            case "status":
            {
                var sb = new StringBuilder();
                sb.AppendLine($"pid={Environment.ProcessId} hooks={_interceptor?.HooksInstalled} intercepts={_interceptor?.Intercepts}");
                sb.AppendLine($"rightclick={Settings.RightClickMinimize} altf4={Settings.AltF4ToTray} uia={Settings.UiaFallback}");
                sb.AppendLine($"rules={string.Join(",", Settings.Rules.Select(r => r.Exe))}");
                sb.AppendLine($"hidden={Hidden.Hidden.Count} startupTask={StartupTask.IsEnabled(Settings.StartupTaskPath)} ({Settings.StartupTaskPath})");
                sb.AppendLine($"uiaCache={_detector.DescribeCache()}");
                sb.AppendLine("hotkeys:\n" + (Hotkeys?.Describe() ?? ""));
                return sb.ToString();
            }
            case "probe":
            {
                // probe <x> <y>: what would Trayify do with a click here?
                var xy = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var pt = new POINT(int.Parse(xy[0]), int.Parse(xy[1]));
                var root = N.RootAt(pt);
                var exe = ProcessInfo.GetExeName(N.ProcessIdOf(root));
                var kind = _detector.Classify(root, pt, out var how);
                bool rule = _interceptor?.HasRule(root) == true;
                return $"hwnd={(long)root} exe={exe} rule={rule} button={kind} via={how} " +
                       $"leftClickWouldTray={rule && kind == CaptionButton.Close} rightClickWouldTray={Settings.RightClickMinimize && kind == CaptionButton.Minimize}";
            }
            case "probe-uia":
            {
                var xy = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var pt = new POINT(int.Parse(xy[0]), int.Parse(xy[1]));
                return await Task.Run(() =>
                {
                    var uia = new Interop.UIAutomationClient.CUIAutomation8();
                    var root = N.RootAt(pt);
                    N.GetWindowRect(root, out var wr);
                    var trace = new StringBuilder();
                    var kind = CaptionButtonDetector.FindButtonAt(uia, pt, wr, out var r, out var name, trace);
                    return $"hwnd={(long)root} button={kind} name='{name}' rect={r} chain:{trace}";
                });
            }
            case "window":
                return await OnUi(() => App.Instance.DescribeMainWindow());
            case "startup":
            {
                // startup on|off|status (uses Settings.StartupTaskPath)
                return await OnUi(() =>
                {
                    string err = "";
                    if (arg == "on") StartupTask.Enable(Settings.StartupTaskPath, out err);
                    else if (arg == "off") StartupTask.Disable(Settings.StartupTaskPath, out err);
                    SettingsChanged?.Invoke();
                    return $"enabled={StartupTask.IsEnabled(Settings.StartupTaskPath)} {err}".Trim();
                });
            }
            case "hotkeys": return await OnUi(() => Hotkeys?.Describe() ?? "");
            case "set-hotkey":
            {
                // set-hotkey <exe (may contain spaces)> <combo|none>
                int cut = arg.LastIndexOf(' ');
                if (cut < 0) return "usage: set-hotkey <exe> <combo|none>";
                var exe = arg[..cut].Trim().ToLowerInvariant();
                if (!Hotkey.TryParse(arg[(cut + 1)..], out var hk)) return "bad combo";
                return await OnUi(() =>
                {
                    if (!HasRule(exe)) return "no such rule";
                    SetHotkey(exe, hk);
                    return Hotkeys?.ErrorFor(exe) ?? $"ok {hk}";
                });
            }
            case "toggle-app": return await OnUi(() => ToggleApp(arg.ToLowerInvariant()));
            case "set-startup-path":
                await OnUi(() => { Update(s => s.StartupTaskPath = arg); return 0; });
                return "ok";
            default:
                return "unknown command. commands: ping show quit list restore <hwnd> restore-all hide <hwnd> pending rules add-rule <exe> remove-rule <exe> set <rightclick|altf4|uia> <on|off> status probe <x> <y> probe-uia <x> <y> window startup <on|off|status> set-startup-path <path> hotkeys set-hotkey <exe> <combo|none> toggle-app <exe>";
        }
    }

    /// <summary>Clean shutdown: unhook, restore everything, remove icons, clear hidden.json.</summary>
    public void Dispose()
    {
        if (_shutDown) return;
        _shutDown = true;
        try { _interceptor?.Dispose(); } catch (Exception ex) { Log.Error("Interceptor dispose", ex); }
        try
        {
            int n = Hidden.RestoreAll(focusLast: true);
            Log.Info($"Shutdown: restored {n} window(s)");
        }
        catch (Exception ex) { Log.Error("RestoreAll on shutdown", ex); }
        Recovery.WriteFile(Array.Empty<HiddenRecord>());
        try { _tray?.Dispose(); } catch { }
        try { _ipc?.Dispose(); } catch { }
    }

    /// <summary>Last-chance path for unhandled exceptions (UI thread may be unusable).</summary>
    public static void EmergencyRestore(string why)
    {
        try { Recovery.RestoreFromFile(why); } catch { }
    }
}
