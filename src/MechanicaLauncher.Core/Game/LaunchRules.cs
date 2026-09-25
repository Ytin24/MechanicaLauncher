using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Game;

internal static class LaunchRules
{
    public static bool Evaluate(IReadOnlyList<Rule>? rules, IReadOnlyDictionary<string, bool>? features = null,
        Architecture? architecture = null, string? osVersion = null)
    {
        if (rules == null || rules.Count == 0) return true;
        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule.Os is { } os)
            {
                if (os.Name != null && os.Name != "windows") continue;
                if (os.Arch != null && !MatchesArchitecture(os.Arch, architecture ?? RuntimeInformation.OSArchitecture)) continue;
                if (os.Version != null && !Regex.IsMatch(osVersion ?? Environment.OSVersion.Version.ToString(),
                    os.Version, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) continue;
            }
            if (rule.Features != null && rule.Features.Any(feature =>
                (features != null && features.TryGetValue(feature.Key, out var enabled) && enabled) != feature.Value)) continue;
            allowed = rule.Action == "allow";
        }
        return allowed;
    }

    private static bool MatchesArchitecture(string name, Architecture architecture) => architecture switch
    {
        Architecture.X64 => name is "x86_64" or "x64" or "amd64",
        Architecture.X86 => name is "x86" or "i386",
        Architecture.Arm64 => name is "arm64" or "aarch64",
        _ => false
    };
}
