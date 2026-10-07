namespace LeagueAkari.WinUI.Services;

/// <summary>LCU debug routes follow League Akari's RadixMatcher segment rules.</summary>
public static class DebugRoute
{
    public static string Normalize(string route) => "/" + string.Join("/", route.Split('/', StringSplitOptions.RemoveEmptyEntries));
    public static void Validate(string route)
    {
        var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("route should not be empty");
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.StartsWith(':')) { if (part.Length == 1) throw new ArgumentException("placeholder should have a name"); }
            else if (part.StartsWith("**")) { if (part != "**") throw new ArgumentException("wildcard should be **"); if (i != parts.Length - 1) throw new ArgumentException("wildcard should be the last part"); }
            else if (part.StartsWith('*')) { if (part != "*") throw new ArgumentException("placeholder * should be the only part"); }
            else if (part.Contains(':') || part.Contains('*')) throw new ArgumentException("normal nodes should not have : or *");
        }
    }
    public static bool Matches(string route, string uri)
    {
        var pattern = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = uri.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == "**") return true;
            if (i >= path.Length) return false;
            if (pattern[i] != "*" && !pattern[i].StartsWith(':') && pattern[i] != path[i]) return false;
        }
        return path.Length == pattern.Length;
    }
}
