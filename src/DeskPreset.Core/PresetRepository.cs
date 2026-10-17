using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskPreset.Core;

/// <summary>Loads and saves presets.json (human-editable).</summary>
public sealed class PresetRepository(string? directory = null)
{
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("DESKPRESET_HOME") is { Length: > 0 } dir ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskPreset");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Directory { get; } = directory ?? DefaultDirectory;
    public string FilePath => Path.Combine(Directory, "presets.json");

    public PresetLibrary Load() => File.Exists(FilePath) ? ReadFile(FilePath) : new PresetLibrary();

    public void Save(PresetLibrary library) => WriteFile(FilePath, library);

    /// <summary>Reads presets.json or an exported file. Paths are normalized to the portable form.</summary>
    public static PresetLibrary ReadFile(string path)
    {
        var lib = JsonSerializer.Deserialize<PresetLibrary>(File.ReadAllText(path), Options) ?? new PresetLibrary();
        foreach (var p in lib.Presets) PathPortability.Generalize(p);
        return lib;
    }

    public static void WriteFile(string path, PresetLibrary library)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Write to a temp file first so a crash never leaves a truncated file.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(library, Options));
        File.Move(tmp, path, overwrite: true);
    }
}


