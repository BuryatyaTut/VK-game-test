using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace WukongRunner.Core;

public static class ResultParser
{
    public static ParsedResult Parse(byte[] bytes, Profile profile, AutomationOptions options)
    {
        ReadOnlyMemory<byte> content = bytes;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) content = content[3..];
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 64 });
        var root = Pointer(document.RootElement, options.ResultObjectPointer)
            ?? throw new InvalidDataException("Result object pointer is absent.");
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected one benchmark result object.");
        if (root.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("Duplicate result fields are ambiguous.");
        var avg = Number(root, "FPSAvg");
        var min = Number(root, "FPSMin");
        var max = Number(root, "FPSMax");
        if (avg <= 0 || min < 0 || max <= 0 || min > avg || avg > max)
            throw new InvalidDataException("FPS values must be finite and satisfy 0 <= min <= average <= max, with average > 0.");

        var reported = Pointer(root, options.ResultSettingsPointer);
        var checks = SettingsEditor.ExpectedSettings(profile).Select(pair =>
        {
            var value = reported is { ValueKind: JsonValueKind.Object } settings && settings.TryGetProperty(pair.Key, out var item)
                && item.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    ? Scalar(item) : null;
            return new SettingCheck(pair.Key, pair.Value, value, value == null ? "unverified" : Equivalent(pair.Value, value) ? "match" : "mismatch");
        }).ToArray();
        var raw = root.EnumerateObject()
            .Where(p => p.Name is not ("FPSAvg" or "FPSMin" or "FPSMax") &&
                p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            .ToDictionary(p => p.Name, p => Scalar(p.Value));
        return new ParsedResult(new(avg, min, max), checks, raw);
    }

    public static JsonElement? Pointer(JsonElement element, string pointer)
    {
        if (pointer == "") return element;
        foreach (var part in pointer.Split('/').Skip(1))
        {
            var name = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return null;
        }
        return element;
    }

    private static double Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) throw new InvalidDataException($"Missing {name}.");
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)) return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number)) return number;
        throw new InvalidDataException($"{name} is not a finite number.");
    }

    private static string Scalar(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    private static bool Equivalent(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        (double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
         double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && double.IsFinite(a) && double.IsFinite(b) && a == b);
}

public sealed record FileStamp(long Length, long LastWriteTicks, string Hash);

public sealed class ResultWatcher(IReadOnlyList<string> directories, AutomationOptions options)
{
    public Dictionary<string, FileStamp> CaptureBaseline()
    {
        var baseline = new Dictionary<string, FileStamp>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var path in Files())
        {
            var info = new FileInfo(path);
            if (info.Length > options.MaxResultBytes) throw new IOException($"History file exceeds the size limit: {path}");
            var bytes = ReadBounded(path);
            baseline[path] = new(bytes.LongLength, info.LastWriteTimeUtc.Ticks, Hash(bytes));
        }
        return baseline;
    }

    public async Task<ResultSnapshot> WaitAsync(Dictionary<string, FileStamp> baseline, Profile profile,
        Action checkHealth, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var seen = new Dictionary<string, (long Length, long WriteTicks, TimeSpan Since)>();
        var previousHashes = baseline.Values.Select(v => v.Hash).ToHashSet();
        string lastProblem = "No new complete JSON result appeared.";
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(options.RunTimeoutSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = new List<ResultSnapshot>();
            foreach (var path in Files())
            {
                try
                {
                    var info = new FileInfo(path);
                    if (baseline.TryGetValue(path, out var old) && old.Length == info.Length && old.LastWriteTicks == info.LastWriteTimeUtc.Ticks) continue;
                    if (!seen.TryGetValue(path, out var observation) || observation.Length != info.Length || observation.WriteTicks != info.LastWriteTimeUtc.Ticks)
                    { seen[path] = (info.Length, info.LastWriteTimeUtc.Ticks, stopwatch.Elapsed); continue; }
                    if (stopwatch.Elapsed - observation.Since < TimeSpan.FromMilliseconds(options.ResultStableMs)) continue;
                    // Wait for metadata stability before reading, to keep benchmark-time disk/CPU overhead low.
                    var bytes = ReadBounded(path);
                    info.Refresh();
                    if (info.Length != observation.Length || info.LastWriteTimeUtc.Ticks != observation.WriteTicks) continue;
                    var hash = Hash(bytes);
                    if (previousHashes.Contains(hash)) continue;
                    var parsed = ResultParser.Parse(bytes, profile, options);
                    candidates.Add(new(path, bytes, hash, parsed));
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                { lastProblem = $"{Path.GetFileName(path)}: {e.Message}"; }
            }
            if (candidates.Select(c => c.Sha256).Distinct().Count() > 1)
                throw new InvalidDataException("Multiple distinct fresh results are ambiguous; refusing to choose a score.");
            if (candidates.Count > 0) return candidates[0];
            // A process may exit immediately after writing results. Inspect results before checking liveness.
            checkHealth();
            await Task.Delay(options.PollIntervalMs, cancellationToken);
        }
        throw new TimeoutException($"Benchmark result timeout. {lastProblem}");
    }

    private IEnumerable<string> Files()
    {
        var files = directories.Where(Directory.Exists).SelectMany(directory => Directory.EnumerateFiles(directory, "*.json",
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Take(513).ToArray();
        if (files.Length > 512) throw new IOException("More than 512 history files; choose a narrower history directory.");
        return files;
    }

    private byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > options.MaxResultBytes) throw new IOException("Result exceeds the configured size limit.");
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (output.Length + count > options.MaxResultBytes) throw new IOException("Growing result exceeds the size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
