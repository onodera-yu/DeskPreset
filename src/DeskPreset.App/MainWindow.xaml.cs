using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskPreset.Core;
using Microsoft.Win32;

namespace DeskPreset.App;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();
    readonly List<ListBox> _appLists = [];
    Point _dragStart;
    AppEntry? _dragEntry;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.Log.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;
            LogExpander.IsExpanded = true;
            LogList.ScrollIntoView(_vm.Log[^1]);
        };
        InputBindings.Add(new KeyBinding(new RelayCommand(() => _ = _vm.RunSelectedAsync()), Key.F5, ModifierKeys.None));
        Closing += (_, _) => _vm.Flush();
    }

    static T? Ctx<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    bool Confirm(string message) =>
        MessageBox.Show(this, message, "DeskPreset", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    // ---------------- presets ----------------

    void NewPreset_Click(object sender, RoutedEventArgs e) => _vm.NewPreset();
    void CaptureNew_Click(object sender, RoutedEventArgs e) => _vm.CaptureNew();
    void DuplicatePreset_Click(object sender, RoutedEventArgs e) => _vm.DuplicateSelected();
    async void Run_Click(object sender, RoutedEventArgs e) => await _vm.RunSelectedAsync();

    async void PresetList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement((ListBox)sender, d) is ListBoxItem)
            await _vm.RunSelectedAsync();
    }

    void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPreset is { } p && Confirm($"プリセット「{p.Name}」を削除しますか？")) _vm.DeleteSelected();
    }

    void Recapture_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPreset is { } p && Confirm($"「{p.Name}」の内容を、今開いているウィンドウの配置で置き換えますか？"))
            _vm.RecaptureSelected();
    }

    void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        ShortcutButton.ContextMenu.PlacementTarget = ShortcutButton;
        ShortcutButton.ContextMenu.IsOpen = true;
    }

    void ShortcutDesktop_Click(object sender, RoutedEventArgs e) => CreateShortcut(startup: false);
    void ShortcutStartup_Click(object sender, RoutedEventArgs e) => CreateShortcut(startup: true);

    void CreateShortcut(bool startup)
    {
        if (_vm.SelectedPreset is not { } p) return;
        try
        {
            var path = ShellHelpers.CreateShortcut(p, startup);
            _vm.Status = $"ショートカットを作成しました: {path}";
        }
        catch (Exception ex)
        {
            _vm.Status = "ショートカットを作成できませんでした: " + ex.Message;
        }
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        ExportButton.ContextMenu.PlacementTarget = ExportButton;
        ExportButton.ContextMenu.IsOpen = true;
    }

    void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPreset is { } p) ExportTo([p], p.Name);
    }

    void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Presets.Count > 0) ExportTo([.. _vm.Presets], "DeskPreset-すべて");
    }

    void ExportTo(IReadOnlyList<Preset> presets, string suggestedName)
    {
        var dlg = new SaveFileDialog
        {
            Title = "プリセットを書き出し",
            Filter = "DeskPreset プリセット (*.deskpreset.json)|*.deskpreset.json|JSON (*.json)|*.json",
            FileName = string.Concat(suggestedName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".deskpreset.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try { _vm.Export(presets, dlg.FileName); }
        catch (Exception ex) { _vm.Status = "書き出しに失敗: " + ex.Message; }
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "プリセットを読み込み",
            Filter = "DeskPreset プリセット (*.json)|*.json|すべてのファイル (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try { _vm.Import(dlg.FileName); }
        catch (Exception ex) { _vm.Status = "読み込みに失敗: " + ex.Message; }
    }

    void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_vm.DataDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_vm.DataDirectory}\"") { UseShellExecute = true })?.Dispose();
    }

    // ---------------- desktops ----------------

    void AddDesktop_Click(object sender, RoutedEventArgs e) => _vm.AddDesktop();

    void RemoveDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx<DesktopLayout>(sender) is not { } d) return;
        if (d.Apps.Count == 0 || Confirm($"{d.Header} と {d.Apps.Count} 個のアプリをプリセットから削除しますか？"))
            _vm.RemoveDesktop(d);
    }

    void ShowDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx<DesktopLayout>(sender) is { } d) _vm.ShowDesktop(d);
    }

    void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var menu = (ContextMenu)FindResource("AddAppMenu");
        menu.DataContext = button.DataContext; // the DesktopLayout
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    void AddFromWindows_Click(object sender, RoutedEventArgs e) => PickApps(sender, AppPickerWindow.Source.Windows);
    void AddFromStartMenu_Click(object sender, RoutedEventArgs e) => PickApps(sender, AppPickerWindow.Source.StartMenu);

    void PickApps(object sender, AppPickerWindow.Source source)
    {
        if (Ctx<DesktopLayout>(sender) is not { } layout) return;
        var picker = new AppPickerWindow(source) { Owner = this };
        if (picker.ShowDialog() == true) _vm.AddEntries(layout, picker.Result);
    }

    void AddFromFile_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx<DesktopLayout>(sender) is not { } layout) return;
        var dlg = new OpenFileDialog
        {
            Title = "起動するアプリを選択",
            Filter = "アプリ / ショートカット (*.exe;*.lnk)|*.exe;*.lnk|すべてのファイル (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) _vm.AddEntries(layout, dlg.FileNames.Select(ShellHelpers.EntryFromFile));
    }

    // ---------------- entries ----------------

    void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedEntry is not { } entry) return;
        var dlg = new OpenFileDialog { Filter = "アプリ (*.exe)|*.exe|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) == true) entry.ExePath = PathPortability.Generalize(dlg.FileName);
    }

    AppEntry? EntryFrom(object sender) => Ctx<AppEntry>(sender) ?? _vm.SelectedEntry;

    async void EntryTest_Click(object sender, RoutedEventArgs e)
    {
        if (EntryFrom(sender) is { } entry) await _vm.TestEntryAsync(entry);
    }

    void EntryTakePosition_Click(object sender, RoutedEventArgs e)
    {
        if (EntryFrom(sender) is { } entry) _vm.TakeCurrentPosition(entry);
    }

    void EntryDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (EntryFrom(sender) is { } entry) _vm.DuplicateEntry(entry);
    }

    void EntryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (EntryFrom(sender) is { } entry) _vm.RemoveEntry(entry);
    }

    void AppList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && ((ListBox)sender).SelectedItem is AppEntry entry)
        {
            _vm.RemoveEntry(entry);
            e.Handled = true;
        }
    }

    // One selection across all desktop columns.
    void AppList_Loaded(object sender, RoutedEventArgs e) => _appLists.Add((ListBox)sender);
    void AppList_Unloaded(object sender, RoutedEventArgs e) => _appLists.Remove((ListBox)sender);

    void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var list = (ListBox)sender;
        if (list.SelectedItem is not AppEntry entry) return;
        foreach (var other in _appLists.Where(l => l != list)) other.SelectedItem = null;
        _vm.SelectedEntry = entry;
    }

    // ---------------- drag & drop between desktops ----------------

    void AppList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragEntry = ItemAt((ListBox)sender, e.OriginalSource)?.DataContext as AppEntry;
    }

    void AppList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragEntry == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var entry = _dragEntry;
        _dragEntry = null;
        DragDrop.DoDragDrop((ListBox)sender, new DataObject(typeof(AppEntry), entry), DragDropEffects.Move);
    }

    void AppList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(AppEntry)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void AppList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(AppEntry)) is not AppEntry entry) return;
        var list = (ListBox)sender;
        if (list.DataContext is not DesktopLayout target) return;

        int index = target.Apps.Count;
        if (ItemAt(list, e.OriginalSource) is { } item)
        {
            index = list.ItemContainerGenerator.IndexFromContainer(item);
            if (e.GetPosition(item).Y > item.ActualHeight / 2) index++;
        }
        _vm.MoveEntry(entry, target, index);
        list.SelectedItem = entry;
    }

    static ListBoxItem? ItemAt(ListBox list, object source)
    {
        for (var d = source as DependencyObject; d != null && d != list;
             d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ListBoxItem item) return item;
        return null;
    }
}

sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}



