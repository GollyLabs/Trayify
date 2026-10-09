using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Trayify.Core;
using Trayify.Native;
using Trayify.UI;
using Windows.Graphics;

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

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _iconCache.Clear();
        RefreshAll();
    }
}
