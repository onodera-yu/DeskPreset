using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using DeskPreset.Core;
using DeskPreset.Core.Interop;

namespace DeskPreset.App;

public partial class AppPickerWindow : Window
{
    public enum Source { Windows, StartMenu }

    public sealed class Item(string name, string detail, string? exePath, string? appId, bool viaAppId, Func<AppEntry> create) : Observable
    {
        bool _isChecked;
        public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
        public string Name { get; } = name;
        public string Detail { get; } = detail;
        public string? ExePath { get; } = exePath;
        public string? AppId { get; } = appId;
        public bool ViaAppId { get; } = viaAppId;
        public Func<AppEntry> Create { get; } = create;
    }

    readonly List<Item> _items;
    readonly ICollectionView _view;

    public List<AppEntry> Result { get; } = [];

    public AppPickerWindow(Source source)
    {
        InitializeComponent();
        Heading.Text = source == Source.Windows ? "起動中のウィンドウから追加" : "インストール済みアプリから追加";
        _items = source == Source.Windows ? LoadWindows() : LoadStartMenu();
        _view = CollectionViewSource.GetDefaultView(_items);
        Items.ItemsSource = _view;
        Loaded += (_, _) => SearchBox.Focus();
    }

    static List<Item> LoadWindows()
    {
        var vds = App.Desktops;
        var index = vds?.GetDesktops().ToDictionary(d => d.Id, d => d.DisplayName) ?? [];
        var apps = PresetCapture.GetStartMenuApps();
        return WindowEnumerator.GetWindows(vds)
            .Select(w =>
            {
                var entry = PresetCapture.FromWindow(w, apps);
                var desktop = w.DesktopId is Guid id && index.TryGetValue(id, out var n) ? n : "";
                return new Item(entry.DisplayName, $"{desktop}  ·  {w.Title}", entry.ExePath, entry.AppUserModelId,
                                entry.LaunchViaAppId, () => entry.Clone());
            })
            .OrderBy(i => i.Detail, StringComparer.CurrentCulture)
            .ToList();
    }

    static List<Item> LoadStartMenu() =>
        PresetCapture.GetStartMenuApps()
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .OrderBy(kv => kv.Value, StringComparer.CurrentCulture)
            .Select(kv => new Item(kv.Value, kv.Key, null, kv.Key, true, () => ShellHelpers.EntryFromStartMenu(kv.Key, kv.Value)))
            .ToList();

    void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        _view.Filter = q.Length == 0 ? null : o => o is Item i
            && (i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.Detail.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var picked = _items.Where(i => i.IsChecked).ToList();
        // Nothing ticked: take the highlighted row so a double-click-like flow still works.
        if (picked.Count == 0 && Items.SelectedItem is Item selected) picked.Add(selected);
        Result.AddRange(picked.Select(i => i.Create()));
        DialogResult = true;
    }
}
