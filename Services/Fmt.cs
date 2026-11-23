using System.Globalization;

namespace AntigravityUsageTracker.Services;

public static class Fmt
{
    public static string Tokens(double n)
    {
        var inv = CultureInfo.InvariantCulture;
        double a = Math.Abs(n);
        if (a >= 1_000_000_000) return (n / 1_000_000_000).ToString("0.##", inv) + "B";
        if (a >= 1_000_000) return (n / 1_000_000).ToString("0.##", inv) + "M";
        if (a >= 1_000) return (n / 1_000).ToString("0.#", inv) + "K";
        return n.ToString("0", inv);
    }

    public static string Percent(double? p) => p is null ? "-" : p.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
}
