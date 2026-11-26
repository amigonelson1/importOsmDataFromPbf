using System.Globalization;
using System.Text.Json.Serialization;

namespace OsmImporter.Core.Options;

public enum SegmentizeMethod
{
  /// <summary>Bisección recursiva hasta que cada parte sea ≤ max (2^k partes). Es lo que hace
  /// ST_Segmentize(geography) en PostGIS 3.4 (verificado contra titan_postgis 3.4.3).</summary>
  Bisect,

  /// <summary>Divide cada arista en ceil(d / max) partes iguales: menos segmentos, más parejos.</summary>
  Uniform,
}

public sealed class BoundingBox
{
  public double MinLon { get; set; }
  public double MinLat { get; set; }
  public double MaxLon { get; set; }
  public double MaxLat { get; set; }

  public BoundingBox() { }

  public BoundingBox(double minLon, double minLat, double maxLon, double maxLat)
  {
    MinLon = minLon; MinLat = minLat; MaxLon = maxLon; MaxLat = maxLat;
  }

  /// <summary>Formato: "minLon,minLat,maxLon,maxLat" (punto decimal).</summary>
  public static BoundingBox Parse(string text)
  {
    var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 4)
      throw new FormatException($"bbox inválido '{text}'. Formato: minLon,minLat,maxLon,maxLat");

    var v = parts.Select(p => double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
    return new BoundingBox(v[0], v[1], v[2], v[3]);
  }

  public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{MinLon},{MinLat},{MaxLon},{MaxLat}");
}

/// <summary>
/// Parámetros de la etapa build. El bbox es obligatorio (perfil o --bbox); el resto tiene valores
/// por defecto genéricos que replican el proceso clásico osm2pgsql + PostGIS.
/// </summary>
public sealed class BuildOptions
{
  [JsonPropertyName("bbox")]
  public BoundingBox? BBox { get; set; }

  /// <summary>Buffer alrededor del bbox, en metros reales (o en unidades EPSG:3857 si LegacyBuffer).</summary>
  public double BufferMeters { get; set; } = 20000;

  /// <summary>true = aplicar el buffer en EPSG:3857 como el SQL original (a −38° de latitud 20 km "3857" ≈ 15,6 km reales).</summary>
  public bool LegacyBuffer { get; set; }

  public List<string> Highways { get; set; } = new()
  {
    "motorway", "trunk", "primary", "secondary", "tertiary",
    "residential", "unclassified", "service", "track",
  };

  /// <summary>
  /// Parte vías largas cada N unidades EPSG:3857 (como osm2pgsql, default 100000) y solo conserva los tramos
  /// que tocan el área. 0 = no partir: entra la vía completa si cualquier parte toca el área.
  /// </summary>
  public double SplitWaysAt3857 { get; set; } = 100_000;

  public double SegmentizeMeters { get; set; } = 50;

  public SegmentizeMethod SegmentizeMethod { get; set; } = SegmentizeMethod.Bisect;

  /// <summary>Se descartan micro-segmentos con longitud ≤ este valor.</summary>
  public double MinSegmentMeters { get; set; } = 1;

  /// <summary>Tamaño de la grilla (grados) para unificar nodos — ST_SnapToGrid.</summary>
  public double SnapDegrees { get; set; } = 0.00001;

  /// <summary>oneway=-1: invertir geometría y marcar sentido único.</summary>
  public bool ReverseOnewayMinusOne { get; set; } = true;

  /// <summary>Excluir vías cerradas con area=yes (osm2pgsql las manda a planet_osm_polygon).</summary>
  public bool ExcludeAreas { get; set; } = true;

  public Dictionary<string, double> CostFactors { get; set; } = new(StringComparer.OrdinalIgnoreCase)
  {
    ["motorway"] = 0.7,
    ["trunk"] = 0.7,
    ["primary"] = 1.0,
    ["secondary"] = 1.0,
    ["tertiary"] = 1.4,
    ["residential"] = 1.4,
  };

  public double DefaultCostFactor { get; set; } = 2.0;

  public double CostFactorFor(string highway) =>
    CostFactors.TryGetValue(highway, out var f) ? f : DefaultCostFactor;

  public void Validate()
  {
    var errors = new List<string>();
    var b = BBox;

    if (b is null) errors.Add("bbox requerido: usa --bbox minLon,minLat,maxLon,maxLat o un perfil (--profile)");
    else
    {
      if (b.MinLon < -180 || b.MaxLon > 180 || b.MinLat < -90 || b.MaxLat > 90) errors.Add("bbox fuera de rango");
      if (b.MinLon >= b.MaxLon || b.MinLat >= b.MaxLat) errors.Add("bbox: min debe ser menor que max");
      if (Math.Max(Math.Abs(b.MinLat), Math.Abs(b.MaxLat)) > 85) errors.Add("bbox: latitudes > 85° no soportadas (EPSG:3857)");
    }

    if (BufferMeters < 0) errors.Add("buffer-m debe ser ≥ 0");
    if (Highways is null || Highways.Count == 0) errors.Add("se requiere al menos un tipo de vía (highways)");
    if (SplitWaysAt3857 < 0) errors.Add("split-ways-at debe ser ≥ 0");
    if (SegmentizeMeters <= 0) errors.Add("segmentize-m debe ser > 0");
    if (MinSegmentMeters < 0) errors.Add("min-segment-m debe ser ≥ 0");
    if (SnapDegrees < 1e-6 || SnapDegrees > 0.01) errors.Add("snap-deg debe estar entre 1e-6 y 0.01 (valores menores causan desbordamiento numérico en la llave natural)");
    if (DefaultCostFactor <= 0 || (CostFactors?.Values.Any(v => v <= 0) ?? false)) errors.Add("los factores de costo deben ser > 0");

    if (errors.Count > 0)
      throw new ArgumentException("Parámetros inválidos:\n  - " + string.Join("\n  - ", errors));

    Highways = Highways!.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().ToList();
    CostFactors = new Dictionary<string, double>(CostFactors ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase);
  }
}
