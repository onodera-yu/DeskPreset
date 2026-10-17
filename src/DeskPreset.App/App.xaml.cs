using System.IO;
using System.Windows;
using DeskPreset.Core;
using DeskPreset.Core.Interop;

namespace DeskPreset.App;

public partial class App : Application
{
    /// <summary>Shell connection; null when this Windows build is not supported.</summary>
    public static VirtualDesktopService? Desktops { get; private set; }
    public static string? DesktopError { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int run = Array.FindIndex(e.Args, a => a is "--run" or "/run");
        if (run >= 0 && run + 1 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await RunHeadlessAsync(e.Args[run + 1]);
            Shutdown();
            return;
        }

        ConnectDesktops();
        new MainWindow().Show();
    }

    static bool ConnectDesktops()
    {
        try
        {
            Desktops = new VirtualDesktopService();
            DesktopError = null;
            return true;
        }
        catch (Exception ex)
        {
            DesktopError = ex.Message;
            return false;
        }
    }

    /// <summary>`DeskPreset.exe --run "name"`: opens a preset without showing the editor (for shortcuts / startup).</summary>
    static async Task RunHeadlessAsync(string name)
    {
        var repo = new PresetRepository();
        var preset = repo.Load().Presets.FirstOrDefault(p => p.Name == name);
        if (preset == null)
        {
            MessageBox.Show($"プリセット「{name}」が見つかりません。", "DeskPreset", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // At sign-in explorer.exe may not be ready yet.
        for (int i = 0; i < 15 && !ConnectDesktops(); i++) await Task.Delay(2000);
        if (Desktops == null)
        {
            MessageBox.Show($"仮想デスクトップに接続できません: {DesktopError}", "DeskPreset", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var lines = new List<string> { $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {name}" };
        try
        {
            await new PresetLauncher(Desktops).RunAsync(preset, new SyncProgress<string>(lines.Add));
        }
        catch (Exception ex)
        {
            lines.Add("エラー: " + ex.Message);
        }
        Directory.CreateDirectory(repo.Directory);
        File.WriteAllLines(Path.Combine(repo.Directory, "last-run.log"), lines);
    }
}

