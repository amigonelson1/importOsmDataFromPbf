using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using OsmImporter.Core;
using OsmImporter.Core.Output;

namespace OsmImporter.Publish.Postgres;

/// <summary>
/// Publica el GeoPackage en PostgreSQL/PostGIS.
/// - create:  crea las tablas (esquema estándar de red vial, nombres según el perfil) y carga.
/// - replace: valida que nadie dependa de la red actual, respalda, vacía y recarga.
/// Todo dentro de una transacción. Carga con COPY binario; los índices se quitan durante la carga y se recrean.
/// Los Ids del destino quedan = fid del GeoPackage y las secuencias se ajustan a max(Id)+1.
/// </summary>
public sealed partial class PostgresPublisher : IPublisher
{
  private const int Srid = 4326;

  private readonly PublishOptions _o;
  private readonly NpgsqlDataSource _ds;

  public string Target { get; }

  public PostgresPublisher(string connectionString, PublishOptions options)
  {
    options.Validate();
    _o = options;

    var b = new NpgsqlConnectionStringBuilder(connectionString) { CommandTimeout = 0 };
    _ds = NpgsqlDataSource.Create(b.ConnectionString);
    Target = $"postgres://{b.Host}:{b.Port}/{b.Database}  [{SchemaName}.{_o.Nodes.Table}, {SchemaName}.{_o.Segments.Table}]";
  }

  private string SchemaName => string.IsNullOrWhiteSpace(_o.Schema) ? "public" : _o.Schema!;
  private string NodesQ => Qualified(_o.Nodes.Table);
  private string SegsQ => Qualified(_o.Segments.Table);

