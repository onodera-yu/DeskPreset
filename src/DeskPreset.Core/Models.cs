using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using DeskPreset.Core.Interop;

namespace DeskPreset.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Preset : Observable
{
    string _name = "新しいプリセット";
    int? _switchToDesktop = 0;
    bool _renameDesktops = true;
    int _timeoutSeconds = 20;

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>0-based desktop to show after launching; null keeps the current one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] // null is meaningful here
    public int? SwitchToDesktop { get => _switchToDesktop; set => Set(ref _switchToDesktop, value); }

    /// <summary>Apply the layout's desktop names to the real virtual desktops.</summary>
    public bool RenameDesktops { get => _renameDesktops; set => Set(ref _renameDesktops, value); }

    /// <summary>How long to wait for launched apps to show their window.</summary>
    public int TimeoutSeconds { get => _timeoutSeconds; set => Set(ref _timeoutSeconds, value); }

    ObservableCollection<DesktopLayout> _desktops = [];

    public ObservableCollection<DesktopLayout> Desktops
    {
        get => _desktops;
        set
        {
            _desktops.CollectionChanged -= Reindex;
            _desktops = value;
            _desktops.CollectionChanged += Reindex;
            Reindex(null, null);
        }
    }

    public Preset() => _desktops.CollectionChanged += Reindex;

    void Reindex(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs? e)
    {
        for (int i = 0; i < _desktops.Count; i++) _desktops[i].Index = i;
    }
}

public sealed class DesktopLayout : Observable
{
    string _name = "";
    int _index;

    public string Name { get => _name; set => Set(ref _name, value); }
    public ObservableCollection<AppEntry> Apps { get; set; } = [];

    /// <summary>Position within the preset; maintained by the UI, not persisted.</summary>
    [JsonIgnore] public int Index { get => _index; set { if (Set(ref _index, value)) OnPropertyChanged(nameof(Header)); } }
    [JsonIgnore] public string Header => $"デスクトップ {Index + 1}";
}

public sealed class AppEntry : Observable
{
    string _displayName = "";
    string? _exePath, _arguments, _workingDirectory, _appUserModelId, _matchProcessName, _titleContains, _capturedTitle;
    bool _launchIfMissing = true, _reuseExisting = true, _restorePosition = true, _launchViaAppId;
    WindowBounds? _bounds;
    WindowShowState _state;

    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }

    /// <summary>Program, document, .lnk or URL to start.</summary>
    public string? ExePath { get => _exePath; set { if (Set(ref _exePath, value)) OnPropertyChanged(nameof(Subtitle)); } }
    public string? Arguments { get => _arguments; set => Set(ref _arguments, value); }
    public string? WorkingDirectory { get => _workingDirectory; set => Set(ref _workingDirectory, value); }

    /// <summary>Store / packaged apps are started through their AppUserModelID instead of an exe.</summary>
    public string? AppUserModelId { get => _appUserModelId; set { if (Set(ref _appUserModelId, value)) OnPropertyChanged(nameof(Subtitle)); } }

    /// <summary>Start through shell:AppsFolder (Store apps, browser PWAs) instead of ExePath.</summary>
    public bool LaunchViaAppId { get => _launchViaAppId; set => Set(ref _launchViaAppId, value); }

    /// <summary>Process that owns the window when it differs from ExePath (launchers such as Update.exe).</summary>
    public string? MatchProcessName { get => _matchProcessName; set => Set(ref _matchProcessName, value); }

    /// <summary>Only windows whose title contains this text count as this app's window.</summary>
    public string? TitleContains { get => _titleContains; set => Set(ref _titleContains, value); }

    /// <summary>Window title at capture time, for reference only.</summary>
    public string? CapturedTitle { get => _capturedTitle; set => Set(ref _capturedTitle, value); }

    public bool LaunchIfMissing { get => _launchIfMissing; set => Set(ref _launchIfMissing, value); }
    public bool ReuseExisting { get => _reuseExisting; set => Set(ref _reuseExisting, value); }
    public bool RestorePosition { get => _restorePosition; set => Set(ref _restorePosition, value); }
    public WindowBounds? Bounds
    {
        get => _bounds;
        set
        {
            if (!Set(ref _bounds, value)) return;
            OnPropertyChanged(nameof(Left)); OnPropertyChanged(nameof(Top));
            OnPropertyChanged(nameof(Width)); OnPropertyChanged(nameof(Height));
        }
    }

    // Editable views of Bounds for the GUI.
    [JsonIgnore] public int Left { get => Bounds?.Left ?? 0; set => Bounds = (Bounds ?? DefaultBounds) with { Left = value }; }
    [JsonIgnore] public int Top { get => Bounds?.Top ?? 0; set => Bounds = (Bounds ?? DefaultBounds) with { Top = value }; }
    [JsonIgnore] public int Width { get => Bounds?.Width ?? 0; set => Bounds = (Bounds ?? DefaultBounds) with { Width = value }; }
    [JsonIgnore] public int Height { get => Bounds?.Height ?? 0; set => Bounds = (Bounds ?? DefaultBounds) with { Height = value }; }
    static readonly WindowBounds DefaultBounds = new(100, 100, 1200, 800);

    public WindowShowState State { get => _state; set => Set(ref _state, value); }

    [JsonIgnore]
    public string Subtitle => !string.IsNullOrEmpty(AppUserModelId) && string.IsNullOrEmpty(ExePath)
        ? AppUserModelId!
        : Path.GetFileName(ExePath ?? "");

    public AppEntry Clone() =>
        System.Text.Json.JsonSerializer.Deserialize<AppEntry>(System.Text.Json.JsonSerializer.Serialize(this))!;

    public bool Matches(WindowInfo w)
    {
        if (!string.IsNullOrEmpty(TitleContains)
            && !w.Title.Contains(TitleContains, StringComparison.CurrentCultureIgnoreCase)) return false;

        // Different app IDs on the same exe (e.g. a Chrome PWA vs. a normal Chrome window) are different apps.
        if (!string.IsNullOrEmpty(AppUserModelId) && !string.IsNullOrEmpty(w.AppUserModelId)
            && !string.Equals(AppUserModelId, w.AppUserModelId, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(MatchProcessName))
            return string.Equals(Path.GetFileName(MatchProcessName), w.ExeName, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(AppUserModelId)
            && string.Equals(AppUserModelId, w.AppUserModelId, StringComparison.OrdinalIgnoreCase)) return true;
        // Packaged Win32 apps (e.g. new Outlook) often have no app ID on the window: match on the package folder.
        if (AppUserModelId?.IndexOf('!') is > 0 and var bang && string.IsNullOrEmpty(w.AppUserModelId)
            && string.Equals(PresetCapture.PackageFamilyFromPath(w.ExePath), AppUserModelId[..bang], StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(ExePath) && !string.IsNullOrEmpty(w.ExePath))
            return string.Equals(Path.GetFileName(ExePath), w.ExeName, StringComparison.OrdinalIgnoreCase);
        return false;
    }
}

public sealed class PresetLibrary
{
    public int Version { get; set; } = 1;
    public ObservableCollection<Preset> Presets { get; set; } = [];
}



