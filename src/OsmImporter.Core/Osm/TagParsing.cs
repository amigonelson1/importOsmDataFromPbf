using System.Globalization;
using System.Text.RegularExpressions;

namespace OsmImporter.Core.Osm;

public static partial class TagParsing
{
  /// <summary>Igual que el SQL original: oneway IN ('yes','true','1').</summary>
  public static bool IsOneWayForward(string? oneway) => oneway is "yes" or "true" or "1";

  public static bool IsOneWayReverse(string? oneway) => oneway is "-1" or "reverse";

  /// <summary>"2" → 2; "2;3" → 2; texto no numérico → null.</summary>
  public static int? ParseLanes(string? value)
  {
    if (string.IsNullOrWhiteSpace(value)) return null;
    var first = value.Split(';', '|')[0].Trim();
    return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is > 0 and < 100 ? n : null;
  }

  /// <summary>Velocidad máxima en km/h. "60" → 60; "60 mph" → 97; "AR:urban", "none", "signals" → null.</summary>
  public static int? ParseMaxSpeed(string? value)
  {
    if (string.IsNullOrWhiteSpace(value)) return null;
    var m = MaxSpeedRegex().Match(value.Split(';')[0]);
    if (!m.Success) return null;

    var number = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
    if (m.Groups["u"].Value.Equals("mph", StringComparison.OrdinalIgnoreCase)) number *= 1.609344;

    var kmh = (int)Math.Round(number, MidpointRounding.AwayFromZero);
    return kmh is > 0 and < 400 ? kmh : null;
  }

  /// <summary>Parsea dimensiones métricas (ej. "4.5", "4.5 m", "14'6\""). Devuelve metros o null.</summary>
  public static double? ParseDimension(string? value)
  {
    if (string.IsNullOrWhiteSpace(value)) return null;
    value = value.Trim().Replace(',', '.');

    var feetMatch = FeetInchesRegex().Match(value);
    if (feetMatch.Success &&
        double.TryParse(feetMatch.Groups["feet"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var feet))
    {
      var inches = 0.0;
      if (feetMatch.Groups["inches"].Success)
        double.TryParse(feetMatch.Groups["inches"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out inches);
      var m = feet * 0.3048 + inches * 0.0254;
      return m is > 0 and < 50 ? Math.Round(m, 2) : null;
    }

    var numMatch = NumberWithUnitRegex().Match(value);
    if (numMatch.Success && double.TryParse(numMatch.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
    {
      var unit = numMatch.Groups["u"].Value;
      if (unit.Equals("feet", StringComparison.OrdinalIgnoreCase) || unit.Equals("ft", StringComparison.OrdinalIgnoreCase))
        num *= 0.3048;
      return num is > 0 and < 50 ? Math.Round(num, 2) : null;
    }

    return null;
  }

  /// <summary>Parsea pesos máximos (ej. "12", "12 t", "24000 lbs"). Devuelve toneladas métricas o null.</summary>
  public static double? ParseWeight(string? value)
  {
    if (string.IsNullOrWhiteSpace(value)) return null;
    value = value.Trim().Replace(',', '.');

    var numMatch = NumberWithUnitRegex().Match(value);
    if (numMatch.Success && double.TryParse(numMatch.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
    {
      var unit = numMatch.Groups["u"].Value.ToLowerInvariant();
      if (unit is "lbs" or "lb") num *= 0.00045359237;
      else if (unit is "kg") num /= 1000.0;
      return num is > 0 and < 1000 ? Math.Round(num, 2) : null;
    }

    return null;
  }

  /// <summary>Parsea layer (-5 a 5). Por defecto 0.</summary>
  public static int ParseLayer(string? value)
  {
    if (string.IsNullOrWhiteSpace(value)) return 0;
    if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
      return Math.Clamp(l, -5, 5);
    return 0;
  }

  private static readonly Regex _feetInchesRegex = new(@"^\s*(?<feet>\d+)\s*'\s*(?<inches>\d+(\.\d+)?)?\s*""?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
  private static Regex FeetInchesRegex() => _feetInchesRegex;

  private static readonly Regex _numberWithUnitRegex = new(@"^\s*(?<n>\d+(\.\d+)?)\s*(?<u>[a-zA-Z]+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
  private static Regex NumberWithUnitRegex() => _numberWithUnitRegex;

  private static readonly Regex _maxSpeedRegex = new(@"^\s*(?<n>\d+(\.\d+)?)\s*(?<u>mph|km/h|kmh|kph)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
  private static Regex MaxSpeedRegex() => _maxSpeedRegex;
}