  private static string Q(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";
  private string Qualified(string table, string? schema = null) => Q(schema ?? SchemaName) + "." + Q(table);

  // ------------------------------------------------------------------ publish

  public async Task<PublishResult> PublishAsync(GeoPackageReader gpkg, PublishMode mode, PublishFlags flags, CancellationToken ct = default)
  {
    if (mode == PublishMode.Sync) return await SyncAsync(gpkg, flags, ct);

    var sw = Stopwatch.StartNew();
    var result = new PublishResult { Target = Target, Mode = mode, DryRun = flags.DryRun };

    await using var conn = await _ds.OpenConnectionAsync(ct);
    await EnsurePostgisAsync(conn, ct);

    var nodesExist = await ExistsAsync(conn, NodesQ, ct);
    var segsExist = await ExistsAsync(conn, SegsQ, ct);

    Dictionary<string, ColumnInfo> nodeCols, segCols;

    if (mode == PublishMode.Create)
    {
      if (nodesExist || segsExist)
        throw new InvalidOperationException($"Las tablas ya existen en el destino ({(nodesExist ? _o.Nodes.Table : "")} {(segsExist ? _o.Segments.Table : "")}). Usa --mode replace (o sync en la fase 3).");

      nodeCols = CreateModeNodeColumns();
      segCols = CreateModeSegmentColumns();
      result.Steps.Add($"Crear {NodesQ} y {SegsQ} (esquema estándar de red vial)");
    }
    else
    {
      if (!nodesExist || !segsExist)
        throw new InvalidOperationException($"No existen {(!nodesExist ? NodesQ : "")} {(!segsExist ? SegsQ : "")} en el destino. Usa --mode create.");

      nodeCols = await ColumnsAsync(conn, _o.Nodes.Table, ct);
      segCols = await ColumnsAsync(conn, _o.Segments.Table, ct);
      RequireColumns(_o.Nodes.Table, nodeCols, MandatoryNodeColumns(), OptionalNodeColumns());
      RequireColumns(_o.Segments.Table, segCols, MandatorySegmentColumns(), OptionalSegmentColumns());
      await RequireSridAsync(conn, _o.Nodes.Table, _o.Nodes.Location, ct);
      await RequireSridAsync(conn, _o.Segments.Table, _o.Segments.Geometry, ct);

      result.PreviousNodes = await CountAsync(conn, NodesQ, ct);
      result.PreviousSegments = await CountAsync(conn, SegsQ, ct);

      result.Dependencies.AddRange(await CheckDependenciesAsync(conn, ct));
      var blocking = result.Dependencies.Where(d => d.RowsReferencing > 0).ToList();
      if (blocking.Count > 0)
      {
        var msg = "replace rompería datos que dependen de la red actual:\n" +
                  string.Join("\n", blocking.Select(d => $"  - {d.Table}.{d.Column} → {d.References}: {d.RowsReferencing:N0} filas ({d.Source})")) +
                  "\nNo se modificó nada. Para actualizar sin romper estas referencias se usará 'publish --mode sync' (fase 3).";
        if (flags.DryRun) { result.Steps.Add("BLOQUEADO: " + msg); result.Elapsed = sw.Elapsed; return result; }
        throw new DependentDataException(msg);
      }

      if (flags.Backup) result.Steps.Add($"Respaldar {_o.Nodes.Table} ({result.PreviousNodes:N0}) y {_o.Segments.Table} ({result.PreviousSegments:N0}) en tablas *_bak_<fecha>");
      result.Steps.Add($"Vaciar {SegsQ} y {NodesQ} (índices fuera durante la carga)");
    }

    result.Steps.Add($"Cargar {gpkg.NodeCount:N0} nodos y {gpkg.SegmentCount:N0} segmentos (COPY binario)");
    result.Steps.Add("Recrear índices, ajustar secuencias de Id, ANALYZE");

    if (flags.DryRun) { result.Elapsed = sw.Elapsed; return result; }

    await using (var tx = await conn.BeginTransactionAsync(ct))
    {
      List<(string Name, string Definition)> indexes;

      if (mode == PublishMode.Create)
      {
        Log.Info("  creando tablas...");
        await ExecAsync(conn, CreateTablesDdl(), ct);
        indexes = CreateIndexesDdl();
      }
      else
      {
        if (flags.Backup)
        {
          var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
          foreach (var table in new[] { _o.Nodes.Table, _o.Segments.Table })
          {
            var bak = $"{table}_bak_{stamp}";
            Log.Info($"  respaldando {table} → {bak}...");
            await ExecAsync(conn, $"CREATE TABLE {Qualified(bak)} AS TABLE {Qualified(table)};", ct);
            result.Backups.Add($"{SchemaName}.{bak}");
          }
        }

        // Vaciar ANTES de quitar índices. TRUNCATE no se permite sobre una tabla referenciada por FKs de
        // otras tablas (aunque esas filas estén en 0, ya verificado arriba), y como los segmentos referencian
        // a los nodos, los nodos solo se pueden truncar junto con los segmentos. Si hay que usar DELETE, cada
        // fila borrada verifica sus FKs: con los índices de FromNodeId/ToNodeId todavía presentes eso es
        // barato; sin ellos es O(n²) contra millones de filas muertas.
        Log.Info("  vaciando tablas...");
        var segsReferenced = result.Dependencies.Any(d => d.Source == "fk" && d.References == "segments");
        var nodesReferenced = result.Dependencies.Any(d => d.Source == "fk" && d.References == "nodes");

        if (!segsReferenced && !nodesReferenced)
        {
          await ExecAsync(conn, $"TRUNCATE {SegsQ}, {NodesQ};", ct);
        }
        else
        {
          await ExecAsync(conn, segsReferenced ? $"DELETE FROM {SegsQ};" : $"TRUNCATE {SegsQ};", ct);
          await ExecAsync(conn, $"DELETE FROM {NodesQ};", ct);
        }

        indexes = [.. await CaptureIndexesAsync(conn, NodesQ, ct), .. await CaptureIndexesAsync(conn, SegsQ, ct)];
        foreach (var (name, _) in indexes)
          await ExecAsync(conn, $"DROP INDEX {Qualified(name)};", ct);
      }

      Log.Info("  cargando nodos...");
      result.NodesWritten = await CopyNodesAsync(conn, gpkg, nodeCols, ct);
      Log.Info($"  {result.NodesWritten:N0} nodos");

      Log.Info("  cargando segmentos...");
      result.SegmentsWritten = await CopySegmentsAsync(conn, gpkg, segCols, ct);
      Log.Info($"  {result.SegmentsWritten:N0} segmentos");

      Log.Info($"  creando {indexes.Count} índices...");
      foreach (var (name, definition) in indexes)
      {
        Log.Debug($"    {name}");
        await ExecAsync(conn, definition + ";", ct);
      }

      await ResetSequenceAsync(conn, NodesQ, _o.Nodes.Id, ct);
      await ResetSequenceAsync(conn, SegsQ, _o.Segments.Id, ct);

      await tx.CommitAsync(ct);
    }

    Log.Info("  ANALYZE...");
    await ExecAsync(conn, $"ANALYZE {NodesQ}; ANALYZE {SegsQ};", ct);

    result.Elapsed = sw.Elapsed;
    return result;
  }

  // ------------------------------------------------------------------ COPY

  private async Task<long> CopyNodesAsync(NpgsqlConnection conn, GeoPackageReader gpkg, Dictionary<string, ColumnInfo> cols, CancellationToken ct)
  {
    var now = DateTime.UtcNow;
    var m = _o.Nodes;
    var fields = new List<(string Column, Func<GpkgNode, object?> Get)>
    {
      (m.Id, n => n.Fid),
      (m.Location, n => Ewkb.Point(n.Lon, n.Lat, Srid)),
    };
    if (m.Valence is { } v && cols.ContainsKey(v)) fields.Add((v, n => n.Valence));
    if (m.CreatedAt is { } c && cols.ContainsKey(c)) fields.Add((c, _ => now));

    return await CopyAsync(conn, NodesQ, fields, cols, gpkg.Nodes(), ct);
  }

  private async Task<long> CopySegmentsAsync(NpgsqlConnection conn, GeoPackageReader gpkg, Dictionary<string, ColumnInfo> cols, CancellationToken ct)
  {
    var now = DateTime.UtcNow;
    var m = _o.Segments;
    var fields = new List<(string Column, Func<GpkgSegment, object?> Get)>
    {
      (m.Id, s => s.Fid),
      (m.OsmId, s => s.OsmId),
      (m.HighwayType, s => s.Highway),
      (m.Geometry, s => Ewkb.Line(s.X1, s.Y1, s.X2, s.Y2, Srid)),
      (m.FromNodeId, s => s.FromNodeFid),
      (m.ToNodeId, s => s.ToNodeFid),
    };
    if (m.Name is { } name && cols.ContainsKey(name)) fields.Add((name, s => s.Name));
    if (m.IsOneWay is { } ow && cols.ContainsKey(ow)) fields.Add((ow, s => s.IsOneWay));
    if (m.Lanes is { } lanes && cols.ContainsKey(lanes)) fields.Add((lanes, s => s.Lanes));
    if (m.MaxSpeed is { } ms && cols.ContainsKey(ms)) fields.Add((ms, s => s.MaxSpeed));
    if (m.Surface is { } surf && cols.ContainsKey(surf)) fields.Add((surf, s => s.Surface));
    if (m.Smoothness is { } sm && cols.ContainsKey(sm)) fields.Add((sm, s => s.Smoothness));
    if (m.TrackType is { } tt && cols.ContainsKey(tt)) fields.Add((tt, s => s.TrackType));
    if (m.Bridge is { } br && cols.ContainsKey(br)) fields.Add((br, s => s.Bridge));
    if (m.Tunnel is { } tu && cols.ContainsKey(tu)) fields.Add((tu, s => s.Tunnel));
    if (m.Layer is { } lay && cols.ContainsKey(lay)) fields.Add((lay, s => s.Layer));
    if (m.MaxWeight is { } mw && cols.ContainsKey(mw)) fields.Add((mw, s => s.MaxWeight));
    if (m.MaxHeight is { } mh && cols.ContainsKey(mh)) fields.Add((mh, s => s.MaxHeight));
    if (m.MaxWidth is { } mwd && cols.ContainsKey(mwd)) fields.Add((mwd, s => s.MaxWidth));
    if (m.LengthMeters is { } len && cols.ContainsKey(len)) fields.Add((len, s => s.LengthMeters));
    if (m.TravelCost is { } cost && cols.ContainsKey(cost)) fields.Add((cost, s => s.TravelCost));
    if (m.CreatedAt is { } c && cols.ContainsKey(c)) fields.Add((c, _ => now));

    return await CopyAsync(conn, SegsQ, fields, cols, gpkg.Segments(), ct);
  }

  private static async Task<long> CopyAsync<T>(
    NpgsqlConnection conn, string table, List<(string Column, Func<T, object?> Get)> fields,
    Dictionary<string, ColumnInfo> cols, IEnumerable<T> rows, CancellationToken ct)
  {
    var infos = fields.Select(f => cols[f.Column]).ToArray();
    var sql = $"COPY {table} ({string.Join(", ", fields.Select(f => Q(f.Column)))}) FROM STDIN (FORMAT BINARY)";

    long count = 0;
    await using var writer = await conn.BeginBinaryImportAsync(sql, ct);
    foreach (var row in rows)
    {
      await writer.StartRowAsync(ct);
      for (var i = 0; i < fields.Count; i++)
        await WriteValueAsync(writer, fields[i].Get(row), infos[i], ct);

      if (++count % 500_000 == 0) Log.Debug($"    {count:N0}...");
    }

    await writer.CompleteAsync(ct);
    return count;
  }

  private static Task WriteValueAsync(NpgsqlBinaryImporter w, object? value, ColumnInfo col, CancellationToken ct)
  {
    if (value is null) return w.WriteNullAsync(ct);

    return col.DataType switch
    {
      "bigint" => w.WriteAsync(Convert.ToInt64(value), NpgsqlDbType.Bigint, ct),
      "integer" => w.WriteAsync(Convert.ToInt32(value), NpgsqlDbType.Integer, ct),
      "smallint" => w.WriteAsync(Convert.ToInt16(value), NpgsqlDbType.Smallint, ct),
      "double precision" => w.WriteAsync(Convert.ToDouble(value), NpgsqlDbType.Double, ct),
      "real" => w.WriteAsync(Convert.ToSingle(value), NpgsqlDbType.Real, ct),
      "numeric" => w.WriteAsync(Convert.ToDecimal(value), NpgsqlDbType.Numeric, ct),
      "boolean" => w.WriteAsync(Convert.ToBoolean(value), NpgsqlDbType.Boolean, ct),
      "timestamp with time zone" => w.WriteAsync((DateTime)value, NpgsqlDbType.TimestampTz, ct),
      "timestamp without time zone" => w.WriteAsync(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Unspecified), NpgsqlDbType.Timestamp, ct),
      "text" or "character varying" or "character" => w.WriteAsync(Truncate(value.ToString()!, col.MaxLength), NpgsqlDbType.Text, ct),
      // geometry / geography: COPY binario pasa los bytes al receive del tipo (acepta EWKB)
      "USER-DEFINED" when col.UdtName is "geometry" or "geography" => w.WriteAsync((byte[])value, NpgsqlDbType.Bytea, ct),
      _ => throw new NotSupportedException($"Tipo de columna no soportado: {col.Name} ({col.DataType}/{col.UdtName})"),
    };
  }

