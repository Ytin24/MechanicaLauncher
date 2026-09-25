namespace MechanicaLauncher.Core.Mods;

internal static class FabricVersionRequirement
{
    public static bool? Matches(string version, IEnumerable<string> alternatives)
    {
        var results = alternatives.Select(r => MatchAll(version, r)).ToArray();
        if (results.Any(r => r == true)) return true;
        return results.Length == 0 || results.Any(r => r == null) ? null : false;
    }

    private static bool? MatchAll(string version, string expression)
    {
        var terms = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = terms.Select(term => MatchTerm(version, term)).ToArray();
        if (results.Any(r => r == false)) return false;
        return results.Any(r => r == null) ? null : true;
    }

    private static bool? MatchTerm(string version, string term)
    {
        if (term == "*") return true;
        var op = new[] { ">=", "<=", ">", "<", "=", "~", "^" }.FirstOrDefault(term.StartsWith) ?? "";
        var required = term[op.Length..];
        if ((op is "" or "=") && version == required) return true;
        var current = Parse(version);
        if (current == null) return null;
        var wildcard = required.Split('.');
        int first = Array.FindIndex(wildcard, part => part is "*" or "x" or "X");
        if (first >= 0)
        {
            if (op is not ("" or "=") || wildcard.Skip(first).Any(p => p is not ("*" or "x" or "X"))) return null;
            for (int i = 0; i < first; i++)
            {
                if (!int.TryParse(wildcard[i], out var component) || component < 0) return null;
                if (component != current.ElementAtOrDefault(i)) return false;
            }
            return true;
        }
        var target = Parse(required);
        if (target == null) return null;
        int comparison = 0;
        for (int i = 0; i < Math.Max(current.Length, target.Length) && comparison == 0; i++)
            comparison = current.ElementAtOrDefault(i).CompareTo(target.ElementAtOrDefault(i));
        return op switch
        {
            "" or "=" => comparison == 0,
            ">=" => comparison >= 0,
            "<=" => comparison <= 0,
            ">" => comparison > 0,
            "<" => comparison < 0,
            "~" => comparison >= 0 && current[0] == target[0] && current.ElementAtOrDefault(1) == target.ElementAtOrDefault(1),
            "^" => comparison >= 0 && current[0] == target[0],
            _ => null
        };
    }

    private static int[]? Parse(string value)
    {
        var build = value.IndexOf('+');
        if (build >= 0) value = value[..build];
        var parts = value.Split('.');
        if (parts.Length > 8) return null;
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length == 0 || parts[i].Any(c => c is < '0' or > '9') || !int.TryParse(parts[i], out result[i])) return null;
        return result;
    }
}
