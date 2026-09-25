using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MechanicaLauncher.Core.IO;

namespace MechanicaLauncher.Core.Game;

public sealed record CrashReport(DateTimeOffset Time, int ExitCode, string Reason, string Evidence, string LogTail);

public static partial class CrashAnalyzer
{
    public static CrashReport Analyze(string log, int exitCode, IEnumerable<string>? secrets = null)
    {
        log = Redact(log, secrets);
        if (log.Length > 64_000) log = log[^64_000..];
        var rules = new (string Code, string[] Patterns)[]
        {
            ("memory", ["OutOfMemoryError", "Could not reserve enough space", "Native memory allocation (malloc) failed"]),
            ("java", ["UnsupportedClassVersionError", "compiled by a more recent version of the Java Runtime", "Unsupported class file major version"]),
            ("duplicate", ["DuplicateModsFoundException", "Duplicate mods", "Found duplicate mods"]),
            ("dependency", ["ModResolutionException", "Incompatible mods found", "Missing or unsupported mandatory dependencies", "requires version"]),
            ("graphics", ["GLFW error 65542", "GLFW error 65543", "Pixel format not accelerated", "Failed to create window"]),
            ("mixin", ["MixinApplyError", "MixinTransformerError", "InvalidMixinException", "InjectionError"]),
            ("files", ["Invalid or corrupt jarfile", "Could not find or load main class", "ZipException"]),
            ("auth", ["Invalid session", "Failed to verify username", "AuthenticationException"])
        };
        foreach (var (code, patterns) in rules)
        {
            var line = log.Split('\n').FirstOrDefault(l => patterns.Any(p => l.Contains(p, StringComparison.OrdinalIgnoreCase)));
            if (line != null) return new(DateTimeOffset.UtcNow, exitCode, code, line.Trim()[..Math.Min(line.Trim().Length, 700)], log);
        }
        return new(DateTimeOffset.UtcNow, exitCode, "unknown", "", log);
    }

    public static string Redact(string text, IEnumerable<string>? secrets = null)
    {
        foreach (var secret in (secrets ?? []).Where(s => !string.IsNullOrEmpty(s) && s.Length > 3).OrderByDescending(s => s.Length))
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return TokenPattern().Replace(text, "$1[redacted]");
    }

    public static async Task<CrashReport> CaptureAsync(string gameDir, int exitCode, IEnumerable<string>? secrets = null)
    {
        var text = await ReadTailAsync(Path.Combine(gameDir, "logs", "launcher-latest.log"));
        var report = Analyze(text, exitCode, secrets);
        SaveReport(gameDir, report);
        return report;
    }

    public static void SaveReport(string gameDir, CrashReport report)
    {
        var path = Path.Combine(gameDir, ".mechanica", "crashes.json");
        var reports = GetReports(gameDir).Prepend(report).Take(20).ToArray();
        AtomicFile.WriteText(path, JsonSerializer.Serialize(reports));
    }

    public static IReadOnlyList<CrashReport> GetReports(string gameDir)
    {
        try { return JsonSerializer.Deserialize<CrashReport[]>(File.ReadAllText(Path.Combine(gameDir, ".mechanica", "crashes.json"))) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public static async Task<string> ReadTailAsync(string path)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
            var offset = Math.Max(0, stream.Length - 128_000);
            stream.Position = offset;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (offset > 0) await reader.ReadLineAsync();
            return await reader.ReadToEndAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    [GeneratedRegex("((?:access[_-]?token|refresh[_-]?token|authorization|password|--accessToken)[\\\"'\\s:=]+(?:Bearer\\s+)?)[^\\s\\\"',}]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();
}
