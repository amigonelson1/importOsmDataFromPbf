using OsmImporter.Core.Geo;
using OsmImporter.Core.Output;

namespace OsmImporter.Publish.Compare;

public sealed class HighwayParity
{
  public string Highway { get; init; } = default!;
  public long DbSegments { get; set; }
  public long GpkgSegments { get; set; }
  public double DbKm { get; set; }
  public double GpkgKm { get; set; }
}

public sealed class SampleSegment
{
  public long Id { get; init; }
  public long OsmId { get; init; }
  public string Highway { get; init; } = default!;
  public string Wkt { get; init; } = default!;
}

public sealed class ParityReport
{
  public string Target { get; init; } = default!;
  public string Gpkg { get; init; } = default!;
  public Dictionary<string, string> Diagnostics { get; init; } = [];
  public string? GpkgSegmentizeMethod { get; init; }

  public long DbNodes { get; set; }
  public long GpkgNodes { get; set; }
  public long NodesMatched { get; set; }
  public double NodesMatchedPercentOfDb { get; set; }

  public long DbSegments { get; set; }
  public long GpkgSegments { get; set; }
  public long SegmentsMatched { get; set; }
  public long SegmentsMatchedWithinOneCell { get; set; }
  public double SegmentsMatchedPercentOfDb { get; set; }
  public double SegmentsMatchedPercentOfGpkg { get; set; }

  public long DbOsmWays { get; set; }
  public long GpkgOsmWays { get; set; }
  public long OsmWaysOnlyInDb { get; set; }
  public long OsmWaysOnlyInGpkg { get; set; }
  public List<long> SampleOsmWaysOnlyInDb { get; set; } = [];
  public List<long> SampleOsmWaysOnlyInGpkg { get; set; } = [];

  public double DbKm { get; set; }
  public double GpkgKm { get; set; }
  public List<HighwayParity> ByHighway { get; set; } = [];

  public List<SampleSegment> SampleOnlyInDb { get; set; } = [];
  public List<SampleSegment> SampleOnlyInGpkg { get; set; } = [];
}

/// <summary>
/// Cruza la red existente en el destino con el GeoPackage usando las llaves naturales
/// (OsmId + extremos snapeados). No depende de los Ids del destino. Es la prueba de paridad
/// del motor .NET contra el SQL original y la base del diff del modo sync.
/// </summary>
public static class ParityComparer
{
  private const int SampleSize = 25;