  private static string Truncate(string s, int? max) => max is { } m && s.Length > m ? s[..m] : s;

  // ------------------------------------------------------------------ DDL (create)

  private string CreateTablesDdl()
  {
    var n = _o.Nodes;
    var s = _o.Segments;
    var nodeLines = new List<string>
    {
      $"{Q(n.Id)} bigint GENERATED BY DEFAULT AS IDENTITY",
      $"{Q(n.Location)} geometry(Point,{Srid}) NOT NULL",
    };
    if (n.Valence is { } v) nodeLines.Add($"{Q(v)} integer NOT NULL DEFAULT 0");
    if (n.CreatedAt is { } nc) nodeLines.Add($"{Q(nc)} timestamp with time zone NOT NULL");
    nodeLines.Add($"CONSTRAINT {Q("PK_" + n.Table)} PRIMARY KEY ({Q(n.Id)})");

    var segLines = new List<string>
    {
      $"{Q(s.Id)} bigint GENERATED BY DEFAULT AS IDENTITY",
      $"{Q(s.OsmId)} bigint NOT NULL",
    };
    if (s.Name is { } name) segLines.Add($"{Q(name)} character varying(500)");
    segLines.Add($"{Q(s.HighwayType)} character varying(100) NOT NULL");
    if (s.IsOneWay is { } ow) segLines.Add($"{Q(ow)} boolean NOT NULL");
    if (s.Lanes is { } lanes) segLines.Add($"{Q(lanes)} integer");
    if (s.MaxSpeed is { } ms) segLines.Add($"{Q(ms)} integer");
    if (s.Surface is { } surf) segLines.Add($"{Q(surf)} character varying(100)");
    if (s.Smoothness is { } sm) segLines.Add($"{Q(sm)} character varying(100)");
    if (s.TrackType is { } tt) segLines.Add($"{Q(tt)} character varying(50)");
    if (s.Bridge is { } br) segLines.Add($"{Q(br)} boolean NOT NULL DEFAULT FALSE");
    if (s.Tunnel is { } tu) segLines.Add($"{Q(tu)} boolean NOT NULL DEFAULT FALSE");
    if (s.Layer is { } lay) segLines.Add($"{Q(lay)} integer NOT NULL DEFAULT 0");
    if (s.MaxWeight is { } mw) segLines.Add($"{Q(mw)} double precision");
    if (s.MaxHeight is { } mh) segLines.Add($"{Q(mh)} double precision");
    if (s.MaxWidth is { } mwd) segLines.Add($"{Q(mwd)} double precision");
    segLines.Add($"{Q(s.Geometry)} geometry(LineString,{Srid}) NOT NULL");
    if (s.LengthMeters is { } len) segLines.Add($"{Q(len)} double precision NOT NULL");
    if (s.CreatedAt is { } sc) segLines.Add($"{Q(sc)} timestamp with time zone NOT NULL DEFAULT now()");
    segLines.Add($"{Q(s.FromNodeId)} bigint");
    segLines.Add($"{Q(s.ToNodeId)} bigint");
    if (s.TravelCost is { } cost) segLines.Add($"{Q(cost)} double precision NOT NULL");
    segLines.Add($"CONSTRAINT {Q("PK_" + s.Table)} PRIMARY KEY ({Q(s.Id)})");
    segLines.Add($"CONSTRAINT {Q($"FK_{s.Table}_{n.Table}_{s.FromNodeId}")} FOREIGN KEY ({Q(s.FromNodeId)}) REFERENCES {NodesQ} ({Q(n.Id)})");
    segLines.Add($"CONSTRAINT {Q($"FK_{s.Table}_{n.Table}_{s.ToNodeId}")} FOREIGN KEY ({Q(s.ToNodeId)}) REFERENCES {NodesQ} ({Q(n.Id)})");

    return $"""
      CREATE TABLE {NodesQ} (
        {string.Join(",\n  ", nodeLines)}
      );
      CREATE TABLE {SegsQ} (
        {string.Join(",\n  ", segLines)}
      );
      """;
  }

