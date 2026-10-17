using System.Collections.ObjectModel;
using DeskPreset.Core;
using DeskPreset.Core.Interop;

namespace DeskPreset.App;

public sealed record Option<T>(T Value, string Label);

public sealed class MainViewModel : Observable
{
    readonly PresetRepository _repo = new();
    readonly AutoSaver _saver;
    Preset? _selectedPreset;
    AppEntry? _selectedEntry;
    bool _isBusy;
    string _status = "";

    public MainViewModel()
    {
        try { Library = _repo.Load(); }
        catch (Exception ex)
        {
            Library = new PresetLibrary();
            _status = $"presets.json を読み込めませんでした: {ex.Message}";
        }
        _saver = new AutoSaver(_repo, Library);
        _saver.Saved += s => Status = s;
        SelectedPreset = Presets.FirstOrDefault();
    }

    public PresetLibrary Library { get; }
    public ObservableCollection<Preset> Presets => Library.Presets;
    public ObservableCollection<string> Log { get; } = [];
    public string DataDirectory => _repo.Directory;
    static VirtualDesktopService? Vds => App.Desktops;

    public string ApiInfo => Vds != null
        ? $"仮想デスクトップ API: {Vds.Profile.Name}"
        : $"仮想デスクトップ API 利用不可: {App.DesktopError}";

