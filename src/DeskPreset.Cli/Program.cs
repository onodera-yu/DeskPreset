using DeskPreset.Core;
using DeskPreset.Core.Interop;

namespace DeskPreset.Cli;

static class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        WindowEnumerator.EnablePerMonitorDpi();
        var cmd = args.FirstOrDefault()?.ToLowerInvariant();
        try
        {
            using var vds = new VirtualDesktopService();
            var repo = new PresetRepository();
            switch (cmd)
            {
                case "desktops":
                    Console.WriteLine($"API: {vds.Profile.Name}");
                    var current = vds.GetCurrentDesktopId();
                    foreach (var d in vds.GetDesktops())
                        Console.WriteLine($"{(d.Id == current ? "*" : " ")} {d.Index + 1}: {d.DisplayName}  {d.Id}");
                    return 0;

                case "windows":
                    var map = vds.GetDesktops().ToDictionary(d => d.Id, d => d.Index + 1);
                    foreach (var w in WindowEnumerator.GetWindows(vds).OrderBy(w => map.GetValueOrDefault(w.DesktopId ?? Guid.Empty)))
                        Console.WriteLine($"[{map.GetValueOrDefault(w.DesktopId ?? Guid.Empty)}] {w.ExeName,-28} {w.State,-9} {w.Title}");
                    return 0;

                case "list":
                    foreach (var p in repo.Load().Presets)
                        Console.WriteLine($"{p.Name}  ({p.Desktops.Count} デスクトップ, {p.Desktops.Sum(d => d.Apps.Count)} アプリ)");
                    return 0;

                case "capture" when args.Length >= 2:
                {
                    var lib = repo.Load();
                    var preset = PresetCapture.Capture(vds, args[1]);
                    var existing = lib.Presets.FirstOrDefault(p => p.Name == args[1]);
                    if (existing != null) lib.Presets[lib.Presets.IndexOf(existing)] = preset;
                    else lib.Presets.Add(preset);
                    repo.Save(lib);
                    Console.WriteLine($"保存しました: {repo.FilePath}");
                    return 0;
                }

                case "run" when args.Length >= 2:
                {
                    var preset = repo.Load().Presets.FirstOrDefault(p => p.Name == args[1]);
                    if (preset == null) { Console.Error.WriteLine($"プリセットが見つかりません: {args[1]}"); return 2; }
                    var log = new SyncProgress<string>(Console.WriteLine);
                    var summary = new PresetLauncher(vds).RunAsync(preset, log).GetAwaiter().GetResult();
                    return summary.Failed == 0 ? 0 : 1;
                }

                default:
                    Console.WriteLine("""
                        usage: deskpreset-cli <command>
                          desktops          仮想デスクトップ一覧
                          windows           デスクトップごとのウィンドウ一覧
                          list              保存済みプリセット一覧
                          capture <名前>     現在の配置をプリセットとして保存
                          run <名前>         プリセットを起動
                        """);
                    return cmd == null ? 0 : 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 1;
        }
    }
}


