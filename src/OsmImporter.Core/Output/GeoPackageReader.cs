using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OsmImporter.Core.Output;

public readonly record struct GpkgNode(long Fid, long Key, double Lon, double Lat, int Valence);

public readonly record struct GpkgSegment(
  long Fid, string Key, long OsmId, string? Name, string Highway, bool IsOneWay, int? Lanes, int? MaxSpeed,
  long FromNodeFid, long ToNodeFid, long FromNodeKey, long ToNodeKey,
  double X1, double Y1, double X2, double Y2, double LengthMeters, double TravelCost,
  string? Surface = null, string? Smoothness = null, string? TrackType = null,
  bool Bridge = false, bool Tunnel = false, int Layer = 0,
  double? MaxWeight = null, double? MaxHeight = null, double? MaxWidth = null);

public readonly record struct GpkgRestriction(
  long Fid, long RelationId, long FromOsmId, long ViaNodeKey, long ToOsmId, string Restriction);

/// <summary>Lee el GeoPackage generado por <see cref="GeoPackageWriter"/> (streaming).</summary>
public sealed class GeoPackageReader : IDisposable
{
  private readonly SqliteConnection _conn;
  private readonly bool _hasExtendedColumns;
  private readonly bool _hasRestrictionsTable;

  public string Path { get; }
  public IReadOnlyDictionary<string, string> Info { get; }
  public long NodeCount { get; }
  public long SegmentCount { get; }
  public bool HasRestrictions => _hasRestrictionsTable;

  public double SnapDegrees => double.Parse(Info["snap_degrees"], CultureInfo.InvariantCulture);

  public GeoPackageReader(string path)
  {
    if (!File.Exists(path)) throw new FileNotFoundException($"No existe el GeoPackage: {path}");
    Path = System.IO.Path.GetFullPath(path);
    _conn = new SqliteConnection($"Data Source={Path};Mode=ReadOnly;Pooling=False");
    _conn.Open();

    var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    try
    {
      using var cmd = _conn.CreateCommand();
      cmd.CommandText = $"SELECT key, value FROM {GeoPackageWriter.InfoTable}";
      using var r = cmd.ExecuteReader();
      while (r.Read()) info[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
    }
    catch (SqliteException)
    {
      throw new InvalidDataException($"{path} no es un GeoPackage generado por osmimport (falta {GeoPackageWriter.InfoTable}).");
    }

    if (!info.TryGetValue("format", out var format) || format != "osm-road-graph")
      throw new InvalidDataException($"{path} no es un GeoPackage de osmimport (format='{format}').");

    Info = info;
    NodeCount = Scalar($"SELECT count(*) FROM {GeoPackageWriter.NodesTable}");
    SegmentCount = Scalar($"SELECT count(*) FROM {GeoPackageWriter.SegmentsTable}");

    // Detectar si el GeoPackage incluye atributos extendidos y tabla de giros
    using (var colCmd = _conn.CreateCommand())
    {
      colCmd.CommandText = $"PRAGMA table_info({GeoPackageWriter.SegmentsTable})";
      using var cr = colCmd.ExecuteReader();
      var colNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      while (cr.Read()) colNames.Add(cr.GetString(1));
      _hasExtendedColumns = colNames.Contains("surface");
    }

    using (var tblCmd = _conn.CreateCommand())
    {
      tblCmd.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{GeoPackageWriter.RestrictionsTable}'";
      _hasRestrictionsTable = Convert.ToInt64(tblCmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }
  }

  public IEnumerable<GpkgNode> Nodes()
  {
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = $"SELECT fid, node_key, geom, valence FROM {GeoPackageWriter.NodesTable} ORDER BY fid";
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
      var c = GpkgGeometry.ReadCoordinates((byte[])r[2]);
      yield return new GpkgNode(r.GetInt64(0), r.GetInt64(1), c[0], c[1], r.GetInt32(3));
    }
  }

  public IEnumerable<GpkgSegment> Segments()
  {
    using var cmd = _conn.CreateCommand();
    if (_hasExtendedColumns)
    {
      cmd.CommandText = $@"
        SELECT fid, segment_key, osm_id, name, highway, is_oneway, lanes, max_speed,
               from_node_fid, to_node_fid, from_node_key, to_node_key, geom, length_m, travel_cost,
               surface, smoothness, tracktype, bridge, tunnel, layer, max_weight, max_height, max_width
        FROM {GeoPackageWriter.SegmentsTable} ORDER BY fid
        ";
      using var r = cmd.ExecuteReader();
      while (r.Read())
      {
        var c = GpkgGeometry.ReadCoordinates((byte[])r[12]);
        yield return new GpkgSegment(
          r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
          r.GetInt64(5) != 0, r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7),
          r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11),
          c[0], c[1], c[2], c[3], r.GetDouble(13), r.GetDouble(14),
          r.IsDBNull(15) ? null : r.GetString(15),
          r.IsDBNull(16) ? null : r.GetString(16),
          r.IsDBNull(17) ? null : r.GetString(17),
          !r.IsDBNull(18) && r.GetInt64(18) != 0,
          !r.IsDBNull(19) && r.GetInt64(19) != 0,
          r.IsDBNull(20) ? 0 : r.GetInt32(20),
          r.IsDBNull(21) ? null : r.GetDouble(21),
          r.IsDBNull(22) ? null : r.GetDouble(22),
          r.IsDBNull(23) ? null : r.GetDouble(23));
      }
    }
    else
    {
      cmd.CommandText = $@"
        SELECT fid, segment_key, osm_id, name, highway, is_oneway, lanes, max_speed,
               from_node_fid, to_node_fid, from_node_key, to_node_key, geom, length_m, travel_cost
        FROM {GeoPackageWriter.SegmentsTable} ORDER BY fid
        ";
      using var r = cmd.ExecuteReader();
      while (r.Read())
      {
        var c = GpkgGeometry.ReadCoordinates((byte[])r[12]);
        yield return new GpkgSegment(
          r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
          r.GetInt64(5) != 0, r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7),
          r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11),
          c[0], c[1], c[2], c[3], r.GetDouble(13), r.GetDouble(14));
      }
    }
  }

  public IEnumerable<GpkgRestriction> Restrictions()
  {
    if (!_hasRestrictionsTable) yield break;
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = $"SELECT fid, relation_id, from_osm_id, via_node_key, to_osm_id, restriction FROM {GeoPackageWriter.RestrictionsTable} ORDER BY fid";
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
      yield return new GpkgRestriction(
        r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetString(5));
    }
  }

  private long Scalar(string sql)
  {
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
  }

  public void Dispose() => _conn.Dispose();
}
