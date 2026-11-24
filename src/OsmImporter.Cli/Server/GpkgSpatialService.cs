using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OsmImporter.Core.Output;

namespace OsmImporter.Cli.Server;

public sealed class GpkgSpatialService
{
  private static readonly HashSet<string> Tier1Highways = new(StringComparer.OrdinalIgnoreCase)
  {
    "motorway", "motorway_link", "trunk", "trunk_link"
  };

  private static readonly HashSet<string> Tier2Highways = new(StringComparer.OrdinalIgnoreCase)
  {
    "motorway", "motorway_link", "trunk", "trunk_link",
    "primary", "primary_link"
  };

  private static readonly HashSet<string> Tier3Highways = new(StringComparer.OrdinalIgnoreCase)
  {
    "motorway", "motorway_link", "trunk", "trunk_link",
    "primary", "primary_link", "secondary", "secondary_link",
    "tertiary", "tertiary_link"
  };

  public record GpkgFileSummary(
    string FileName,
    string FullPath,
    long SizeBytes,
    DateTime LastModifiedUtc,
    bool HasReport,
    bool HasParity);

  public record GpkgBounds(double MinLon, double MinLat, double MaxLon, double MaxLat);

  public record GpkgMetadata(
    string FileName,
    long NodeCount,
    long SegmentCount,
    GpkgBounds? Bounds,
    Dictionary<string, string> Info,
    JsonElement? Report,
    JsonElement? Parity);

  public static List<GpkgFileSummary> ListFiles(string outputDir)
  {
    if (!Directory.Exists(outputDir)) return [];

    var files = Directory.GetFiles(outputDir, "*.gpkg", SearchOption.TopDirectoryOnly);
    var list = new List<GpkgFileSummary>();

    foreach (var file in files)
    {
      if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
      var fi = new FileInfo(file);
      var reportPath = file + ".report.json";
      var parityPath = file + ".parity.json";

      list.Add(new GpkgFileSummary(
        fi.Name,
        fi.FullName,
        fi.Length,
        fi.LastWriteTimeUtc,
        File.Exists(reportPath),
        File.Exists(parityPath)));
    }

    return list.OrderByDescending(f => f.LastModifiedUtc).ToList();
  }

