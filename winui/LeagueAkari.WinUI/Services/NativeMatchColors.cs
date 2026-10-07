namespace LeagueAkari.WinUI.Services;

public readonly record struct NativeRgb(byte R, byte G, byte B);
public sealed record NativeMatchPalette(NativeRgb Background, NativeRgb Border, NativeRgb Result);

/// <summary>MatchCardOverview's neutral surface and result shadow, composited as in match-card.css.</summary>
public static class NativeMatchColors
{
    public static NativeMatchPalette History(string result, bool dark, string theme)
    {
        int kind = result == "win" ? 0 : result == "loss" ? 1 : 2;
        (int R, int G, int B, double Alpha)[] overlays = theme switch
        {
            "butter" => [(89,155,211,.24),(215,110,123,.22),(173,122,53,.18)],
            "graphite" => [(42,137,191,.24),(190,74,96,.24),(96,118,139,.18)],
            "cyber" => [(46,130,168,.24),(178,74,91,.24),(142,162,0,.18)],
            "sakura" => [(118,171,233,.24),(213,88,129,.24),(200,88,135,.16)],
            "mint" => [(89,159,214,.24),(215,104,131,.22),(50,142,108,.18)],
            "aurora" => [(55,151,179,.24),(190,73,125,.24),(104,91,153,.18)],
            _ when dark => [(37,99,235,.16),(190,18,60,.16),(148,163,184,.12)],
            _ => [(96,165,250,.35),(239,68,68,.3),(168,168,168,.24)]
        };
        // Named themes override the neutral base via --la-card-surface-95 before the shadow is applied.
        NativeRgb surface = theme switch { "butter" => new(250,248,240), "graphite" => new(16,31,46), "cyber" => new(15,15,15), "sakura" => new(255,247,249), "mint" => new(250,252,251), "aurora" => new(41,39,60), _ => dark ? new(23,23,23) : new(245,245,245) };
        var background = Composite(surface, overlays[kind]);
        var resultColor = kind switch { 0 => dark ? new NativeRgb(147,197,253) : new(37,99,235), 1 => dark ? new NativeRgb(252,165,165) : new(185,28,28), _ => dark ? new NativeRgb(230,230,230) : new(0,0,0) };
        var border = kind switch { 0 => dark ? new NativeRgb(147,197,253) : new(37,99,235), 1 => dark ? new NativeRgb(252,165,165) : new(220,38,38), _ => dark ? new NativeRgb(255,255,255) : new(0,0,0) };
        return new(background, border, resultColor);
    }

    public static NativeMatchPalette Ongoing(string result, bool dark)
    {
        int kind = result == "win" ? 0 : result == "loss" ? 1 : 2;
        (int R, int G, int B, double Alpha) tint = kind switch { 0 => dark ? (59,130,246,.25) : (96,165,250,.35), 1 => dark ? (243,73,72,.25) : (243,73,72,.3), _ => dark ? (255,255,255,.15) : (200,200,200,.45) };
        return new(Composite(dark ? new(20,20,22) : new(243,243,244), tint), new((byte)tint.R,(byte)tint.G,(byte)tint.B), kind switch { 0 => dark ? new(147,197,253) : new(37,99,235), 1 => dark ? new(252,165,165) : new(185,28,28), _ => dark ? new(230,230,230) : new(0,0,0) });
    }
    private static NativeRgb Composite(NativeRgb background, (int R,int G,int B,double Alpha) overlay) => new((byte)Math.Round(background.R*(1-overlay.Alpha)+overlay.R*overlay.Alpha), (byte)Math.Round(background.G*(1-overlay.Alpha)+overlay.G*overlay.Alpha), (byte)Math.Round(background.B*(1-overlay.Alpha)+overlay.B*overlay.Alpha));
}
