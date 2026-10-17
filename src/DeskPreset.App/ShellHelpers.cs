using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskPreset.Core;

namespace DeskPreset.App;

/// <summary>Shortcuts, Start menu items and icons via the Windows shell.</summary>
static class ShellHelpers
{
    public static AppEntry EntryFromFile(string path)
    {
        var entry = EntryFromFileCore(path);
        PathPortability.Generalize(entry);
        return entry;
    }

    static AppEntry EntryFromFileCore(string path)
    {
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                dynamic ws = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                dynamic sc = ws.CreateShortcut(path);
                string target = sc.TargetPath;
                if (!string.IsNullOrEmpty(target))
                    return new AppEntry
                    {
                        DisplayName = Path.GetFileNameWithoutExtension(path),
                        ExePath = target,
                        Arguments = NullIfEmpty((string)sc.Arguments),
                        WorkingDirectory = NullIfEmpty((string)sc.WorkingDirectory),
                    };
            }
            catch { /* fall back to launching the .lnk itself */ }
        }
        bool isExe = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        return new AppEntry
        {
            DisplayName = isExe ? PresetCapture.DescribeExe(path) : Path.GetFileNameWithoutExtension(path),
            ExePath = path,
        };
    }

    /// <summary>Turns a shell:AppsFolder item (launch ID + name) into an entry.</summary>
    public static AppEntry EntryFromStartMenu(string id, string name)
    {
        if (id.Equals("Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase))
            return new AppEntry { DisplayName = name, ExePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") };

        var path = ResolveKnownFolderPath(id);
        if (path != null && File.Exists(path))
        {
            var e = EntryFromFile(path);
            e.DisplayName = name;
            return e;
        }
        // Packaged apps (Name_Publisher!App) and plain AppUserModelIDs (Chrome, browser PWAs, …)
        return new AppEntry { DisplayName = name, AppUserModelId = id, LaunchViaAppId = true };
    }

    /// <summary>"{KnownFolderGuid}\rest" or an absolute path → file system path.</summary>
    static string? ResolveKnownFolderPath(string id)
    {
        if (Path.IsPathRooted(id) && id.Length > 2 && id[1] == ':') return id;
        if (!id.StartsWith('{')) return null;
        int end = id.IndexOf('}');
        if (end < 0 || !Guid.TryParse(id[1..end], out var folder)) return null;
        if (SHGetKnownFolderPath(folder, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p) + id[(end + 1)..]; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static string CreateShortcut(Preset preset, bool startup)
    {
        var folder = Environment.GetFolderPath(startup ? Environment.SpecialFolder.Startup : Environment.SpecialFolder.DesktopDirectory);
        var safe = string.Concat(preset.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(folder, $"DeskPreset - {safe}.lnk");
        var exe = Environment.ProcessPath!;
        dynamic ws = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic sc = ws.CreateShortcut(path);
        sc.TargetPath = exe;
        sc.Arguments = $"--run \"{preset.Name}\"";
        sc.WorkingDirectory = Path.GetDirectoryName(exe);
        sc.IconLocation = exe + ",0";
        sc.Description = $"仮想デスクトップ プリセット「{preset.Name}」を開く";
        sc.Save();
        return path;
    }

    static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ---------------- icons ----------------

    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string? exePath, string? appId, bool viaAppId)
    {
        string? parsingName = viaAppId && !string.IsNullOrEmpty(appId) ? @"shell:AppsFolder\" + appId
            : !string.IsNullOrEmpty(exePath) ? PathPortability.Expand(exePath)
            : !string.IsNullOrEmpty(appId) ? @"shell:AppsFolder\" + appId
            : null;
        if (parsingName == null) return null;
        if (IconCache.TryGetValue(parsingName, out var cached)) return cached;
        return IconCache[parsingName] = LoadIcon(parsingName);
    }

    static ImageSource? LoadIcon(string parsingName)
    {
        if (SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _) != 0) return null;
        try
        {
            var info = new SHFILEINFO();
            if (SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_PIDL | SHGFI_ICON | SHGFI_LARGEICON) == 0
                || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var img = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
                return img;
            }
            finally { DestroyIcon(info.hIcon); }
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_PIDL = 0x8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);
}

/// <summary>[ExePath, AppUserModelId, LaunchViaAppId] → icon.</summary>
sealed class AppIconConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        ShellHelpers.GetIcon(values[0] as string, values[1] as string, values[2] is true);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>When true: null → Visible, non-null → Collapsed.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        (value != null) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

sealed class StateLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        MainViewModel.StateOptions.FirstOrDefault(o => Equals(o.Value, value))?.Label ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}


