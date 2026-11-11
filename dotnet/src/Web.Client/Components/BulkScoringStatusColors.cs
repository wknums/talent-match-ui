namespace TalentMatch.Web.Client.Components;

// Status colours for the bulk scoring panel. Every pair keeps a text contrast of at least 4.5:1 (WCAG AA).
public static class BulkScoringStatusColors
{
    public const double MinimumContrast = 4.5;

    private static readonly IReadOnlyDictionary<string, (string Foreground, string Background)> Palette =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["waiting"] = ("#334155", "#e2e8f0"),
            ["importing"] = ("#1e3a8a", "#dbeafe"),
            ["converting"] = ("#1e3a8a", "#dbeafe"),
            ["scoring"] = ("#1e3a8a", "#dbeafe"),
            ["completed"] = ("#14532d", "#dcfce7"),
            ["failed"] = ("#7f1d1d", "#fee2e2"),
            ["skipped"] = ("#44403c", "#f5f5f4"),
            ["cancelled"] = ("#713f12", "#fef3c7"),
            ["active"] = ("#1e3a8a", "#dbeafe"),
        };

    public static IEnumerable<string> Statuses => Palette.Keys;

    public static (string Foreground, string Background) For(string status) =>
        Palette.TryGetValue(status, out var colors) ? colors : Palette["waiting"];

    public static double ContrastRatio(string foreground, string background)
    {
        var (a, b) = (Luminance(foreground), Luminance(background));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int start)
        {
            var value = Convert.ToInt32(hex.Substring(start, 2), 16) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }
}
