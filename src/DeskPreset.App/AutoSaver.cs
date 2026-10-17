using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using DeskPreset.Core;

namespace DeskPreset.App;

/// <summary>Saves the library shortly after any preset, desktop or app entry changes.</summary>
sealed class AutoSaver
{
    readonly PresetRepository _repo;
    readonly PresetLibrary _library;
    readonly DispatcherTimer _timer;

    public event Action<string>? Saved;

    public AutoSaver(PresetRepository repo, PresetLibrary library)
    {
        _repo = repo;
        _library = library;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => Flush();

        Watch(library.Presets);
        foreach (var p in library.Presets) Watch(p);
    }

    public void Flush()
    {
        _timer.Stop();
        try
        {
            _repo.Save(_library);
            Saved?.Invoke($"保存しました {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            Saved?.Invoke("保存に失敗: " + ex.Message);
        }
    }

    void Changed()
    {
        _timer.Stop();
        _timer.Start();
    }

    void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesktopLayout.Index) or nameof(DesktopLayout.Header)) return;
        Changed();
    }

    void Watch(INotifyCollectionChanged collection) => collection.CollectionChanged += (_, e) =>
    {
        foreach (var item in e.NewItems ?? Array.Empty<object>()) Watch(item);
        Changed();
    };

    void Watch(object item)
    {
        if (item is INotifyPropertyChanged npc)
        {
            npc.PropertyChanged -= OnPropertyChanged; // items moved between lists are re-added
            npc.PropertyChanged += OnPropertyChanged;
        }
        switch (item)
        {
            case Preset p:
                Watch(p.Desktops);
                foreach (var d in p.Desktops) Watch(d);
                break;
            case DesktopLayout d:
                Watch(d.Apps);
                foreach (var a in d.Apps) Watch(a);
                break;
        }
    }
}
