using Microsoft.Data.Sqlite;
using OsmImporter.Core.Graph;

namespace OsmImporter.Core.Output;

/// <summary>
/// Escribe el grafo como GeoPackage (SQLite estándar OGC, se abre directo en QGIS):
///   road_nodes     (POINT)      — nodo snapeado, node_key, valence
///   road_segments  (LINESTRING) — micro-segmento, segment_key, atributos, nodos origen/destino
///   build_info     (atributos)  — parámetros, origen y estadísticas de la construcción
/// </summary>
public static class GeoPackageWriter
{
  public const string NodesTable = "road_nodes";
  public const string SegmentsTable = "road_segments";
  public const string RestrictionsTable = "road_turn_restrictions";
  public const string InfoTable = "build_info";

  private const string Wgs84Wkt =
    "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563,AUTHORITY[\"EPSG\",\"7030\"]]," +
    "AUTHORITY[\"EPSG\",\"6326\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]]," +
    "UNIT[\"degree\",0.0174532925199433,AUTHORITY[\"EPSG\",\"9122\"]],AUTHORITY[\"EPSG\",\"4326\"]]";

  public static void Write(string path, RoadGraph graph, IReadOnlyDictionary<string, string> info, CancellationToken ct = default)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    var temp = path + ".tmp";
    if (File.Exists(temp)) File.Delete(temp);
    var completed = false;

