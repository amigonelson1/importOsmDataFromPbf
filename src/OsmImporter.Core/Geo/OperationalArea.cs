using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using OsmImporter.Core.Options;

namespace OsmImporter.Core.Geo;

/// <summary>
/// Área operativa = bbox + buffer, evaluada en EPSG:3857 como el SQL original
/// (ST_Buffer(ST_Transform(ST_MakeEnvelope(...,4326),3857), buffer)).
/// </summary>
public sealed class OperationalArea
{
  private readonly IPreparedGeometry _prepared;
  private readonly Envelope _envelope;
  private readonly GeometryFactory _factory = new();

  /// <summary>Buffer efectivamente aplicado en unidades EPSG:3857.</summary>
  public double Buffer3857 { get; }

  public Geometry Geometry3857 { get; }

  public OperationalArea(BuildOptions options)
  {
    var b = options.BBox ?? throw new ArgumentException("bbox requerido");
    var (x1, y1) = Geodesy.ToWebMercator(b.MinLon, b.MinLat);
    var (x2, y2) = Geodesy.ToWebMercator(b.MaxLon, b.MaxLat);

    // En 3857 la escala es 1/cos(lat). Para que el buffer sea de al menos N metros reales en todo el
    // bbox se usa la latitud de mayor valor absoluto (la de mayor distorsión).
    if (options.LegacyBuffer)
    {
      Buffer3857 = options.BufferMeters;
    }
    else
    {
      var maxAbsLat = Math.Min(85, Math.Max(Math.Abs(b.MinLat), Math.Abs(b.MaxLat)) + options.BufferMeters / 111_000.0);
      Buffer3857 = options.BufferMeters / Math.Cos(maxAbsLat * Math.PI / 180);
    }

    var envelope = _factory.ToGeometry(new Envelope(x1, x2, y1, y2));
    Geometry3857 = Buffer3857 > 0 ? envelope.Buffer(Buffer3857) : envelope; // 8 segmentos por cuadrante, igual que PostGIS
    _envelope = Geometry3857.EnvelopeInternal;
    _prepared = PreparedGeometryFactory.Prepare(Geometry3857);
  }

  /// <summary>¿La línea (lon/lat) intersecta el área? Igual que ST_Intersects(l.way, oa.geom).</summary>
  public bool Intersects(IReadOnlyList<(double Lon, double Lat)> lonLat)
  {
    if (lonLat.Count < 2) return false;

    var coords = new Coordinate[lonLat.Count];
    var env = new Envelope();
    for (var i = 0; i < lonLat.Count; i++)
    {
      var (x, y) = Geodesy.ToWebMercator(lonLat[i].Lon, lonLat[i].Lat);
      coords[i] = new Coordinate(x, y);
      env.ExpandToInclude(x, y);
    }

    if (!_envelope.Intersects(env)) return false;
    if (_envelope.Contains(env) && Buffer3857 == 0) return true;

    return _prepared.Intersects(_factory.CreateLineString(coords));
  }
}
