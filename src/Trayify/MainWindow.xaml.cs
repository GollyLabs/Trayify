using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Trayify.Core;
using Trayify.Native;
using Trayify.UI;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace Trayify;

public sealed partial class MainWindow : Window
{
    private readonly AppCore _core;
    private readonly ObservableCollection<OpenWindowItem> _windows = new();
    private readonly ObservableCollection<RuleItem> _rules = new();
    private readonly ObservableCollection<HiddenItem> _hidden = new();
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _allowClose, _loading;
    private readonly IntPtr _hwnd;

    public MainWindow(AppCore core)
    {
        _core = core;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Paths.IconFile);
        AppWindow.Title = "Trayify";

        double scale = Math.Max(96u, N.GetDpiForWindow(_hwnd)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(760 * scale), (int)(860 * scale)));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));

        // Closing the window just hides it; Trayify keeps running in the tray.
        AppWindow.Closing += (s, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            s.Hide();
        };

        RulesList.ItemsSource = _rules;
        HiddenList.ItemsSource = _hidden;
        WindowsList.ItemsSource = _windows;

        _core.Hidden.Changed += () => { RefreshHidden(); RefreshWindows(); };
        _core.SettingsChanged += () => { LoadSettings(); RefreshRules(); SyncRuleFlags(); };
        if (_core.Hotkeys != null) _core.Hotkeys.StatusChanged += UpdateHotkeyErrors;
    }

    public void ShowAndActivate()
    {
        LoadSettings();
        RefreshAll();
        AppWindow.Show();
        Activate();
        WindowUtil.ForceForeground(_hwnd);
    }

    public void CloseForReal()
    {
        _allowClose = true;
        Close();
    }

    public string Describe() =>
        $"window visible={AppWindow.IsVisible} foreground={N.GetForegroundWindow() == _hwnd} hwnd={(long)_hwnd} " +
        $"windows={_windows.Count} rules={_rules.Count} hidden={_hidden.Count} rightclick={RightClickSwitch.IsOn} altf4={AltF4Switch.IsOn} uia={UiaSwitch.IsOn} startup={StartupSwitch.IsOn}";

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            RightClickSwitch.IsOn = _core.Settings.RightClickMinimize;
            AltF4Switch.IsOn = _core.Settings.AltF4ToTray;
            UiaSwitch.IsOn = _core.Settings.UiaFallback;
            StartupSwitch.IsOn = StartupTask.IsEnabled(_core.Settings.StartupTaskPath);
        }
        finally { _loading = false; }
    }

    private void RefreshAll() { RefreshRules(); RefreshHidden(); RefreshWindows(); }

    private ImageSource? Icon(string key, IntPtr hwnd, string? exePath)
    {
        if (_iconCache.TryGetValue(key, out var img)) return img;
        double scale = Math.Max(96u, N.GetDpiForWindow(_hwnd)) / 96.0;
        img = IconImage.ForWindow(hwnd, exePath, (int)Math.Round(24 * scale));
        _iconCache[key] = img;
        return img;
    }

    private void RefreshRules()
    {
        _rules.Clear();
        foreach (var r in _core.Settings.Rules)
            _rules.Add(new RuleItem
            {
                Exe = r.Exe,
                Name = r.DisplayName ?? r.Exe,
                Path = r.Path,
                Icon = Icon(r.Path ?? r.Exe, IntPtr.Zero, r.Path),
                Hotkey = new Hotkey(r.HotkeyModifiers, r.HotkeyKey).ToString(),
                Error = _core.Hotkeys?.ErrorFor(r.Exe) ?? "",
            });
        NoRulesText.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshHidden()
    {
        _hidden.Clear();
        foreach (var h in _core.Hidden.Hidden)
            _hidden.Add(new HiddenItem
            {
                Hwnd = h.Hwnd,
                Title = h.Title,
                Details = $"{h.ExeName}  ·  {h.Reason switch { HideReason.CloseButton => "closed to tray", HideReason.MinimizeRightClick => "right-click minimize", HideReason.AltF4 => "Alt+F4", _ => "sent to tray" }}  ·  {h.HiddenAt:t}",
                Icon = Icon(h.ExePath ?? h.ExeName, h.Hwnd, h.ExePath),
            });
        NoHiddenText.Visibility = _hidden.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RestoreAllButton.IsEnabled = _hidden.Count > 0;
        HiddenHeader.Text = _hidden.Count == 0 ? "In the tray" : $"In the tray ({_hidden.Count})";
    }

    private void RefreshWindows()
    {
        _windows.Clear();
        foreach (var w in WindowUtil.GetAppWindows().OrderBy(w => w.ExeName).ThenBy(w => w.Title))
            _windows.Add(new OpenWindowItem
            {
                Hwnd = w.Hwnd, Title = w.Title, ExeName = w.ExeName, ExePath = w.ExePath, Pid = w.Pid,
                Icon = Icon(w.ExePath ?? w.ExeName, w.Hwnd, w.ExePath),
                IsRule = _core.HasRule(w.ExeName),
            });
    }

    private void SyncRuleFlags()
    {
        foreach (var w in _windows) w.IsRule = _core.HasRule(w.ExeName);
    }

    // ---- handlers ----
    private void OnRightClickToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) _core.Update(s => s.RightClickMinimize = RightClickSwitch.IsOn);
    }

    private void OnAltF4Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) _core.Update(s => s.AltF4ToTray = AltF4Switch.IsOn);
    }

    private void OnUiaToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) _core.Update(s => s.UiaFallback = UiaSwitch.IsOn);
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var path = _core.Settings.StartupTaskPath;
        bool ok = StartupSwitch.IsOn ? StartupTask.Enable(path, out var err) : StartupTask.Disable(path, out err);
        if (!ok) StartupDetails.Text = $"Could not update the scheduled task: {err}";
        LoadSettings();
    }

    private void OnRuleToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts || ts.DataContext is not OpenWindowItem item) return;
        if (ts.IsOn == _core.HasRule(item.ExeName)) return; // programmatic sync
        if (ts.IsOn) _core.AddRule(item.ExeName, item.ExePath, FriendlyName(item));
        else _core.RemoveRule(item.ExeName);
    }

    private static string FriendlyName(OpenWindowItem item)
    {
        try
        {
            if (item.ExePath != null)
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(item.ExePath);
                if (!string.IsNullOrWhiteSpace(vi.FileDescription)) return vi.FileDescription!;
                if (!string.IsNullOrWhiteSpace(vi.ProductName)) return vi.ProductName!;
            }
        }
        catch { }
        return Path.GetFileNameWithoutExtension(item.ExeName);
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string exe }) _core.RemoveRule(exe);
    }

    private void OnRestoreOne(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long h }) _core.Hidden.Restore((IntPtr)h);
    }

    private void OnRestoreAll(object sender, RoutedEventArgs e) => _core.Hidden.RestoreAll();

    private void OnSendToTray(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long h }) _core.Hidden.Hide((IntPtr)h, HideReason.Manual);
    }

    // ---- shortcut recorder ----
    private RuleItem? _recording;

    private void UpdateHotkeyErrors()
    {
        foreach (var r in _rules) r.Error = _core.Hotkeys?.ErrorFor(r.Exe) ?? "";
    }

    private RuleItem? RuleFor(object sender) =>
        sender is FrameworkElement { Tag: string exe } ? _rules.FirstOrDefault(r => r.Exe == exe) : null;

    private void OnRecordHotkey(object sender, RoutedEventArgs e)
    {
        var item = RuleFor(sender);
        if (item == null || item.IsRecording) return;
        StopRecording();
        _recording = item;
        item.IsRecording = true;
        item.Hint = "Press the new shortcut (Esc cancels, Backspace clears).";
        _core.Hotkeys?.Suspend(); // so pressing the current combo doesn't trigger it
        ((Control)sender).Focus(FocusState.Programmatic);
    }

    private void StopRecording()
    {
        if (_recording == null) return;
        _recording.IsRecording = false;
        _recording.Hint = "";
        _recording = null;
        _core.Hotkeys?.Resume();
        UpdateHotkeyErrors();
    }

    private void OnRecorderLostFocus(object sender, RoutedEventArgs e)
    {
        if (_recording != null && _recording == RuleFor(sender)) StopRecording();
    }

    private static bool Down(VirtualKey k) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    private void OnRecorderKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var item = RuleFor(sender);
        if (item == null || !item.IsRecording) return;
        e.Handled = true;
        var key = e.Key;
        if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.Shift or VirtualKey.LeftShift
            or VirtualKey.RightShift or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.LeftWindows or VirtualKey.RightWindows)
            return; // wait for the actual key

        uint mods = 0;
        if (Down(VirtualKey.Control)) mods |= Hotkey.MOD_CONTROL;
        if (Down(VirtualKey.Menu)) mods |= Hotkey.MOD_ALT;
        if (Down(VirtualKey.Shift)) mods |= Hotkey.MOD_SHIFT;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) mods |= Hotkey.MOD_WIN;

        if (mods == 0 && key == VirtualKey.Escape) { StopRecording(); return; }
        if (mods == 0 && key is VirtualKey.Back or VirtualKey.Delete)
        {
            var exe = item.Exe;
            StopRecording();
            _core.SetHotkey(exe, default);
            return;
        }
        bool isFKey = key >= VirtualKey.F1 && key <= VirtualKey.F24;
        if (mods == 0 && !isFKey)
        {
            item.Hint = "Use at least one modifier (Ctrl, Alt, Shift or Win), or an F-key.";
            return;
        }
        var hk = new Hotkey(mods, (uint)key);
        var target = item.Exe;
        StopRecording();
        _core.SetHotkey(target, hk); // re-registers; failures show up as the rule's red message
    }

    private void OnClearHotkey(object sender, RoutedEventArgs e)
    {
        if (RuleFor(sender) is { } item) _core.SetHotkey(item.Exe, default);
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _iconCache.Clear();
        RefreshAll();
    }
}
