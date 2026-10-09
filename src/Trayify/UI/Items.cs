using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace Trayify.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class OpenWindowItem : Observable
{
    public IntPtr Hwnd { get; init; }
    public long Id => (long)Hwnd;
    public string Title { get; init; } = "";
    public string ExeName { get; init; } = "";
    public string? ExePath { get; init; }
    public uint Pid { get; init; }
    public ImageSource? Icon { get; init; }
    public string Details => $"{ExeName}  ·  PID {Pid}";
    public string RuleLabel => $"Close to tray: {ExeName}";
    public string SendLabel => $"Send to tray now: {Title}";
    private bool _isRule;
    public bool IsRule { get => _isRule; set => Set(ref _isRule, value); }
}

public sealed class RuleItem : Observable
{
    public string Exe { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public ImageSource? Icon { get; init; }
    public string Details => Path ?? Exe;
    public string RemoveLabel => $"Remove {Exe}";
    public string ShortcutLabel => $"Shortcut for {Exe}";
    public string ClearShortcutLabel => $"Clear shortcut for {Exe}";

    private string _hotkey = "";
    /// <summary>Saved shortcut text, e.g. "Ctrl+G" ("" = none).</summary>
    public string Hotkey { get => _hotkey; set { Set(ref _hotkey, value); Raise(); } }

    private bool _recording;
    public bool IsRecording { get => _recording; set { Set(ref _recording, value); Raise(); } }

    private string _hint = "";
    /// <summary>Recorder hint ("Use at least one modifier...").</summary>
    public string Hint { get => _hint; set { Set(ref _hint, value); Raise(); } }

    private string _error = "";
    /// <summary>Registration failure (combo already taken), shown in red.</summary>
    public string Error { get => _error; set { Set(ref _error, value); Raise(); } }

    public string ShortcutButtonText => IsRecording ? "Press a shortcut…" : Hotkey.Length > 0 ? Hotkey : "Set shortcut";
    public Microsoft.UI.Xaml.Visibility ClearVisibility => Hotkey.Length > 0 && !IsRecording ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string Message => Error.Length > 0 ? Error : Hint;
    public Microsoft.UI.Xaml.Visibility MessageVisibility => Message.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private void Raise()
    {
        OnChanged(nameof(ShortcutButtonText));
        OnChanged(nameof(ClearVisibility));
        OnChanged(nameof(Message));
        OnChanged(nameof(MessageVisibility));
    }
}

public sealed class HiddenItem
{
    public IntPtr Hwnd { get; init; }
    public long Id => (long)Hwnd;
    public string Title { get; init; } = "";
    public string Details { get; init; } = "";
    public string RestoreLabel => $"Restore {Title}";
    public ImageSource? Icon { get; init; }
}