  /// <summary>Índices estándar: GIST en geometrías y extremos de segmento, btree en FKs, osm_id y tipo de vía.</summary>
  private List<(string Name, string Definition)> CreateIndexesDdl()
  {
    var n = _o.Nodes;
    var s = _o.Segments;
    var list = new List<(string, string)>();
    void Add(string table, string tableQ, string suffix, string method, string expr) =>
      list.Add(($"IX_{table}_{suffix}", $"CREATE INDEX {Q($"IX_{table}_{suffix}")} ON {tableQ}{method} ({expr})"));

    Add(n.Table, NodesQ, n.Location, " USING GIST", Q(n.Location));
    Add(s.Table, SegsQ, s.Geometry, " USING GIST", Q(s.Geometry));
    Add(s.Table, SegsQ, "StartPoint", " USING GIST", $"ST_StartPoint({Q(s.Geometry)})");
    Add(s.Table, SegsQ, "EndPoint", " USING GIST", $"ST_EndPoint({Q(s.Geometry)})");
    Add(s.Table, SegsQ, s.FromNodeId, "", Q(s.FromNodeId));
    Add(s.Table, SegsQ, s.ToNodeId, "", Q(s.ToNodeId));
    Add(s.Table, SegsQ, s.OsmId, "", Q(s.OsmId));
    Add(s.Table, SegsQ, s.HighwayType, "", Q(s.HighwayType));
    if (s.IsOneWay is { } ow) Add(s.Table, SegsQ, $"{s.HighwayType}_{ow}", "", $"{Q(s.HighwayType)}, {Q(ow)}");
    return list;
  }

