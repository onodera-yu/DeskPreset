using System.Diagnostics;
using DeskPreset.Core.Interop;

namespace DeskPreset.Core;

public sealed record LaunchSummary(int Reused, int Launched, int Failed);

/// <summary>Reports immediately on the calling thread (Progress&lt;T&gt; posts asynchronously and can reorder or drop late messages).</summary>
public sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

/// <summary>Opens a preset: creates missing desktops, reuses or launches apps and moves their windows into place.</summary>
public sealed class PresetLauncher(VirtualDesktopService vds)
{
    sealed class Target(AppEntry entry, Guid desktopId, string desktopName)
    {
        public AppEntry Entry { get; } = entry;
        public Guid DesktopId { get; } = desktopId;
        public string DesktopName { get; } = desktopName;
        public IntPtr Window { get; set; }
        public bool Launched { get; set; }
    }

    /// <remarks>Must be awaited on the thread that owns <paramref name="vds"/> (no Task.Run).</remarks>
    public async Task<LaunchSummary> RunAsync(Preset preset, IProgress<string>? log = null, CancellationToken ct = default)
    {
        void Log(string s) => log?.Report(s);

        var desktops = EnsureDesktops(preset, Log);
        var targets = new List<Target>();
        for (int i = 0; i < preset.Desktops.Count; i++)
            foreach (var e in preset.Desktops[i].Apps)
                targets.Add(new Target(e, desktops[i].Id, desktops[i].DisplayName));

        var claimed = new HashSet<IntPtr>();
        var windows = WindowEnumerator.GetWindows(vds);
        int reused = 0, failed = 0;

        // 1) Windows that are already open.
        foreach (var t in targets.Where(t => t.Entry.ReuseExisting))
        {
            var w = windows.Where(w => !claimed.Contains(w.Handle) && t.Entry.Matches(w))
                           .OrderByDescending(w => w.DesktopId == t.DesktopId)
                           .FirstOrDefault();
            if (w == null) continue;
            Assign(t, w, claimed, Log);
            reused++;
        }

        // 2) Launch what is missing.
        var before = windows.Select(w => w.Handle).ToHashSet();
        var pending = new List<Target>();
        foreach (var t in targets.Where(t => t.Window == IntPtr.Zero))
        {
            if (!t.Entry.LaunchIfMissing)
            {
                Log($"スキップ: {t.Entry.DisplayName}（起動しない設定）");
                continue;
            }
            try
            {
                Start(t.Entry);
                t.Launched = true;
                pending.Add(t);
                Log($"起動: {t.Entry.DisplayName}");
            }
            catch (Exception ex)
            {
                failed++;
                Log($"起動失敗: {t.Entry.DisplayName} - {ex.Message}");
            }
            await Task.Delay(120, ct);
        }

        // 3) Wait for the new windows. A window that closes soon after appearing (splash screen) is re-assigned.
        var deadline = DateTime.Now.AddSeconds(Math.Max(3, preset.TimeoutSeconds));
        var settleUntil = DateTime.Now;
        while (DateTime.Now < deadline)
        {
            foreach (var t in targets.Where(t => t.Launched && t.Window != IntPtr.Zero && !Native.IsWindow(t.Window)).ToList())
            {
                claimed.Remove(t.Window);
                t.Window = IntPtr.Zero;
                pending.Add(t);
            }
            if (pending.Count == 0 && DateTime.Now >= settleUntil) break;

            await Task.Delay(300, ct);
            windows = WindowEnumerator.GetWindows(vds);
            foreach (var t in pending.ToList())
            {
                var w = windows.FirstOrDefault(w => !before.Contains(w.Handle) && !claimed.Contains(w.Handle) && t.Entry.Matches(w));
                if (w == null) continue;
                Assign(t, w, claimed, Log);
                pending.Remove(t);
                settleUntil = DateTime.Now.AddSeconds(1.5);
            }
        }
        foreach (var t in pending)
        {
            failed++;
            Log($"ウィンドウが見つかりません: {t.Entry.DisplayName}（タイムアウト）");
        }

        // 4) Apps often restore their own saved position right after showing; apply ours once more.
        foreach (var t in targets.Where(t => t.Launched && t.Window != IntPtr.Zero))
            Place(t, Log);

        if (preset.SwitchToDesktop is int idx && idx >= 0 && idx < desktops.Count)
            vds.SwitchTo(desktops[idx].Id);

        int launched = targets.Count(t => t.Launched && t.Window != IntPtr.Zero);
        Log($"完了: 再利用 {reused} / 起動 {launched} / 失敗 {failed}");
        return new LaunchSummary(reused, launched, failed);
    }

    IReadOnlyList<DesktopInfo> EnsureDesktops(Preset preset, Action<string> log)
    {
        var desktops = vds.GetDesktops();
        for (int n = desktops.Count; n < preset.Desktops.Count; n++)
        {
            vds.CreateDesktop();
            log($"デスクトップ {n + 1} を作成");
        }
        desktops = vds.GetDesktops();

        if (preset.RenameDesktops)
        {
            for (int i = 0; i < preset.Desktops.Count; i++)
            {
                var name = preset.Desktops[i].Name;
                if (string.IsNullOrWhiteSpace(name) || desktops[i].Name == name) continue;
                vds.Rename(desktops[i].Id, name);
            }
            desktops = vds.GetDesktops();
        }
        return desktops;
    }

    void Assign(Target t, WindowInfo w, HashSet<IntPtr> claimed, Action<string> log)
    {
        t.Window = w.Handle;
        claimed.Add(w.Handle);
        Place(t, log);
    }

    void Place(Target t, Action<string> log)
    {
        if (vds.GetWindowDesktopId(t.Window) != t.DesktopId && !vds.MoveWindow(t.Window, t.DesktopId))
            log($"移動できません: {t.Entry.DisplayName}（管理者権限のアプリの可能性）");
        if (t.Entry.RestorePosition && t.Entry.Bounds is { } b)
            WindowEnumerator.SetPlacement(t.Window, b, t.Entry.State);
    }

    public static void Start(AppEntry e)
    {
        var exe = PathPortability.Expand(e.ExePath);
        bool useAumid = !string.IsNullOrEmpty(e.AppUserModelId)
            && (e.LaunchViaAppId || string.IsNullOrEmpty(exe) || PresetCapture.IsPackagedPath(exe));
        if (useAumid)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{e.AppUserModelId}") { UseShellExecute = true })?.Dispose();
            return;
        }
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("実行ファイルが設定されていません");
        if (exe.Contains('*') || (Path.IsPathFullyQualified(exe) && !File.Exists(exe) && !Directory.Exists(exe)))
            throw new FileNotFoundException($"このPCに見つかりません: {exe}");

        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = PathPortability.ExpandArguments(e.Arguments) };
        if (!string.IsNullOrWhiteSpace(e.WorkingDirectory)) psi.WorkingDirectory = PathPortability.Expand(e.WorkingDirectory)!;
        else if (File.Exists(exe)) psi.WorkingDirectory = Path.GetDirectoryName(exe)!;
        Process.Start(psi)?.Dispose();
    }
}




