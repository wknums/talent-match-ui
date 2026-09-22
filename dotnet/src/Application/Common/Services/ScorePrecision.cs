namespace TalentMatch.Application.Common.Services;

public static class ScorePrecision
{
    public const int DecimalPlaces = 3;

    public static double Round(double value)
        => Math.Round(value, DecimalPlaces, MidpointRounding.AwayFromZero);

    public static double? Round(double? value)
        => value.HasValue ? Round(value.Value) : null;
}
