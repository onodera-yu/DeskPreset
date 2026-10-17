using System.Text.RegularExpressions;

namespace DeskPreset.Core;

/// <summary>
/// Makes stored paths work on other PCs: user/system folders become %ENV% variables, and version-numbered
/// folders (app-1.2.3, 120.0.6099.71, …) become "*" resolved to the newest match at launch time.
/// </summary>
public static partial class PathPortability
{
    static readonly (string Var, Environment.SpecialFolder Folder)[] Roots =
    [
        ("%LOCALAPPDATA%", Environment.SpecialFolder.LocalApplicationData),
        ("%APPDATA%", Environment.SpecialFolder.ApplicationData),
        ("%ProgramFiles(x86)%", Environment.SpecialFolder.ProgramFilesX86),
        ("%ProgramFiles%", Environment.SpecialFolder.ProgramFiles),
        ("%SystemRoot%", Environment.SpecialFolder.Windows),
        ("%USERPROFILE%", Environment.SpecialFolder.UserProfile),
    ];

    // "app-2.42304.0", "1.2.3", "v20.1", "120.0.6099.71" — at least two numeric parts.
    [GeneratedRegex(@"^(?<prefix>[A-Za-z_-]*?)v?\d+(\.\d+)+$")]
    private static partial Regex VersionSegment();

    /// <summary>Absolute path → portable form. Already portable or non-path strings are returned unchanged.</summary>
    public static string? Generalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('%') || !Path.IsPathFullyQualified(path)) return path;
        if (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return path; // launched via app ID

        path = PreferSquirrelStub(path);

        // Longest root first so %LOCALAPPDATA% wins over %USERPROFILE%.
        var result = path;
        foreach (var (var, folder) in Roots
                     .Select(r => (r.Var, Root: Environment.GetFolderPath(r.Folder)))
                     .Where(r => r.Root.Length > 0)
                     .OrderByDescending(r => r.Root.Length)
                     .Select(r => (r.Var, Folder: r.Root)))
        {
            if (path.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase))
            {
                result = var + path[folder.Length..];
                break;
            }
        }

        var parts = result.Split('\\');
        for (int i = 1; i < parts.Length - 1; i++) // never the drive/root or the file name
        {
            var m = VersionSegment().Match(parts[i]);
            if (m.Success) parts[i] = m.Groups["prefix"].Value + "*";
        }
        return string.Join('\\', parts);
    }

    /// <summary>
    /// Squirrel-installed apps (Discord, Slack, Claude, …) keep the real exe in app-x.y.z\ and a stable
    /// launcher with the same name one level up; the launcher survives updates.
    /// </summary>
    static string PreferSquirrelStub(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir == null || !Path.GetFileName(dir).StartsWith("app-", StringComparison.OrdinalIgnoreCase)) return path;
        var stub = Path.Combine(Path.GetDirectoryName(dir)!, Path.GetFileName(path));
        return File.Exists(stub) ? stub : path;
    }

    /// <summary>Portable form → real path on this PC (newest match for "*" folders).</summary>
    public static string? Expand(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        path = Environment.ExpandEnvironmentVariables(path);
        if (!path.Contains('*') || !Path.IsPathFullyQualified(path)) return path;

        var parts = path.Split('\\');
        var current = parts[0] + @"\";
        for (int i = 1; i < parts.Length; i++)
        {
            if (!parts[i].Contains('*'))
            {
                current = Path.Combine(current, parts[i]);
                continue;
            }
            if (!Directory.Exists(current)) return path;
            var match = Directory.EnumerateDirectories(current, parts[i])
                .OrderByDescending(d => VersionKey(Path.GetFileName(d)))
                .FirstOrDefault(d => i == parts.Length - 1 || Exists(Path.Combine([d, .. parts[(i + 1)..]])));
            if (match == null) return path;
            current = match;
        }
        return current;
    }

    static bool Exists(string p) => p.Contains('*') ? Expand(p) is { } e && !e.Contains('*') && File.Exists(e) : File.Exists(p) || Directory.Exists(p);

    /// <summary>Sortable key: numbers compared numerically ("10.0" &gt; "9.9").</summary>
    static string VersionKey(string name) =>
        Regex.Replace(name, @"\d+", m => m.Value.PadLeft(10, '0'));

    /// <summary>Generalizes the path-like fields of an entry in place.</summary>
    public static void Generalize(AppEntry e)
    {
        e.ExePath = Generalize(e.ExePath);
        e.WorkingDirectory = Generalize(e.WorkingDirectory);
        if (e.Arguments is { Length: > 2 } a && a[0] == '"' && a[^1] == '"' && !a[1..^1].Contains('"'))
            e.Arguments = $"\"{Generalize(a[1..^1])}\"";
        else if (e.Arguments is { } plain && !plain.Contains(' '))
            e.Arguments = Generalize(plain);
    }

    /// <summary>Expands %VARS% in arguments, and "*" folders when the whole argument is one path.</summary>
    public static string ExpandArguments(string? args)
    {
        if (string.IsNullOrEmpty(args)) return "";
        var s = Environment.ExpandEnvironmentVariables(args);
        if (!s.Contains('*')) return s;
        return s.Length > 2 && s[0] == '"' && s[^1] == '"' ? $"\"{Expand(s[1..^1])}\"" : Expand(s) ?? s;
    }

    public static void Generalize(Preset p)
    {
        foreach (var d in p.Desktops)
            foreach (var a in d.Apps)
                Generalize(a);
    }
}