  private Dictionary<string, ColumnInfo> CreateModeNodeColumns()
  {
    var n = _o.Nodes;
    var d = new Dictionary<string, ColumnInfo>(StringComparer.Ordinal)
    {
      [n.Id] = new(n.Id, "bigint", "int8", null),
      [n.Location] = new(n.Location, "USER-DEFINED", "geometry", null),
    };
    if (n.Valence is { } v) d[v] = new(v, "integer", "int4", null);
    if (n.CreatedAt is { } c) d[c] = new(c, "timestamp with time zone", "timestamptz", null);
    return d;
  }

  private Dictionary<string, ColumnInfo> CreateModeSegmentColumns()
  {
    var s = _o.Segments;
    var d = new Dictionary<string, ColumnInfo>(StringComparer.Ordinal)
    {
      [s.Id] = new(s.Id, "bigint", "int8", null),
      [s.OsmId] = new(s.OsmId, "bigint", "int8", null),
      [s.HighwayType] = new(s.HighwayType, "character varying", "varchar", 100),
      [s.Geometry] = new(s.Geometry, "USER-DEFINED", "geometry", null),
      [s.FromNodeId] = new(s.FromNodeId, "bigint", "int8", null),
      [s.ToNodeId] = new(s.ToNodeId, "bigint", "int8", null),
    };
    if (s.Name is { } name) d[name] = new(name, "character varying", "varchar", 500);
    if (s.IsOneWay is { } ow) d[ow] = new(ow, "boolean", "bool", null);
    if (s.Lanes is { } l) d[l] = new(l, "integer", "int4", null);
    if (s.MaxSpeed is { } m) d[m] = new(m, "integer", "int4", null);
    if (s.Surface is { } surf) d[surf] = new(surf, "character varying", "varchar", 100);
    if (s.Smoothness is { } sm) d[sm] = new(sm, "character varying", "varchar", 100);
    if (s.TrackType is { } tt) d[tt] = new(tt, "character varying", "varchar", 50);
    if (s.Bridge is { } br) d[br] = new(br, "boolean", "bool", null);
    if (s.Tunnel is { } tu) d[tu] = new(tu, "boolean", "bool", null);
    if (s.Layer is { } lay) d[lay] = new(lay, "integer", "int4", null);
    if (s.MaxWeight is { } mw) d[mw] = new(mw, "double precision", "float8", null);
    if (s.MaxHeight is { } mh) d[mh] = new(mh, "double precision", "float8", null);
    if (s.MaxWidth is { } mwd) d[mwd] = new(mwd, "double precision", "float8", null);
    if (s.LengthMeters is { } len) d[len] = new(len, "double precision", "float8", null);
    if (s.TravelCost is { } c) d[c] = new(c, "double precision", "float8", null);
    if (s.CreatedAt is { } ca) d[ca] = new(ca, "timestamp with time zone", "timestamptz", null);
    return d;
  }