  public static GpkgMetadata? GetMetadata(string gpkgPath)
  {
    if (!File.Exists(gpkgPath)) return null;

    var fi = new FileInfo(gpkgPath);
    var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    long nodeCount = 0;
    long segmentCount = 0;
    GpkgBounds? bounds = null;

    using (var conn = new SqliteConnection($"Data Source={gpkgPath};Mode=ReadOnly;Pooling=False"))
    {
      conn.Open();

      // Leer build_info si existe
      try
      {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM build_info";
        using var r = cmd.ExecuteReader();
        while (r.Read()) info[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
      }
      catch { /* opcional */ }

      // Conteos
      try
      {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM road_nodes";
        nodeCount = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
      }
      catch { }

      try
      {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM road_segments";
        segmentCount = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
      }
      catch { }

      // Bounding box desde gpkg_contents
      try
      {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT min_x, min_y, max_x, max_y FROM gpkg_contents WHERE table_name = 'road_segments'";
        using var r = cmd.ExecuteReader();
        if (r.Read() && !r.IsDBNull(0) && !r.IsDBNull(1) && !r.IsDBNull(2) && !r.IsDBNull(3))
        {
          bounds = new GpkgBounds(r.GetDouble(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3));
        }
      }
      catch { }
    }

    JsonElement? reportObj = null;
    var reportPath = gpkgPath + ".report.json";
    if (File.Exists(reportPath))
    {
      try
      {
        var jsonText = File.ReadAllText(reportPath);
        using var doc = JsonDocument.Parse(jsonText);
        reportObj = doc.RootElement.Clone();
      }
      catch { }
    }

    JsonElement? parityObj = null;
    var parityPath = gpkgPath + ".parity.json";
    if (File.Exists(parityPath))
    {
      try
      {
        var jsonText = File.ReadAllText(parityPath);
        using var doc = JsonDocument.Parse(jsonText);
        parityObj = doc.RootElement.Clone();
      }
      catch { }
    }

    return new GpkgMetadata(fi.Name, nodeCount, segmentCount, bounds, info, reportObj, parityObj);
  }

  public static object GetSegmentsGeoJson(
    string gpkgPath,
    double minLon,
    double minLat,
    double maxLon,
    double maxLat,
    double zoom,
    string? highwayFilter = null,
    int limit = 12000)
  {
    if (!File.Exists(gpkgPath))
      return new { type = "FeatureCollection", features = Array.Empty<object>() };

    limit = Math.Clamp(limit, 100, 25000);

    // Filtros explícitos del usuario (checkboxes)
    HashSet<string>? userFilters = null;
    if (!string.IsNullOrWhiteSpace(highwayFilter))
    {
      var parts = highwayFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      if (parts.Length > 0)
      {
        userFilters = new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
      }
    }

    // Nivel de detalle por zoom (solo si el usuario no especificó filtro manual)
    HashSet<string>? zoomAllowedHighways = null;
    if (userFilters == null)
    {
      if (zoom < 7) zoomAllowedHighways = Tier1Highways;
      else if (zoom < 9) zoomAllowedHighways = Tier2Highways;
      else if (zoom < 11) zoomAllowedHighways = Tier3Highways;
      // Para zoom >= 11: todas las vías permitidas
    }

    using var conn = new SqliteConnection($"Data Source={gpkgPath};Mode=ReadOnly;Pooling=False");
    conn.Open();

    // Comprobar si existe el índice RTree
    bool hasRtree = false;
    using (var checkCmd = conn.CreateCommand())
    {
      checkCmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='rtree_road_segments_geom'";
      hasRtree = Convert.ToInt64(checkCmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    using var cmd = conn.CreateCommand();

    string query;
    if (hasRtree)
    {
      query = """
        SELECT s.fid, s.name, s.highway, s.is_oneway, s.lanes, s.max_speed,
               s.length_m, s.travel_cost, s.osm_id, s.from_node_key, s.to_node_key, s.geom
        FROM road_segments s
        JOIN rtree_road_segments_geom r ON s.fid = r.id
        WHERE r.minx <= $maxLon AND r.maxx >= $minLon AND r.miny <= $maxLat AND r.maxy >= $minLat
        """;
    }
    else
    {
      query = """
        SELECT fid, name, highway, is_oneway, lanes, max_speed,
               length_m, travel_cost, osm_id, from_node_key, to_node_key, geom
        FROM road_segments
        """;
    }

    cmd.CommandText = query + $" LIMIT {limit * 3};";
    cmd.Parameters.AddWithValue("$minLon", minLon);
    cmd.Parameters.AddWithValue("$maxLon", maxLon);
    cmd.Parameters.AddWithValue("$minLat", minLat);
    cmd.Parameters.AddWithValue("$maxLat", maxLat);

    var matchingHighPriority = new List<object>();
    var fallbackOthers = new List<object>();

    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
      var highway = r.GetString(2);

      // Si el usuario desmarcó este highway en la UI, ignorar siempre
      if (userFilters != null && !userFilters.Contains(highway))
        continue;

      var blob = (byte[])r[11];
      double[] coords;
      try
      {
        coords = GpkgGeometry.ReadCoordinates(blob);
      }
      catch
      {
        continue;
      }

      if (coords.Length < 4) continue;

      var coordList = new List<double[]>();
      for (int i = 0; i < coords.Length; i += 2)
      {
        coordList.Add([Math.Round(coords[i], 6), Math.Round(coords[i + 1], 6)]);
      }

      var fid = r.GetInt64(0);
      var name = r.IsDBNull(1) ? null : r.GetString(1);
      var isOneway = r.GetInt64(3) != 0;
      var lanes = r.IsDBNull(4) ? (int?)null : r.GetInt32(4);
      var maxSpeed = r.IsDBNull(5) ? (int?)null : r.GetInt32(5);
      var lengthM = Math.Round(r.GetDouble(6), 1);
      var travelCost = Math.Round(r.GetDouble(7), 1);
      var osmId = r.GetInt64(8);
      var fromKey = r.GetInt64(9);
      var toKey = r.GetInt64(10);

      var feature = new
      {
        type = "Feature",
        id = fid,
        geometry = new
        {
          type = "LineString",
          coordinates = coordList
        },
        properties = new
        {
          fid,
          name,
          highway,
          is_oneway = isOneway,
          lanes,
          max_speed = maxSpeed,
          length_m = lengthM,
          travel_cost = travelCost,
          osm_id = osmId,
          from_key = fromKey,
          to_key = toKey
        }
      };

      // Si cumple con la jerarquía del zoom actual, va a alta prioridad
      if (zoomAllowedHighways == null || zoomAllowedHighways.Contains(highway))
      {
        matchingHighPriority.Add(feature);
        if (matchingHighPriority.Count >= limit) break;
      }
      else
      {
        // Guardar como fallback por si la zona no tiene vías de alta prioridad
        if (fallbackOthers.Count < limit)
        {
          fallbackOthers.Add(feature);
        }
      }
    }

    // Si la zona no tenía autopistas/primarias (como ciudades intermedias), usar fallback para que NUNCA quede vacío
    List<object> finalFeatures;
    if (matchingHighPriority.Count >= 20 || userFilters != null)
    {
      finalFeatures = matchingHighPriority;
    }
    else
    {
      // Unir ambas listas hasta el límite
      finalFeatures = matchingHighPriority;
      foreach (var fb in fallbackOthers)
      {
        if (finalFeatures.Count >= limit) break;
        finalFeatures.Add(fb);
      }
    }

    return new
    {
      type = "FeatureCollection",
      features = finalFeatures
    };
  }

  public static object GetNodesGeoJson(
    string gpkgPath,
    double minLon,
    double minLat,
    double maxLon,
    double maxLat,
    int limit = 3000)
  {
    if (!File.Exists(gpkgPath))
      return new { type = "FeatureCollection", features = Array.Empty<object>() };

    limit = Math.Clamp(limit, 50, 10000);

    using var conn = new SqliteConnection($"Data Source={gpkgPath};Mode=ReadOnly;Pooling=False");
    conn.Open();

    bool hasRtree = false;
    using (var checkCmd = conn.CreateCommand())
    {
      checkCmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='rtree_road_nodes_geom'";
      hasRtree = Convert.ToInt64(checkCmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    var features = new List<object>();

    using var cmd = conn.CreateCommand();
    string query;
    if (hasRtree)
    {
      query = """
        SELECT n.fid, n.node_key, n.valence, n.geom
        FROM road_nodes n
        JOIN rtree_road_nodes_geom r ON n.fid = r.id
        WHERE r.minx <= $maxLon AND r.maxx >= $minLon AND r.miny <= $maxLat AND r.maxy >= $minLat
        LIMIT $limit;
        """;
    }
    else
    {
      query = """
        SELECT fid, node_key, valence, geom
        FROM road_nodes
        LIMIT $limit;
        """;
    }

    cmd.CommandText = query;
    cmd.Parameters.AddWithValue("$minLon", minLon);
    cmd.Parameters.AddWithValue("$maxLon", maxLon);
    cmd.Parameters.AddWithValue("$minLat", minLat);
    cmd.Parameters.AddWithValue("$maxLat", maxLat);
    cmd.Parameters.AddWithValue("$limit", limit);

    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
      var blob = (byte[])r[3];
      double[] coords;
      try
      {
        coords = GpkgGeometry.ReadCoordinates(blob);
      }
      catch { continue; }

      if (coords.Length < 2) continue;

      var fid = r.GetInt64(0);
      var nodeKey = r.GetInt64(1);
      var valence = r.GetInt32(2);

      features.Add(new
      {
        type = "Feature",
        id = fid,
        geometry = new
        {
          type = "Point",
          coordinates = new[] { Math.Round(coords[0], 6), Math.Round(coords[1], 6) }
        },
        properties = new
        {
          fid,
          node_key = nodeKey,
          valence
        }
      });
    }

    return new
    {
      type = "FeatureCollection",
      features
    };
  }
}
