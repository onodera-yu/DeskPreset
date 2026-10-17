using System.Diagnostics;
using DeskPreset.Core.Interop;

namespace DeskPreset.Core;

/// <summary>Turns the windows currently open on each virtual desktop into a preset.</summary>
public static class PresetCapture
{
    public static Preset Capture(VirtualDesktopService vds, string name)
    {
        var desktops = vds.GetDesktops();
        var windows = WindowEnumerator.GetWindows(vds);
        var folders = GetExplorerFolders();
        var apps = GetStartMenuApps();

        var preset = new Preset { Name = name };
        foreach (var d in desktops)
        {
            var layout = new DesktopLayout { Name = d.Name, Index = d.Index };
            // EnumWindows returns top-most first; reverse so the back-most window is launched first.
            foreach (var w in windows.Where(w => w.DesktopId == d.Id).Reverse())
            {
                var entry = FromWindow(w, apps);
                if (folders.TryGetValue(w.Handle, out var folder)) entry.Arguments = Quote(folder);
                PathPortability.Generalize(entry);
                layout.Apps.Add(entry);
            }
            preset.Desktops.Add(layout);
        }
        return preset;
    }

    /// <param name="apps">Start menu apps (AppUserModelID → name) from <see cref="GetStartMenuApps"/>.</param>
    public static AppEntry FromWindow(WindowInfo w, IReadOnlyDictionary<string, string>? apps = null)
    {
        apps ??= GetStartMenuApps();
        var isFrameHost = string.Equals(w.ExeName, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
        var aumid = w.AppUserModelId;
        if (string.IsNullOrEmpty(aumid) && IsPackagedPath(w.ExePath)) aumid = FindPackageAppId(w.ExePath, apps);

        // Store apps and browser PWAs only start correctly through their app ID.
        bool viaAppId = aumid != null && apps.ContainsKey(aumid)
            && (isFrameHost || IsPackagedPath(w.ExePath) || aumid.Contains("_crx_", StringComparison.OrdinalIgnoreCase));
        string displayName = viaAppId && apps.TryGetValue(aumid!, out var appName) ? appName
            : isFrameHost ? w.Title : DescribeExe(w.ExePath);

        var entry = new AppEntry
        {
            DisplayName = displayName,
            ExePath = isFrameHost ? null : w.ExePath,
            AppUserModelId = aumid,
            LaunchViaAppId = viaAppId,
            // Without an app ID, packaged apps sharing ApplicationFrameHost.exe can only be told apart by title.
            TitleContains = isFrameHost && aumid == null ? w.Title : null,
            CapturedTitle = w.Title,
            Bounds = w.Bounds,
            State = w.State,
        };
        PathPortability.Generalize(entry);
        return entry;
    }

    public static bool IsPackagedPath(string? path) =>
        path != null && path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps an exe under WindowsApps (…\Name_Version_Arch__PublisherId\…) to a Start menu app ID
    /// of the form Name_PublisherId!AppId.
    /// </summary>
    static string? FindPackageAppId(string exePath, IReadOnlyDictionary<string, string> apps)
    {
        var family = PackageFamilyFromPath(exePath);
        return family == null ? null : apps.Keys.FirstOrDefault(k => k.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Package family name (Name_PublisherId) of an exe installed under WindowsApps.</summary>
    public static string? PackageFamilyFromPath(string? exePath)
    {
        if (!IsPackagedPath(exePath)) return null;
        var parts = exePath!.Split('\\');
        int i = Array.FindIndex(parts, p => p.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= parts.Length) return null;
        var folder = parts[i + 1];
        int sep = folder.IndexOf("__", StringComparison.Ordinal);
        int first = folder.IndexOf('_');
        return sep < 0 || first < 0 ? null : folder[..first] + "_" + folder[(sep + 2)..];
    }

    /// <summary>Everything in shell:AppsFolder, keyed by its launch ID.</summary>
    public static Dictionary<string, string> GetStartMenuApps()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return map;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string id = item.Path;
                string name = item.Name;
                if (!string.IsNullOrEmpty(id)) map.TryAdd(id, name);
            }
        }
        catch { /* shell unavailable */ }
        return map;
    }
    public static string DescribeExe(string path)
    {
        if (string.IsNullOrEmpty(path)) return "(不明)";
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
        }
        catch { /* unreadable version info */ }
        return Path.GetFileNameWithoutExtension(path);
    }

    static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    /// <summary>Folder shown in each File Explorer window, keyed by window handle.</summary>
    static Dictionary<IntPtr, string> GetExplorerFolders()
    {
        var map = new Dictionary<IntPtr, string>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return map;
            dynamic shell = Activator.CreateInstance(type)!;
            foreach (dynamic win in shell.Windows())
            {
                try
                {
                    string path = win.Document.Folder.Self.Path;
                    long hwnd = win.HWND;
                    if (!string.IsNullOrEmpty(path) && !path.StartsWith("::")) map[new IntPtr(hwnd)] = path;
                }
                catch { /* not a folder view (e.g. Home) */ }
            }
        }
        catch { /* shell unavailable */ }
        return map;
    }
}