    try
    {
      using (var conn = new SqliteConnection($"Data Source={temp};Pooling=False"))
      {
        conn.Open();
        ct.ThrowIfCancellationRequested();
        Exec(conn, "PRAGMA application_id = 1196444487;"); // 'GPKG'
        Exec(conn, "PRAGMA user_version = 10400;");          // GeoPackage 1.4
        Exec(conn, "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA page_size = 65536;");
        Exec(conn, "PRAGMA cache_size = -262144; PRAGMA temp_store = MEMORY; PRAGMA locking_mode = EXCLUSIVE;");

        CreateCoreTables(conn);
        CreateDataTables(conn);
        var rtree = TryCreateRtree(conn, NodesTable) & TryCreateRtree(conn, SegmentsTable);

        using (var tx = conn.BeginTransaction())
        {
          WriteNodes(conn, tx, graph, rtree, ct);
          WriteSegments(conn, tx, graph, rtree, ct);
          WriteRestrictions(conn, tx, graph, ct);
          WriteInfo(conn, tx, info);
          RegisterContents(conn, tx, graph);
          tx.Commit();
        }

        ct.ThrowIfCancellationRequested();
        Log.Info("  creando índices...");
        Exec(conn, $"CREATE UNIQUE INDEX ux_{NodesTable}_key ON {NodesTable}(node_key);");
        Exec(conn, $"CREATE UNIQUE INDEX ux_{SegmentsTable}_key ON {SegmentsTable}(segment_key);");
        Exec(conn, $"CREATE INDEX ix_{SegmentsTable}_osm ON {SegmentsTable}(osm_id);");
        Exec(conn, $"CREATE INDEX ix_{SegmentsTable}_highway ON {SegmentsTable}(highway);");
        Exec(conn, $"CREATE INDEX ix_{RestrictionsTable}_via ON {RestrictionsTable}(via_node_key);");
        Exec(conn, $"CREATE INDEX ix_{RestrictionsTable}_from ON {RestrictionsTable}(from_osm_id);");
        Exec(conn, "ANALYZE;");
      }

      if (File.Exists(path)) File.Delete(path);
      File.Move(temp, path);
      completed = true;
    }
    finally
    {
      if (!completed && File.Exists(temp))
      {
        try { File.Delete(temp); } catch { /* ignore */ }
      }
    }
  }

  private static void CreateCoreTables(SqliteConnection conn)
  {
    Exec(conn, """
      CREATE TABLE gpkg_spatial_ref_sys (
        srs_name TEXT NOT NULL, srs_id INTEGER NOT NULL PRIMARY KEY, organization TEXT NOT NULL,
        organization_coordsys_id INTEGER NOT NULL, definition TEXT NOT NULL, description TEXT);
      CREATE TABLE gpkg_contents (
        table_name TEXT NOT NULL PRIMARY KEY, data_type TEXT NOT NULL, identifier TEXT UNIQUE,
        description TEXT DEFAULT '', last_change DATETIME NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
        min_x DOUBLE, min_y DOUBLE, max_x DOUBLE, max_y DOUBLE, srs_id INTEGER,
        CONSTRAINT fk_gc_r_srs_id FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id));
      CREATE TABLE gpkg_geometry_columns (
        table_name TEXT NOT NULL, column_name TEXT NOT NULL, geometry_type_name TEXT NOT NULL,
        srs_id INTEGER NOT NULL, z TINYINT NOT NULL, m TINYINT NOT NULL,
        CONSTRAINT pk_geom_cols PRIMARY KEY (table_name, column_name),
        CONSTRAINT uk_gc_table_name UNIQUE (table_name),
        CONSTRAINT fk_gc_tn FOREIGN KEY (table_name) REFERENCES gpkg_contents(table_name),
        CONSTRAINT fk_gc_srs FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys (srs_id));
      CREATE TABLE gpkg_extensions (
        table_name TEXT, column_name TEXT, extension_name TEXT NOT NULL, definition TEXT NOT NULL, scope TEXT NOT NULL,
        CONSTRAINT ge_tce UNIQUE (table_name, column_name, extension_name));
      INSERT INTO gpkg_spatial_ref_sys VALUES ('Undefined cartesian SRS', -1, 'NONE', -1, 'undefined', 'undefined cartesian coordinate reference system');
      INSERT INTO gpkg_spatial_ref_sys VALUES ('Undefined geographic SRS', 0, 'NONE', 0, 'undefined', 'undefined geographic coordinate reference system');
      """);

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO gpkg_spatial_ref_sys VALUES ('WGS 84 geodetic', 4326, 'EPSG', 4326, $def, 'longitude/latitude coordinates in decimal degrees on the WGS 84 spheroid');";
    cmd.Parameters.AddWithValue("$def", Wgs84Wkt);
    cmd.ExecuteNonQuery();
  }

  private static void CreateDataTables(SqliteConnection conn)
  {
    Exec(conn, $"""
      CREATE TABLE {NodesTable} (
        fid INTEGER PRIMARY KEY,
        geom POINT NOT NULL,
        node_key INTEGER NOT NULL,
        valence INTEGER NOT NULL);
      CREATE TABLE {SegmentsTable} (
        fid INTEGER PRIMARY KEY,
        geom LINESTRING NOT NULL,
        segment_key TEXT NOT NULL,
        osm_id INTEGER NOT NULL,
        name TEXT,
        highway TEXT NOT NULL,
        is_oneway INTEGER NOT NULL,
        lanes INTEGER,
        max_speed INTEGER,
        surface TEXT,
        smoothness TEXT,
        tracktype TEXT,
        bridge INTEGER NOT NULL DEFAULT 0,
        tunnel INTEGER NOT NULL DEFAULT 0,
        layer INTEGER NOT NULL DEFAULT 0,
        max_weight REAL,
        max_height REAL,
        max_width REAL,
        from_node_fid INTEGER NOT NULL REFERENCES {NodesTable}(fid),
        to_node_fid INTEGER NOT NULL REFERENCES {NodesTable}(fid),
        from_node_key INTEGER NOT NULL,
        to_node_key INTEGER NOT NULL,
        length_m REAL NOT NULL,
        travel_cost REAL NOT NULL);
      CREATE TABLE {RestrictionsTable} (
        fid INTEGER PRIMARY KEY,
        relation_id INTEGER NOT NULL,
        from_osm_id INTEGER NOT NULL,
        via_node_key INTEGER NOT NULL,
        to_osm_id INTEGER NOT NULL,
        restriction TEXT NOT NULL);
      CREATE TABLE {InfoTable} (
        fid INTEGER PRIMARY KEY AUTOINCREMENT,
        key TEXT NOT NULL UNIQUE,
        value TEXT);
      """);
  }

  private static void WriteNodes(SqliteConnection conn, SqliteTransaction tx, RoadGraph graph, bool rtree, CancellationToken ct)
  {
    using (var cmd = conn.CreateCommand())
    {
      cmd.Transaction = tx;
      cmd.CommandText = $"INSERT INTO {NodesTable} (fid, geom, node_key, valence) VALUES ($fid, $geom, $key, $val);";
      var pFid = cmd.Parameters.Add("$fid", SqliteType.Integer);
      var pGeom = cmd.Parameters.Add("$geom", SqliteType.Blob);
      var pKey = cmd.Parameters.Add("$key", SqliteType.Integer);
      var pVal = cmd.Parameters.Add("$val", SqliteType.Integer);
      cmd.Prepare();

      var count = 0;
      foreach (var n in graph.Nodes)
      {
        if (++count % 50_000 == 0) ct.ThrowIfCancellationRequested();
        pFid.Value = n.Fid;
        pGeom.Value = GpkgGeometry.Point(n.Lon, n.Lat);
        pKey.Value = n.Key;
        pVal.Value = n.Valence;
        cmd.ExecuteNonQuery();
      }
    }

    if (rtree)
    {
      using var idx = RtreeInsert(conn, tx, NodesTable, true);
      if (idx != null)
      {
        var count = 0;
        foreach (var n in graph.Nodes)
        {
          if (++count % 50_000 == 0) ct.ThrowIfCancellationRequested();
          idx.Insert(n.Fid, n.Lon, n.Lon, n.Lat, n.Lat);
        }
      }
    }

    Log.Info($"  {graph.Nodes.Count:N0} nodos escritos e indexados");
  }

  private static void WriteSegments(SqliteConnection conn, SqliteTransaction tx, RoadGraph graph, bool rtree, CancellationToken ct)
  {
    using (var cmd = conn.CreateCommand())
    {
      cmd.Transaction = tx;
      cmd.CommandText = $"""
        INSERT INTO {SegmentsTable}
          (fid, geom, segment_key, osm_id, name, highway, is_oneway, lanes, max_speed,
           surface, smoothness, tracktype, bridge, tunnel, layer, max_weight, max_height, max_width,
           from_node_fid, to_node_fid, from_node_key, to_node_key, length_m, travel_cost)
        VALUES ($fid, $geom, $key, $osm, $name, $hw, $ow, $lanes, $ms,
                $surf, $smooth, $tt, $br, $tu, $lay, $mw, $mh, $mwd,
                $ff, $tf, $fk, $tk, $len, $cost);
        """;
      string[] names = [
        "$fid", "$geom", "$key", "$osm", "$name", "$hw", "$ow", "$lanes", "$ms",
        "$surf", "$smooth", "$tt", "$br", "$tu", "$lay", "$mw", "$mh", "$mwd",
        "$ff", "$tf", "$fk", "$tk", "$len", "$cost"
      ];
      var p = names.Select(n => cmd.Parameters.Add(n, SqliteType.Text)).ToArray();
      p[1].SqliteType = SqliteType.Blob;
      cmd.Prepare();

      var count = 0;
      foreach (var s in graph.Segments)
      {
        if (++count % 50_000 == 0) ct.ThrowIfCancellationRequested();
        p[0].Value = s.Fid;
        p[1].Value = GpkgGeometry.Line(s.X1, s.Y1, s.X2, s.Y2);
        p[2].Value = s.Key;
        p[3].Value = s.Way.OsmId;
        p[4].Value = (object?)s.Way.Name ?? DBNull.Value;
        p[5].Value = s.Way.Highway;
        p[6].Value = s.Way.IsOneWay ? 1 : 0;
        p[7].Value = (object?)s.Way.Lanes ?? DBNull.Value;
        p[8].Value = (object?)s.Way.MaxSpeed ?? DBNull.Value;
        p[9].Value = (object?)s.Way.Surface ?? DBNull.Value;
        p[10].Value = (object?)s.Way.Smoothness ?? DBNull.Value;
        p[11].Value = (object?)s.Way.TrackType ?? DBNull.Value;
        p[12].Value = s.Way.Bridge ? 1 : 0;
        p[13].Value = s.Way.Tunnel ? 1 : 0;
        p[14].Value = s.Way.Layer;
        p[15].Value = (object?)s.Way.MaxWeight ?? DBNull.Value;
        p[16].Value = (object?)s.Way.MaxHeight ?? DBNull.Value;
        p[17].Value = (object?)s.Way.MaxWidth ?? DBNull.Value;
        p[18].Value = s.FromNode.Fid;
        p[19].Value = s.ToNode.Fid;
        p[20].Value = s.FromKey;
        p[21].Value = s.ToKey;
        p[22].Value = s.LengthMeters;
        p[23].Value = s.TravelCost;
        cmd.ExecuteNonQuery();

        if (count % 1_000_000 == 0) Log.Debug($"  {count:N0} segmentos...");
      }
    }

    if (rtree)
    {
      using var idx = RtreeInsert(conn, tx, SegmentsTable, true);
      if (idx != null)
      {
        var count = 0;
        foreach (var s in graph.Segments)
        {
          if (++count % 50_000 == 0) ct.ThrowIfCancellationRequested();
          idx.Insert(s.Fid, Math.Min(s.X1, s.X2), Math.Max(s.X1, s.X2), Math.Min(s.Y1, s.Y2), Math.Max(s.Y1, s.Y2));
          if (count % 1_000_000 == 0) Log.Debug($"  {count:N0} segmentos indexados...");
        }
      }
    }

    Log.Info($"  {graph.Segments.Count:N0} segmentos escritos e indexados");
  }

  private static void WriteRestrictions(SqliteConnection conn, SqliteTransaction tx, RoadGraph graph, CancellationToken ct)
  {
    if (graph.Restrictions.Count == 0) return;

    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = $"""
      INSERT INTO {RestrictionsTable} (fid, relation_id, from_osm_id, via_node_key, to_osm_id, restriction)
      VALUES ($fid, $rel, $from, $via, $to, $res);
      """;
    string[] names = ["$fid", "$rel", "$from", "$via", "$to", "$res"];
    var p = names.Select(n => cmd.Parameters.Add(n, SqliteType.Text)).ToArray();
    cmd.Prepare();

    var count = 0;
    long fid = 1;
    foreach (var r in graph.Restrictions)
    {
      if (++count % 50_000 == 0) ct.ThrowIfCancellationRequested();
      p[0].Value = fid++;
      p[1].Value = r.RelationId;
      p[2].Value = r.FromOsmId;
      p[3].Value = r.ViaNodeKey;
      p[4].Value = r.ToOsmId;
      p[5].Value = r.Restriction;
      cmd.ExecuteNonQuery();
    }
    Log.Info($"  {graph.Restrictions.Count:N0} restricciones de giro escritas");
  }

  private static void WriteInfo(SqliteConnection conn, SqliteTransaction tx, IReadOnlyDictionary<string, string> info)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = $"INSERT INTO {InfoTable} (key, value) VALUES ($k, $v);";
    var k = cmd.Parameters.Add("$k", SqliteType.Text);
    var v = cmd.Parameters.Add("$v", SqliteType.Text);
    foreach (var (key, value) in info)
    {
      k.Value = key;
      v.Value = value;
      cmd.ExecuteNonQuery();
    }
  }

  private static void RegisterContents(SqliteConnection conn, SqliteTransaction tx, RoadGraph graph)
  {
    double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
    foreach (var n in graph.Nodes)
    {
      minX = Math.Min(minX, n.Lon); maxX = Math.Max(maxX, n.Lon);
      minY = Math.Min(minY, n.Lat); maxY = Math.Max(maxY, n.Lat);
    }
    if (graph.Nodes.Count == 0) minX = minY = maxX = maxY = 0;

    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = $"""
      INSERT INTO gpkg_contents (table_name, data_type, identifier, description, min_x, min_y, max_x, max_y, srs_id) VALUES
        ('{NodesTable}', 'features', '{NodesTable}', 'Nodos del grafo vial (coordenada snapeada)', $minx, $miny, $maxx, $maxy, 4326),
        ('{SegmentsTable}', 'features', '{SegmentsTable}', 'Micro-segmentos del grafo vial', $minx, $miny, $maxx, $maxy, 4326),
        ('{RestrictionsTable}', 'attributes', '{RestrictionsTable}', 'Restricciones de giro OSM (type=restriction)', NULL, NULL, NULL, NULL, NULL),
        ('{InfoTable}', 'attributes', '{InfoTable}', 'Parámetros y estadísticas de la construcción', NULL, NULL, NULL, NULL, NULL);
      INSERT INTO gpkg_geometry_columns VALUES ('{NodesTable}', 'geom', 'POINT', 4326, 0, 0);
      INSERT INTO gpkg_geometry_columns VALUES ('{SegmentsTable}', 'geom', 'LINESTRING', 4326, 0, 0);
      """;
    cmd.Parameters.AddWithValue("$minx", minX);
    cmd.Parameters.AddWithValue("$miny", minY);
    cmd.Parameters.AddWithValue("$maxx", maxX);
    cmd.Parameters.AddWithValue("$maxy", maxY);
    cmd.ExecuteNonQuery();
  }

  /// <summary>Índice espacial (extensión gpkg_rtree_index). Opcional: si falla, el gpkg sigue siendo válido.</summary>
  private static bool TryCreateRtree(SqliteConnection conn, string table)
  {
    try
    {
      Exec(conn, $"CREATE VIRTUAL TABLE rtree_{table}_geom USING rtree(id, minx, maxx, miny, maxy);");
      Exec(conn, $"INSERT INTO gpkg_extensions VALUES ('{table}', 'geom', 'gpkg_rtree_index', 'http://www.geopackage.org/spec120/#extension_rtree', 'write-only');");
      return true;
    }
    catch (SqliteException ex)
    {
      Log.Warn($"no se pudo crear el índice espacial de {table} ({ex.Message}); el archivo sigue siendo válido.");
      return false;
    }
  }

  private static RtreeWriter? RtreeInsert(SqliteConnection conn, SqliteTransaction tx, string table, bool enabled) =>
    enabled ? new RtreeWriter(conn, tx, table) : null;

  private sealed class RtreeWriter : IDisposable
  {
    private readonly SqliteCommand _cmd;
    private readonly SqliteParameter[] _p;

    public RtreeWriter(SqliteConnection conn, SqliteTransaction tx, string table)
    {
      _cmd = conn.CreateCommand();
      _cmd.Transaction = tx;
      _cmd.CommandText = $"INSERT INTO rtree_{table}_geom VALUES ($id, $a, $b, $c, $d);";
      _p = new[] { "$id", "$a", "$b", "$c", "$d" }.Select(n => _cmd.Parameters.Add(n, SqliteType.Real)).ToArray();
      _p[0].SqliteType = SqliteType.Integer;
      _cmd.Prepare();
    }

    public void Insert(long id, double minX, double maxX, double minY, double maxY)
    {
      _p[0].Value = id; _p[1].Value = minX; _p[2].Value = maxX; _p[3].Value = minY; _p[4].Value = maxY;
      _cmd.ExecuteNonQuery();
    }

    public void Dispose() => _cmd.Dispose();
  }

  private static void Exec(SqliteConnection conn, string sql)
  {
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
  }
}
