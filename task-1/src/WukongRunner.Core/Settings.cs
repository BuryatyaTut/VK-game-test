using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WukongRunner.Core;

public static class AtomicFile
{
    public static void Write(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record RecoveryRecord(string OriginalPath, string BackupFile, string Sha256);

public sealed class SettingsBackup
{
    private readonly string path;
    private readonly byte[] original;
    public byte[] Original => original.ToArray();
    public string ManifestPath { get; }

    public SettingsBackup(string settingsPath, string runDirectory)
    {
        path = Path.GetFullPath(settingsPath);
        if (new FileInfo(path).LinkTarget != null) throw new IOException("Settings must be a regular file, not a symbolic link.");
        original = File.ReadAllBytes(path);
        if (original.Length == 0) throw new InvalidDataException("Settings are empty. Initialize the benchmark first.");
        var backup = Path.Combine(runDirectory, "original-settings.ini");
        ManifestPath = Path.Combine(runDirectory, "recovery.json");
        AtomicFile.Write(backup, original);
        JsonFiles.Write(ManifestPath, new RecoveryRecord(path, "original-settings.ini", Convert.ToHexString(SHA256.HashData(original))));
    }

    public void Apply(Profile profile) => AtomicFile.Write(path, SettingsEditor.Apply(original, profile));
    public void Restore() => AtomicFile.Write(path, original);

    public static void Recover(string manifestPath)
    {
        var record = JsonFiles.Read<RecoveryRecord>(manifestPath);
        if (record.BackupFile != "original-settings.ini" || !Path.IsPathFullyQualified(record.OriginalPath))
            throw new InvalidDataException("Invalid recovery manifest.");
        var bytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, record.BackupFile));
        if (Convert.ToHexString(SHA256.HashData(bytes)) != record.Sha256)
            throw new InvalidDataException("Recovery backup checksum does not match.");
        if (new FileInfo(record.OriginalPath).LinkTarget != null)
            throw new IOException("Refusing to restore through a symbolic link.");
        AtomicFile.Write(record.OriginalPath, bytes);
    }
}

public static partial class SettingsEditor
{
    public const string GameSection = "/Script/GSGameSettings.GSGameUserSettings";
    private static readonly Dictionary<string, string> Qualities = new()
    {
        ["ViewDistance"] = "sg.ViewDistanceQuality", ["AntiAliasing"] = "sg.AntiAliasingQuality",
        ["ShadowQuality"] = "sg.ShadowQuality", ["GlobalIllumination"] = "sg.GlobalIlluminationQuality",
        ["ReflectionQuality"] = "sg.ReflectionQuality", ["PostProcessing"] = "sg.PostProcessQuality",
        ["TextureQuality"] = "sg.TextureQuality", ["FxQuality"] = "sg.EffectsQuality",
        ["VegetationQuality"] = "sg.FoliageQuality", ["MaterialQuality"] = "sg.ShadingQuality"
    };

    public static Dictionary<string, string> UiValues(Profile profile)
    {
        var values = Qualities.Keys.ToDictionary(k => k, _ => profile.Quality.ToString(CultureInfo.InvariantCulture));
        values["Vsync"] = "0";
        values["ScreenMode"] = "2";
        values["LockFrameRate"] = "0";
        values["InsertFrame"] = "0";
        values["Rtx"] = "0";
        values["MotionBlur"] = "0";
        values["QualityLevel"] = "6"; // Custom: individual settings below determine the profile.
        foreach (var pair in profile.UiOverrides) values[pair.Key] = pair.Value;
        return values;
    }

    public static Dictionary<string, string> ExpectedSettings(Profile profile)
    {
        var values = UiValues(profile);
        values["ResolutionSizeX"] = profile.Width.ToString(CultureInfo.InvariantCulture);
        values["ResolutionSizeY"] = profile.Height.ToString(CultureInfo.InvariantCulture);
        values["sg.ResolutionQuality"] = profile.RenderPercent.ToString(CultureInfo.InvariantCulture);
        return values;
    }

