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

public sealed class RuleItem
{
    public string Exe { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public ImageSource? Icon { get; init; }
    public string Details => Path ?? Exe;
    public string RemoveLabel => $"Remove {Exe}";
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