  internal IEnumerable<string> MandatoryNodeColumns() =>
    new[] { _o.Nodes.Id, _o.Nodes.Location }.OfType<string>();

  internal IEnumerable<string> OptionalNodeColumns() =>
    new[] { _o.Nodes.Valence, _o.Nodes.CreatedAt }.OfType<string>();

  internal IEnumerable<string> MandatorySegmentColumns()
  {
    var s = _o.Segments;
    return new[] { s.Id, s.OsmId, s.HighwayType, s.Geometry, s.FromNodeId, s.ToNodeId }.OfType<string>();
  }

  internal IEnumerable<string> OptionalSegmentColumns()
  {
    var s = _o.Segments;
    return new[] {
      s.Name, s.IsOneWay, s.Lanes, s.MaxSpeed,
      s.Surface, s.Smoothness, s.TrackType, s.Bridge, s.Tunnel, s.Layer,
      s.MaxWeight, s.MaxHeight, s.MaxWidth,
      s.LengthMeters, s.TravelCost, s.CreatedAt
    }.OfType<string>();
  }

  // ------------------------------------------------------------------ catálogo / verificaciones

  private sealed record ColumnInfo(string Name, string DataType, string UdtName, int? MaxLength);

  private async Task<Dictionary<string, ColumnInfo>> ColumnsAsync(NpgsqlConnection conn, string table, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand("""
      SELECT column_name, data_type, udt_name, character_maximum_length
      FROM information_schema.columns WHERE table_schema = $1 AND table_name = $2
      """, conn);
    cmd.Parameters.Add(new() { Value = SchemaName });
    cmd.Parameters.Add(new() { Value = table });

    var d = new Dictionary<string, ColumnInfo>(StringComparer.Ordinal);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    while (await r.ReadAsync(ct))
      d[r.GetString(0)] = new ColumnInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3));
    return d;
  }

  private static void RequireColumns(string table, Dictionary<string, ColumnInfo> existing, IEnumerable<string> mandatory, IEnumerable<string> optional)
  {
    var missing = mandatory.Where(c => !existing.ContainsKey(c)).ToList();
    if (missing.Count > 0)
      throw new InvalidOperationException($"La tabla {table} no tiene las columnas requeridas del perfil: {string.Join(", ", missing)}.");

    foreach (var opt in optional)
    {
      if (!existing.ContainsKey(opt))
        Log.Warn($"Columna opcional '{opt}' configurada en perfil pero no existe en tabla {table}; se omitirá.");
    }
  }

  private async Task RequireSridAsync(NpgsqlConnection conn, string table, string column, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand("""
      SELECT srid FROM geometry_columns WHERE f_table_schema = $1 AND f_table_name = $2 AND f_geometry_column = $3
      UNION ALL
      SELECT srid FROM geography_columns WHERE f_table_schema = $1 AND f_table_name = $2 AND f_geography_column = $3
      """, conn);
    cmd.Parameters.Add(new() { Value = SchemaName });
    cmd.Parameters.Add(new() { Value = table });
    cmd.Parameters.Add(new() { Value = column });
    var srid = await cmd.ExecuteScalarAsync(ct);
    if (srid is int s && s != 0 && s != Srid)
      throw new InvalidOperationException($"{table}.{column} usa SRID {s}; osmimport solo escribe SRID {Srid} (sin reproyección).");
  }

  private static async Task EnsurePostgisAsync(NpgsqlConnection conn, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand("SELECT 1 FROM pg_extension WHERE extname = 'postgis'", conn);
    if (await cmd.ExecuteScalarAsync(ct) is null)
      throw new InvalidOperationException("La base destino no tiene la extensión PostGIS. Ejecuta: CREATE EXTENSION postgis;");
  }

  private static async Task<bool> ExistsAsync(NpgsqlConnection conn, string qualified, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", conn);
    cmd.Parameters.Add(new() { Value = qualified });
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task<long> CountAsync(NpgsqlConnection conn, string qualified, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {qualified}", conn);
    return (long)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private async Task<List<DependencyStatus>> CheckDependenciesAsync(NpgsqlConnection conn, CancellationToken ct)
  {
    var result = new List<DependencyStatus>();

    // 1) FKs reales desde otras tablas hacia nodos/segmentos
    var fks = new List<(string Schema, string Table, string Column, string Target)>();
    await using (var cmd = new NpgsqlCommand("""
      SELECT ns.nspname, src.relname, a.attname, c.confrelid::regclass::text
      FROM pg_constraint c
      JOIN pg_class src ON src.oid = c.conrelid
      JOIN pg_namespace ns ON ns.oid = src.relnamespace
      JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY (c.conkey)
      WHERE c.contype = 'f'
        AND c.confrelid IN ($1::regclass, $2::regclass)
        AND c.conrelid NOT IN ($1::regclass, $2::regclass)
      """, conn))
    {
      cmd.Parameters.Add(new() { Value = NodesQ });
      cmd.Parameters.Add(new() { Value = SegsQ });
      await using var r = await cmd.ExecuteReaderAsync(ct);
      while (await r.ReadAsync(ct)) fks.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
    }

    foreach (var fk in fks)
    {
      var refs = fk.Target.Trim('"').EndsWith(_o.Segments.Table, StringComparison.Ordinal) ? "segments" : "nodes";
      var count = await CountScalarAsync(conn, $"SELECT count(*) FROM {Qualified(fk.Table, fk.Schema)} WHERE {Q(fk.Column)} IS NOT NULL", ct);
      result.Add(new DependencyStatus { Source = "fk", Table = $"{fk.Schema}.{fk.Table}", Column = fk.Column, References = refs, RowsReferencing = count });
    }

    // 2) Dependencias lógicas declaradas en el perfil
    foreach (var d in _o.Dependencies)
    {
      var schema = d.Schema ?? SchemaName;
      if (fks.Any(f => f.Schema == schema && f.Table == d.Table && f.Column == d.Column)) continue;

      var qualified = Qualified(d.Table, schema);
      if (!await ExistsAsync(conn, qualified, ct))
      {
        result.Add(new DependencyStatus { Source = "declarada", Table = $"{schema}.{d.Table}", Column = d.Column, References = d.References, Note = "la tabla no existe en el destino" });
        continue;
      }

      var where = $"NULLIF({Q(d.Column)}::text, '') IS NOT NULL" + (string.IsNullOrWhiteSpace(d.Filter) ? "" : $" AND ({d.Filter})");
      var count = await CountScalarAsync(conn, $"SELECT count(*) FROM {qualified} WHERE {where}", ct);
      result.Add(new DependencyStatus { Source = "declarada", Table = $"{schema}.{d.Table}", Column = d.Column, References = d.References, RowsReferencing = count, Note = d.Description });
    }

    return result;
  }

  private static async Task<long> CountScalarAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand(sql, conn);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
  }

  /// <summary>Índices que no respaldan una constraint (PK/UNIQUE/EXCLUDE): se pueden quitar y recrear tal cual.</summary>
  private static async Task<List<(string, string)>> CaptureIndexesAsync(NpgsqlConnection conn, string qualified, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand("""
      SELECT ci.relname, pg_get_indexdef(i.indexrelid)
      FROM pg_index i JOIN pg_class ci ON ci.oid = i.indexrelid
      WHERE i.indrelid = $1::regclass
        AND NOT EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = i.indexrelid)
      """, conn);
    cmd.Parameters.Add(new() { Value = qualified });
    var list = new List<(string, string)>();
    await using var r = await cmd.ExecuteReaderAsync(ct);
    while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetString(1)));
    return list;
  }

  private static async Task ResetSequenceAsync(NpgsqlConnection conn, string qualified, string idColumn, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand("SELECT pg_get_serial_sequence($1, $2)", conn);
    cmd.Parameters.Add(new() { Value = qualified });
    cmd.Parameters.Add(new() { Value = idColumn });
    if (await cmd.ExecuteScalarAsync(ct) is not string seq) return;

    await using var set = new NpgsqlCommand($"SELECT setval($1, COALESCE((SELECT max({Q(idColumn)}) FROM {qualified}), 0) + 1, false)", conn);
    set.Parameters.Add(new() { Value = seq });
    await set.ExecuteScalarAsync(ct);
  }

  private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  // ------------------------------------------------------------------ lectura / diagnóstico

  public async Task<ExistingNetwork> ReadExistingAsync(CancellationToken ct = default)
  {
    await using var conn = await _ds.OpenConnectionAsync(ct);
    return await ReadExistingAsync(conn, ct);
  }

  private async Task<ExistingNetwork> ReadExistingAsync(NpgsqlConnection conn, CancellationToken ct)
  {
    var s = _o.Segments;
    var n = _o.Nodes;
    var g = $"{Q(s.Geometry)}::geometry";
    var len = s.LengthMeters is { } l ? Q(l) : $"ST_Length({Q(s.Geometry)}::geography)";

    var segments = new List<ExistingSegment>();
    await using (var cmd = new NpgsqlCommand($"""
      SELECT {Q(s.Id)}, {Q(s.OsmId)}, {Q(s.HighwayType)},
             ST_X(ST_StartPoint({g})), ST_Y(ST_StartPoint({g})), ST_X(ST_EndPoint({g})), ST_Y(ST_EndPoint({g})),
             {len}
      FROM {SegsQ}
      """, conn))
    await using (var r = await cmd.ExecuteReaderAsync(ct))
    {
      while (await r.ReadAsync(ct))
        segments.Add(new ExistingSegment(r.GetInt64(0), r.GetInt64(1), r.GetString(2),
          r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7)));
    }

    var nodes = new List<ExistingNode>();
    await using (var cmd = new NpgsqlCommand($"SELECT {Q(n.Id)}, ST_X({Q(n.Location)}::geometry), ST_Y({Q(n.Location)}::geometry) FROM {NodesQ}", conn))
    await using (var r = await cmd.ExecuteReaderAsync(ct))
    {
      while (await r.ReadAsync(ct))
        nodes.Add(new ExistingNode(r.GetInt64(0), r.GetDouble(1), r.GetDouble(2)));
    }

    return new ExistingNetwork { Nodes = nodes, Segments = segments };
  }

  public async Task<Dictionary<string, string>> DiagnosticsAsync(CancellationToken ct = default)
  {
    await using var conn = await _ds.OpenConnectionAsync(ct);
    var d = new Dictionary<string, string>();

    await using (var cmd = new NpgsqlCommand("SELECT current_setting('server_version'), postgis_lib_version()", conn))
    await using (var r = await cmd.ExecuteReaderAsync(ct))
    {
      if (await r.ReadAsync(ct)) { d["postgres"] = r.GetString(0); d["postgis"] = r.GetString(1); }
    }

    // 0.002° de latitud ≈ 222 m: uniform → 5 partes (6 puntos); bisección → 8 partes (9 puntos)
    await using (var cmd = new NpgsqlCommand("SELECT ST_NPoints(ST_Segmentize('SRID=4326;LINESTRING(0 0, 0 0.002)'::geography, 50)::geometry)", conn))
    {
      var points = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
      d["segmentize_method"] = points switch { 6 => "uniform", 9 => "bisect", _ => $"desconocido ({points} puntos)" };
    }

    return d;
  }

  public ValueTask DisposeAsync() => _ds.DisposeAsync();
}