  public static ParityReport Compare(GeoPackageReader gpkg, ExistingNetwork db, string target, Dictionary<string, string> diagnostics)
  {
    var snap = gpkg.SnapDegrees;
    var report = new ParityReport
    {
      Target = target,
      Gpkg = gpkg.Path,
      Diagnostics = diagnostics,
      GpkgSegmentizeMethod = TryGetSegmentizeMethod(gpkg),
      DbNodes = db.Nodes.Count,
      DbSegments = db.Segments.Count,
    };

    // ---------------- nodos
    var gpkgNodeKeys = new HashSet<long>();
    foreach (var n in gpkg.Nodes()) gpkgNodeKeys.Add(n.Key);
    report.GpkgNodes = gpkgNodeKeys.Count;
    report.NodesMatched = db.Nodes.LongCount(n => gpkgNodeKeys.Contains(GridKey.KeyFor(n.Lon, n.Lat, snap)));
    gpkgNodeKeys = null!;

    // ---------------- segmentos (multiconjunto por llave natural)
    var byHighway = new Dictionary<string, HighwayParity>();
    HighwayParity Hp(string h) => byHighway.TryGetValue(h, out var p) ? p : byHighway[h] = new HighwayParity { Highway = h };

    var gpkgKeys = new Dictionary<(long, long, long), int>();
    var gpkgByOsm = new Dictionary<long, List<(long From, long To)>>();
    var gpkgUnmatchedCandidates = new List<GpkgSegment>();
    long gpkgCount = 0;

    foreach (var s in gpkg.Segments())
    {
      gpkgCount++;
      var k = (s.OsmId, s.FromNodeKey, s.ToNodeKey);
      gpkgKeys[k] = gpkgKeys.GetValueOrDefault(k) + 1;
      var hp = Hp(s.Highway);
      hp.GpkgSegments++;
      hp.GpkgKm += s.LengthMeters / 1000;
      report.GpkgKm += s.LengthMeters / 1000;
    }
    report.GpkgSegments = gpkgCount;

    var dbOsm = new HashSet<long>();
    var unmatchedDb = new List<(ExistingSegment Seg, long From, long To)>();
    foreach (var s in db.Segments)
    {
      dbOsm.Add(s.OsmId);
      var hp = Hp(s.Highway);
      hp.DbSegments++;
      hp.DbKm += s.LengthMeters / 1000;
      report.DbKm += s.LengthMeters / 1000;

      var k = (s.OsmId, GridKey.KeyFor(s.X1, s.Y1, snap), GridKey.KeyFor(s.X2, s.Y2, snap));
      if (gpkgKeys.TryGetValue(k, out var c) && c > 0)
      {
        gpkgKeys[k] = c - 1;
        report.SegmentsMatched++;
      }
      else
      {
        unmatchedDb.Add((s, k.Item2, k.Item3));
      }
    }

    // Tolerancia de ±1 celda en cada extremo: separa "mismo segmento con redondeo distinto"
    // (p. ej. coordenadas que pasaron por EPSG:3857 en osm2pgsql) de diferencias reales.
    foreach (var ((osm, from, to), count) in gpkgKeys)
    {
      if (count <= 0) continue;
      if (!gpkgByOsm.TryGetValue(osm, out var list)) gpkgByOsm[osm] = list = [];
      for (var i = 0; i < count; i++) list.Add((from, to));
    }

    var stillUnmatchedDb = new List<ExistingSegment>();
    foreach (var (seg, from, to) in unmatchedDb)
    {
      if (gpkgByOsm.TryGetValue(seg.OsmId, out var candidates))
      {
        var idx = candidates.FindIndex(c => Near(c.From, from) && Near(c.To, to));
        if (idx >= 0)
        {
          candidates.RemoveAt(idx);
          report.SegmentsMatchedWithinOneCell++;
          continue;
        }
      }
      stillUnmatchedDb.Add(seg);
    }

    report.SampleOnlyInDb = stillUnmatchedDb.Take(SampleSize).Select(s => new SampleSegment
    {
      Id = s.Id, OsmId = s.OsmId, Highway = s.Highway, Wkt = Wkt(s.X1, s.Y1, s.X2, s.Y2),
    }).ToList();

    // Muestras de segmentos que solo están en el gpkg
    var remaining = new HashSet<(long, long, long)>(gpkgByOsm.SelectMany(kv => kv.Value.Select(v => (kv.Key, v.From, v.To))));
    var gpkgOsm = new HashSet<long>();
    foreach (var s in gpkg.Segments())
    {
      gpkgOsm.Add(s.OsmId);
      if (report.SampleOnlyInGpkg.Count < SampleSize && remaining.Contains((s.OsmId, s.FromNodeKey, s.ToNodeKey)))
        report.SampleOnlyInGpkg.Add(new SampleSegment { Id = s.Fid, OsmId = s.OsmId, Highway = s.Highway, Wkt = Wkt(s.X1, s.Y1, s.X2, s.Y2) });
    }

    // ---------------- nivel vía OSM (diferencias de selección: área, tipos de vía, versión del pbf)
    report.DbOsmWays = dbOsm.Count;
    report.GpkgOsmWays = gpkgOsm.Count;
    var onlyDb = dbOsm.Where(id => !gpkgOsm.Contains(id)).ToList();
    var onlyGpkg = gpkgOsm.Where(id => !dbOsm.Contains(id)).ToList();
    report.OsmWaysOnlyInDb = onlyDb.Count;
    report.OsmWaysOnlyInGpkg = onlyGpkg.Count;
    report.SampleOsmWaysOnlyInDb = onlyDb.Order().Take(SampleSize).ToList();
    report.SampleOsmWaysOnlyInGpkg = onlyGpkg.Order().Take(SampleSize).ToList();

    report.NodesMatchedPercentOfDb = Pct(report.NodesMatched, report.DbNodes);
    report.SegmentsMatchedPercentOfDb = Pct(report.SegmentsMatched + report.SegmentsMatchedWithinOneCell, report.DbSegments);
    report.SegmentsMatchedPercentOfGpkg = Pct(report.SegmentsMatched + report.SegmentsMatchedWithinOneCell, report.GpkgSegments);
    report.DbKm = Math.Round(report.DbKm, 3);
    report.GpkgKm = Math.Round(report.GpkgKm, 3);
    report.ByHighway = byHighway.Values
      .Select(h => { h.DbKm = Math.Round(h.DbKm, 3); h.GpkgKm = Math.Round(h.GpkgKm, 3); return h; })
      .OrderByDescending(h => Math.Max(h.DbSegments, h.GpkgSegments)).ToList();

    return report;
  }

  private static bool Near(long a, long b)
  {
    var (ax, ay) = GridKey.Unpack(a);
    var (bx, by) = GridKey.Unpack(b);
    return Math.Abs(ax - bx) <= 1 && Math.Abs(ay - by) <= 1;
  }

  private static double Pct(long part, long total) => total == 0 ? 0 : Math.Round(100.0 * part / total, 3);

  private static string Wkt(double x1, double y1, double x2, double y2) =>
    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"LINESTRING({x1} {y1}, {x2} {y2})");

  private static string? TryGetSegmentizeMethod(GeoPackageReader gpkg)
  {
    if (!gpkg.Info.TryGetValue("options_json", out var json)) return null;
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    return doc.RootElement.TryGetProperty("segmentizeMethod", out var m) ? m.GetString() : null;
  }
}