    public Preset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (_selectedPreset != null) _selectedPreset.Desktops.CollectionChanged -= DesktopsChanged;
            if (!Set(ref _selectedPreset, value)) return;
            if (value != null) value.Desktops.CollectionChanged += DesktopsChanged;
            SelectedEntry = null;
            OnPropertyChanged(nameof(SwitchOptions));
        }
    }

    void DesktopsChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(SwitchOptions));

    public AppEntry? SelectedEntry { get => _selectedEntry; set => Set(ref _selectedEntry, value); }
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;
    public string Status { get => _status; set => Set(ref _status, value); }

    public IReadOnlyList<Option<int?>> SwitchOptions =>
    [
        new(null, "切り替えない"),
        .. (SelectedPreset?.Desktops ?? []).Select(d => new Option<int?>(d.Index,
            string.IsNullOrEmpty(d.Name) ? d.Header : $"{d.Header}（{d.Name}）")),
    ];

    public static IReadOnlyList<Option<WindowShowState>> StateOptions { get; } =
    [
        new(WindowShowState.Normal, "通常"),
        new(WindowShowState.Maximized, "最大化"),
        new(WindowShowState.Minimized, "最小化"),
    ];

    // ---------------- presets ----------------

    string UniqueName(string baseName)
    {
        var name = baseName;
        for (int i = 2; Presets.Any(p => p.Name == name); i++) name = $"{baseName} ({i})";
        return name;
    }

    public void NewPreset()
    {
        var preset = new Preset { Name = UniqueName("新しいプリセット") };
        int count = Math.Max(1, Vds?.GetDesktops().Count ?? 1);
        for (int i = 0; i < count; i++) preset.Desktops.Add(new DesktopLayout());
        Presets.Add(preset);
        SelectedPreset = preset;
    }

    public bool CaptureNew()
    {
        if (!RequireApi()) return false;
        var preset = PresetCapture.Capture(Vds!, UniqueName($"キャプチャ {DateTime.Now:MM/dd HH:mm}"));
        Presets.Add(preset);
        SelectedPreset = preset;
        Status = $"{preset.Desktops.Sum(d => d.Apps.Count)} 個のウィンドウを取り込みました";
        return true;
    }

    public void RecaptureSelected()
    {
        if (SelectedPreset is not { } preset || !RequireApi()) return;
        var captured = PresetCapture.Capture(Vds!, preset.Name);
        SelectedEntry = null;
        // Replace contents in place so bindings and the autosave watcher keep working.
        preset.Desktops.Clear();
        foreach (var d in captured.Desktops) preset.Desktops.Add(d);
        Status = $"現在の配置で上書きしました（{captured.Desktops.Sum(d => d.Apps.Count)} ウィンドウ）";
    }

    public void DuplicateSelected()
    {
        if (SelectedPreset is not { } src) return;
        var copy = new Preset
        {
            Name = UniqueName(src.Name + " のコピー"),
            SwitchToDesktop = src.SwitchToDesktop,
            RenameDesktops = src.RenameDesktops,
            TimeoutSeconds = src.TimeoutSeconds,
        };
        foreach (var d in src.Desktops)
        {
            var layout = new DesktopLayout { Name = d.Name };
            foreach (var a in d.Apps) layout.Apps.Add(a.Clone());
            copy.Desktops.Add(layout);
        }
        Presets.Insert(Presets.IndexOf(src) + 1, copy);
        SelectedPreset = copy;
    }

    public void DeleteSelected()
    {
        if (SelectedPreset is not { } p) return;
        int i = Presets.IndexOf(p);
        Presets.Remove(p);
        SelectedPreset = Presets.Count == 0 ? null : Presets[Math.Min(i, Presets.Count - 1)];
    }

    public void Export(IReadOnlyList<Preset> presets, string path)
    {
        PresetRepository.WriteFile(path, new PresetLibrary { Presets = new(presets) });
        Status = $"{presets.Count} 件のプリセットを書き出しました: {path}";
    }

    public void Import(string path)
    {
        var imported = PresetRepository.ReadFile(path).Presets.ToList();
        foreach (var p in imported)
        {
            p.Name = UniqueName(p.Name);
            Presets.Add(p);
        }
        if (imported.Count > 0) SelectedPreset = imported[0];
        Status = $"{imported.Count} 件のプリセットを読み込みました";
    }

    public Task RunSelectedAsync() => SelectedPreset is { } p ? RunAsync(p) : Task.CompletedTask;

    async Task RunAsync(Preset preset)
    {
        if (!RequireApi() || IsBusy) return;
        IsBusy = true;
        Log.Add($"── {preset.Name} を起動 ({DateTime.Now:HH:mm:ss}) ──");
        try
        {
            var r = await new PresetLauncher(Vds!).RunAsync(preset, new SyncProgress<string>(Log.Add));
            Status = $"完了: 再利用 {r.Reused} / 起動 {r.Launched} / 失敗 {r.Failed}";
        }
        catch (Exception ex)
        {
            Log.Add("エラー: " + ex.Message);
            Status = "エラーが発生しました（ログを参照）";
        }
        finally { IsBusy = false; }
    }

    // ---------------- desktops ----------------

    public void AddDesktop() => SelectedPreset?.Desktops.Add(new DesktopLayout());

    public void RemoveDesktop(DesktopLayout layout)
    {
        if (SelectedEntry != null && layout.Apps.Contains(SelectedEntry)) SelectedEntry = null;
        SelectedPreset?.Desktops.Remove(layout);
    }

    public void ShowDesktop(DesktopLayout layout)
    {
        if (!RequireApi()) return;
        var desktops = Vds!.GetDesktops();
        if (layout.Index < desktops.Count) Vds.SwitchTo(desktops[layout.Index].Id);
        else Status = $"{layout.Header} はまだ存在しません（起動すると作成されます）";
    }

    // ---------------- entries ----------------

    public DesktopLayout? LayoutOf(AppEntry entry) => SelectedPreset?.Desktops.FirstOrDefault(d => d.Apps.Contains(entry));

    public void AddEntries(DesktopLayout layout, IEnumerable<AppEntry> entries)
    {
        foreach (var e in entries) layout.Apps.Add(e);
        SelectedEntry = layout.Apps.LastOrDefault();
    }

    public void RemoveEntry(AppEntry entry)
    {
        LayoutOf(entry)?.Apps.Remove(entry);
        if (SelectedEntry == entry) SelectedEntry = null;
    }

    public void DuplicateEntry(AppEntry entry)
    {
        if (LayoutOf(entry) is not { } layout) return;
        var copy = entry.Clone();
        layout.Apps.Insert(layout.Apps.IndexOf(entry) + 1, copy);
        SelectedEntry = copy;
    }

    public void MoveEntry(AppEntry entry, DesktopLayout target, int index)
    {
        if (LayoutOf(entry) is not { } source) return;
        int from = source.Apps.IndexOf(entry);
        if (source == target)
        {
            if (index > from) index--;
            index = Math.Clamp(index, 0, target.Apps.Count - 1);
            if (index != from) target.Apps.Move(from, index);
        }
        else
        {
            source.Apps.RemoveAt(from);
            target.Apps.Insert(Math.Clamp(index, 0, target.Apps.Count), entry);
        }
        SelectedEntry = entry;
    }

    /// <summary>Opens just this entry on its desktop, without touching the rest of the preset.</summary>
    public Task TestEntryAsync(AppEntry entry)
    {
        if (SelectedPreset is not { } p || LayoutOf(entry) is not { } layout) return Task.CompletedTask;
        var single = new Preset { Name = entry.DisplayName, SwitchToDesktop = null, RenameDesktops = false, TimeoutSeconds = p.TimeoutSeconds };
        for (int i = 0; i <= layout.Index; i++) single.Desktops.Add(new DesktopLayout());
        single.Desktops[layout.Index].Apps.Add(entry);
        return RunAsync(single);
    }

    /// <summary>Copies the position of the currently open window that matches the entry.</summary>
    public void TakeCurrentPosition(AppEntry entry)
    {
        var w = WindowEnumerator.GetWindows(Vds).FirstOrDefault(entry.Matches);
        if (w == null)
        {
            Status = $"「{entry.DisplayName}」のウィンドウが開いていません";
            return;
        }
        entry.Bounds = w.Bounds;
        entry.State = w.State;
        Status = $"位置を取得しました: {w.Title}";
    }

    public void Flush() => _saver.Flush();

    bool RequireApi()
    {
        if (Vds != null) return true;
        Status = ApiInfo;
        return false;
    }
}