    public static byte[] Apply(byte[] bytes, Profile profile)
    {
        profile.Validate();
        if (profile.Width == 0) throw new InvalidDataException("Resolve desktop dimensions before editing settings.");
        var (encoding, offset) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, offset, bytes.Length - offset);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        string section = "";
        var uiLines = new List<int>();
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Count; i++)
        {
            var trim = lines[i].Trim();
            if (trim.StartsWith('[') && trim.EndsWith(']'))
            {
                section = trim[1..^1];
                if (!sections.Add(section)) throw new InvalidDataException($"Duplicate INI section: {section}");
            }
            if (section.Equals(GameSection, StringComparison.OrdinalIgnoreCase) && Key(lines[i]) == "UISettingData") uiLines.Add(i);
        }
        if (!sections.Contains(GameSection) || !sections.Contains("ScalabilityGroups") || uiLines.Count != 1)
            throw new InvalidDataException("Expected GSGameUserSettings, ScalabilityGroups, and one UISettingData entry. Initialize the benchmark first.");

        var ui = UiValues(profile);
        var found = new HashSet<string>();
        lines[uiLines[0]] = UiPair().Replace(lines[uiLines[0]], match =>
        {
            var key = match.Groups[1].Value;
            if (!ui.TryGetValue(key, out var value)) return match.Value;
            if (!found.Add(key)) throw new InvalidDataException($"Duplicate UI setting: {key}");
            var group = match.Groups[2];
            var position = group.Index - match.Index;
            return match.Value[..position] + value + match.Value[(position + group.Length)..];
        });
        var missing = ui.Keys.Except(found).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("Unsupported UISettingData; missing: " + string.Join(", ", missing));

        var game = new Dictionary<string, string>
        {
            ["bUseVSync"] = "False", ["bUseDynamicResolution"] = "False", ["FrameRateLimit"] = "0.000000",
            ["ResolutionSizeX"] = profile.Width.ToString(CultureInfo.InvariantCulture),
            ["ResolutionSizeY"] = profile.Height.ToString(CultureInfo.InvariantCulture),
            ["LastUserConfirmedResolutionSizeX"] = profile.Width.ToString(CultureInfo.InvariantCulture),
            ["LastUserConfirmedResolutionSizeY"] = profile.Height.ToString(CultureInfo.InvariantCulture),
            ["DesiredScreenWidth"] = (profile.Width * profile.RenderPercent / 100).ToString(CultureInfo.InvariantCulture),
            ["DesiredScreenHeight"] = (profile.Height * profile.RenderPercent / 100).ToString(CultureInfo.InvariantCulture),
            ["FullscreenMode"] = "2", ["LastConfirmedFullscreenMode"] = "2", ["PreferredFullscreenMode"] = "2"
        };
        SetSection(lines, GameSection, game);
        var scalability = Qualities.Values.ToDictionary(k => k, _ => (profile.Quality - 1).ToString(CultureInfo.InvariantCulture));
        scalability["sg.ResolutionQuality"] = profile.RenderPercent.ToString(CultureInfo.InvariantCulture);
        scalability["sg.RayTracingQuality"] = "0";
        SetSection(lines, "ScalabilityGroups", scalability);
        SetSection(lines, "RayTracing", new Dictionary<string, string> { ["r.RayTracing.EnableInGame"] = "False" });
        return bytes[..offset].Concat(encoding.GetBytes(string.Join(newline, lines))).ToArray();
    }

    private static void SetSection(List<string> lines, string name, Dictionary<string, string> values)
    {
        var start = lines.FindIndex(line => line.Trim().Equals("[" + name + "]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) { lines.Add("[" + name + "]"); start = lines.Count - 1; }
        var end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
        foreach (var (key, value) in values)
        {
            var matches = Enumerable.Range(start + 1, end - start - 1).Where(i => Key(lines[i]) == key).ToArray();
            if (matches.Length > 1) throw new InvalidDataException($"Duplicate INI key: {key}");
            if (matches.Length == 1)
            {
                var index = matches[0];
                var equals = lines[index].IndexOf('=');
                var comment = lines[index].IndexOf(';', equals + 1);
                lines[index] = lines[index][..(equals + 1)] + value + (comment >= 0 ? " " + lines[index][comment..] : "");
            }
            else { lines.Insert(end++, key + "=" + value); }
        }
    }

    private static string? Key(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) return null;
        var equals = line.IndexOf('=');
        return equals < 0 ? null : line[..equals].Trim();
    }

    private static (Encoding, int) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new UTF8Encoding(false, true), 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return (new UnicodeEncoding(false, false, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return (new UnicodeEncoding(true, false, true), 2);
        return (new UTF8Encoding(false, true), 0);
    }

    [GeneratedRegex("\\(\\s*\"([^\"]+)\"\\s*,\\s*\"([^\"]*)\"\\s*\\)")]
    private static partial Regex UiPair();
}
