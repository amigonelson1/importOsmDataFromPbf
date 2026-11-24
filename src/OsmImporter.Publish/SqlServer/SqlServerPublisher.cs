using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using OsmImporter.Core;
using OsmImporter.Core.Output;
using OsmImporter.Publish.Sync;

namespace OsmImporter.Publish.SqlServer;

/// <summary>
/// Publica en SQL Server (2016+). Características que condicionan el diseño:
/// - El DDL ES transaccional: create, replace y sync son atómicos (todo o nada), también en dry-run.
/// - Carga con SqlBulkCopy a tablas temporales (#) con la geometría en WKB, y de ahí INSERT ... SELECT con
///   geography::STGeomFromWKB(wkb, 4326) (WKB OGC: X = longitud). Ids explícitos con IDENTITY_INSERT.
/// - Escrituras masivas: los índices no únicos se deshabilitan y se reconstruyen dentro de la transacción.
/// - Diff de atributos y reasignación de dependencias en memoria (SyncPlanner / DependencyPlanner).
/// - Tipo espacial: geography (default al crear, como EF Core + NetTopologySuite) o geometry; en tablas
///   existentes se detecta.
/// </summary>
public sealed class SqlServerPublisher : IPublisher
{
  private const int Srid = 4326;
  private const int BulkChunk = 50_000;

  private readonly PublishOptions _o;
  private readonly string _connectionString;
  private readonly string _schema;

  public string Target { get; }

  public SqlServerPublisher(string connectionString, PublishOptions options)
  {
    options.Validate();
    if (options.SqlServerSpatialType is not ("geography" or "geometry"))
      throw new ArgumentException("publish.sqlServerSpatialType debe ser 'geography' o 'geometry'.");
    _o = options;
    var b = new SqlConnectionStringBuilder(connectionString) { CommandTimeout = 0 };
    _connectionString = b.ConnectionString;
    _schema = string.IsNullOrWhiteSpace(_o.Schema) || _o.Schema == "public" ? "dbo" : _o.Schema!;
    Target = $"sqlserver://{b.DataSource}/{b.InitialCatalog}  [{_schema}.{_o.Nodes.Table}, {_schema}.{_o.Segments.Table}]";
  }

  private static string Q(string ident) => "[" + ident.Replace("]", "]]") + "]";
  private string Qualified(string table, string? schema = null) => Q(schema ?? _schema) + "." + Q(table);
  private string NodesQ => Qualified(_o.Nodes.Table);
  private string SegsQ => Qualified(_o.Segments.Table);

  private async Task<SqlConnection> OpenAsync(CancellationToken ct)
  {
    var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  // ==================================================================== publish

  public async Task<PublishResult> PublishAsync(GeoPackageReader gpkg, PublishMode mode, PublishFlags flags, CancellationToken ct = default) =>
    mode switch
    {
      PublishMode.Create => await CreateAsync(gpkg, flags, ct),
      PublishMode.Replace => await ReplaceAsync(gpkg, flags, ct),
      _ => await SyncAsync(gpkg, flags, ct),
    };

  private async Task<PublishResult> CreateAsync(GeoPackageReader gpkg, PublishFlags flags, CancellationToken ct)
  {
    var sw = Stopwatch.StartNew();
    var result = new PublishResult { Target = Target, Mode = PublishMode.Create, DryRun = flags.DryRun };
    await using var conn = await OpenAsync(ct);

    if (await ExistsAsync(conn, null, _o.Nodes.Table, ct) || await ExistsAsync(conn, null, _o.Segments.Table, ct))
      throw new InvalidOperationException($"Las tablas ya existen en {_schema}. Usa --mode replace o sync.");

    var spatial = _o.SqlServerSpatialType;
    result.Steps.Add($"Crear {NodesQ} y {SegsQ} (esquema estándar de red vial, tipo espacial {spatial})");
    result.Steps.Add($"Cargar {gpkg.NodeCount:N0} nodos y {gpkg.SegmentCount:N0} segmentos (SqlBulkCopy)");
    result.Steps.Add("Crear índices espaciales y btree — todo en una transacción");
    if (flags.DryRun) { result.Elapsed = sw.Elapsed; return result; }

    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
    Log.Info("  creando tablas...");
    await ExecAsync(conn, tx, CreateTablesDdl(spatial), ct);

    var now = DateTime.UtcNow;
    Log.Info("  cargando nodos...");
    result.NodesWritten = await InsertNodesAsync(conn, tx, gpkg.Nodes().Select(n => new NodeRow(n.Fid, n.Lon, n.Lat, n.Valence)), spatial, now, ct);
    Log.Info("  cargando segmentos...");
    var hwLen = 100; var nameLen = 500;
    result.SegmentsWritten = await InsertSegmentsAsync(conn, tx, gpkg.Segments().Select(s => new SegmentRow(s.Fid, s, s.FromNodeFid, s.ToNodeFid)), spatial, now, hwLen, nameLen, ct);

    Log.Info("  creando índices...");
    foreach (var ddl in CreateIndexesDdl(spatial)) await ExecAsync(conn, tx, ddl, ct);
    await tx.CommitAsync(ct);

    result.Elapsed = sw.Elapsed;
    return result;
  }

  private async Task<PublishResult> ReplaceAsync(GeoPackageReader gpkg, PublishFlags flags, CancellationToken ct)
  {
    var sw = Stopwatch.StartNew();
    var result = new PublishResult { Target = Target, Mode = PublishMode.Replace, DryRun = flags.DryRun };
    await using var conn = await OpenAsync(ct);
    var meta = await PreflightExistingAsync(conn, ct);

    result.PreviousNodes = await ScalarAsync(conn, null, $"SELECT COUNT_BIG(*) FROM {NodesQ}", ct);
    result.PreviousSegments = await ScalarAsync(conn, null, $"SELECT COUNT_BIG(*) FROM {SegsQ}", ct);

    foreach (var dep in await ListDependenciesAsync(conn, ct))
    {
      var count = await ScalarAsync(conn, null,
        $"SELECT COUNT_BIG(*) FROM {Qualified(dep.Table, dep.Schema)} d WHERE NULLIF(CAST(d.{Q(dep.Column)} AS nvarchar(100)), '') IS NOT NULL{Filter(dep)}", ct);
      result.Dependencies.Add(new DependencyStatus { Source = dep.Source, Table = $"{dep.Schema}.{dep.Table}", Column = dep.Column, References = dep.References, RowsReferencing = count });
    }

    var blocking = result.Dependencies.Where(d => d.RowsReferencing > 0).ToList();
    if (blocking.Count > 0)
    {
      var msg = "replace rompería datos que dependen de la red actual:\n" +
                string.Join("\n", blocking.Select(d => $"  - {d.Table}.{d.Column} → {d.References}: {d.RowsReferencing:N0} filas ({d.Source})")) +
                "\nNo se modificó nada. Usa --mode sync para actualizar sin romper estas referencias.";
      if (flags.DryRun) { result.Steps.Add("BLOQUEADO: " + msg); result.Elapsed = sw.Elapsed; return result; }
      throw new DependentDataException(msg);
    }

    if (flags.Backup) result.Steps.Add($"Respaldar {_o.Nodes.Table} y {_o.Segments.Table} en tablas *_bak_<fecha>");
    result.Steps.Add($"En una transacción: borrar la red actual ({result.PreviousSegments:N0} segmentos) y cargar {gpkg.SegmentCount:N0} segmentos / {gpkg.NodeCount:N0} nodos");
    if (flags.DryRun) { result.Elapsed = sw.Elapsed; return result; }

    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
    if (flags.Backup) result.Backups.AddRange(await BackupAsync(conn, tx, ct));

    var indexes = await DisableIndexesAsync(conn, tx, ct);
    Log.Info("  borrando red actual...");
    await ExecAsync(conn, tx, $"DELETE FROM {SegsQ}; DELETE FROM {NodesQ};", ct);

    var now = DateTime.UtcNow;
    Log.Info("  cargando nodos...");
    result.NodesWritten = await InsertNodesAsync(conn, tx, gpkg.Nodes().Select(n => new NodeRow(n.Fid, n.Lon, n.Lat, n.Valence)), meta.NodeSpatial, now, ct, meta.NodeCols);
    Log.Info("  cargando segmentos...");
    result.SegmentsWritten = await InsertSegmentsAsync(conn, tx, gpkg.Segments().Select(s => new SegmentRow(s.Fid, s, s.FromNodeFid, s.ToNodeFid)),
      meta.SegSpatial, now, meta.HighwayLength, meta.NameLength, ct, meta.SegCols);

    await RebuildIndexesAsync(conn, tx, indexes, ct);
    await tx.CommitAsync(ct);

    result.Elapsed = sw.Elapsed;
    return result;
  }

  private async Task<PublishResult> SyncAsync(GeoPackageReader gpkg, PublishFlags flags, CancellationToken ct)
  {
    var sw = Stopwatch.StartNew();
    var result = new PublishResult { Target = Target, Mode = PublishMode.Sync, DryRun = flags.DryRun };
    await using var conn = await OpenAsync(ct);
    var meta = await PreflightExistingAsync(conn, ct);

    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
    // Bloqueo de escritura sobre la red durante todo el sync (las lecturas de otros siguen funcionando)
    await ExecAsync(conn, tx, $"SELECT TOP 0 1 FROM {NodesQ} WITH (UPDLOCK, TABLOCK); SELECT TOP 0 1 FROM {SegsQ} WITH (UPDLOCK, TABLOCK);", ct);

    Log.Info("  leyendo red existente...");
    var existing = await ReadExistingAsync(conn, tx, meta, full: true, ct);
    result.PreviousNodes = existing.Nodes.Count;
    result.PreviousSegments = existing.Segments.Count;

    Log.Info("  calculando diff por llave natural...");
    var diff = NetworkDiff.Compute(gpkg, existing);
    var stats = result.Sync = new SyncStats
    {
      NodesExisting = result.PreviousNodes, NodesKeptExact = diff.NodesKeptExact, NodesKeptTolerant = diff.NodesKeptTolerant,
      NodesNew = diff.NewNodeCount, NodesRetired = diff.RetiredNodeIds.Count,
      SegmentsExisting = result.PreviousSegments, SegmentsKeptExact = diff.SegmentsKeptExact, SegmentsKeptTolerant = diff.SegmentsKeptTolerant,
      SegmentsNew = diff.NewSegmentCount, SegmentsRetired = diff.RetiredSegmentIds.Count,
    };
    Log.Info($"  segmentos: {stats.SegmentsKeptExact + stats.SegmentsKeptTolerant:N0} se conservan ({stats.SegmentsKeptTolerant:N0} con ±1 celda), " +
             $"{stats.SegmentsNew:N0} nuevos, {stats.SegmentsRetired:N0} retirados ({stats.RetiredSegmentsPercent}%)");
    Log.Info($"  nodos:     {stats.NodesKeptExact + stats.NodesKeptTolerant:N0} se conservan ({stats.NodesKeptTolerant:N0} con ±1 celda), " +
             $"{stats.NodesNew:N0} nuevos, {stats.NodesRetired:N0} retirados");

    var maxRetired = flags.MaxRetiredPercent ?? _o.MaxRetiredPercent;
    if (stats.RetiredSegmentsPercent > maxRetired)
      throw new InvalidOperationException(
        $"sync retiraría el {stats.RetiredSegmentsPercent}% de los segmentos existentes (límite {maxRetired}%). " +
        "¿El gpkg es de otra zona o se construyó con otros parámetros? Revisa con 'compare'. Si es intencional usa --max-retired-percent <n>. No se modificó nada.");

    var (nodeStart, nodeIdent) = await NextFreeIdAsync(conn, tx, _o.Nodes.Table, _o.Nodes.Id, ct);
    var (segStart, segIdent) = await NextFreeIdAsync(conn, tx, _o.Segments.Table, _o.Segments.Id, ct);
    diff.AssignNewIds(Range(nodeStart, diff.NewNodeCount), Range(segStart, diff.NewSegmentCount));

    var plan = SyncPlanner.Plan(diff, existing, MappedColumns.From(_o));
    foreach (var (col, count) in plan.ColumnChanges.Where(kv => kv.Value > 0)) Log.Info($"    cambia {col}: {count:N0} filas");

    Log.Info("  analizando dependencias...");
    var planner = new DependencyPlanner(diff, existing, plan);
    var decisions = new List<(DependencyTarget Dep, DependencyDecision Decision)>();
    foreach (var dep in await ListDependenciesAsync(conn, ct, tx))
    {
      var decision = planner.Decide(new DependencyInput
      {
        Source = dep.Source, Table = $"{dep.Schema}.{dep.Table}", Column = dep.Column, References = dep.References,
        Strategy = dep.Strategy, IsNullable = dep.IsNullable, RowsByReferencedId = await ReferenceCountsAsync(conn, tx, dep, ct),
      });
      decisions.Add((dep, decision));
      result.DependencyOutcomes.Add(decision.Outcome);
    }

    SyncPlanner.ApplyRetention(plan, existing,
      decisions.Where(d => d.Dep.References == "nodes").SelectMany(d => d.Decision.Retain),
      decisions.Where(d => d.Dep.References == "segments").SelectMany(d => d.Decision.Retain),
      _o.Nodes.Valence is not null);
    stats.NodesRetained = plan.RetainedNodes;
    stats.SegmentsRetained = plan.RetainedSegments;
    stats.NodesUpdated = plan.NodeUpdates.Count;
    stats.SegmentsUpdated = plan.SegmentUpdates.Count;
    result.ColumnChanges = plan.ColumnChanges;
    var existingCount = existing.Segments.Count;
    existing = null!;

    // ---------------------------------------------------------------- aplicar (misma transacción)
    if (flags.Backup && !flags.DryRun) result.Backups.AddRange(await BackupAsync(conn, tx, ct));

    var heavy = plan.SegmentInserts.Count + plan.SegmentUpdates.Count > 0.05 * Math.Max(1, existingCount);
    var indexes = heavy ? await DisableIndexesAsync(conn, tx, ct) : [];

    var now = DateTime.UtcNow;
    Log.Info("  insertando y actualizando...");
    await InsertNodesAsync(conn, tx, plan.NodeInserts, meta.NodeSpatial, now, ct, meta.NodeCols);
    await UpdateNodesAsync(conn, tx, plan.NodeUpdates, meta.NodeSpatial, ct, meta.NodeCols);
    await UpdateSegmentsAsync(conn, tx, plan.SegmentUpdates, meta, ct);
    await InsertSegmentsAsync(conn, tx, plan.SegmentInserts, meta.SegSpatial, now, meta.HighwayLength, meta.NameLength, ct, meta.SegCols);
    await RebuildIndexesAsync(conn, tx, indexes, ct);

    Log.Info("  aplicando dependencias...");
    foreach (var (dep, decision) in decisions.Where(d => d.Decision.Map.Count > 0))
      await ApplyDependencyMapAsync(conn, tx, dep, decision.Map, ct);

    Log.Info("  retirando elementos...");
    await DeleteIdsAsync(conn, tx, SegsQ, _o.Segments.Id, plan.RetiredSegmentIds, ct);
    await DeleteIdsAsync(conn, tx, NodesQ, _o.Nodes.Id, plan.RetiredNodeIds, ct);

    result.NodesWritten = plan.NodeInserts.Count;
    result.SegmentsWritten = plan.SegmentInserts.Count;

    if (flags.DryRun)
    {
      await tx.RollbackAsync(ct);
      // IDENTITY no es transaccional: se devuelve a donde estaba para no dejar un hueco de Ids
      await ReseedAsync(conn, NodesQ, nodeIdent, ct);
      await ReseedAsync(conn, SegsQ, segIdent, ct);
      result.Steps.Add("Simulación completa dentro de una transacción y revertida: no se modificó nada.");
    }
    else
    {
      await tx.CommitAsync(ct);
    }

    result.Elapsed = sw.Elapsed;
    return result;
  }

  // ==================================================================== escritura

  private async Task<long> InsertNodesAsync(SqlConnection conn, SqlTransaction tx, IEnumerable<NodeRow> rows, string spatial, DateTime now, CancellationToken ct, Dictionary<string, ColumnInfo>? nodeCols = null)
  {
    await ExecAsync(conn, tx, "CREATE TABLE #osm_nodes (id bigint NOT NULL, wkb varbinary(64) NOT NULL, valence int NOT NULL);", ct);
    var count = await BulkAsync<NodeRow>(conn, tx, "#osm_nodes",
      [("id", typeof(long), r => r.Id), ("wkb", typeof(byte[]), r => Ewkb.PointWkb(r.Lon, r.Lat)), ("valence", typeof(int), r => r.Valence)], rows, ct);

    if (count > 0)
    {
      var n = _o.Nodes;
      var cols = new List<string> { Q(n.Id), Q(n.Location) };
      var vals = new List<string> { "s.id", $"{spatial}::STGeomFromWKB(s.wkb, {Srid})" };
      if (n.Valence is { } v && (nodeCols == null || nodeCols.ContainsKey(v))) { cols.Add(Q(v)); vals.Add("s.valence"); }
      if (n.CreatedAt is { } c && (nodeCols == null || nodeCols.ContainsKey(c))) { cols.Add(Q(c)); vals.Add("@now"); }
      await ExecAsync(conn, tx, $"""
        SET IDENTITY_INSERT {NodesQ} ON;
        INSERT INTO {NodesQ} ({string.Join(", ", cols)}) SELECT {string.Join(", ", vals)} FROM #osm_nodes s;
        SET IDENTITY_INSERT {NodesQ} OFF;
        """, ct, ("@now", now));
    }

    await ExecAsync(conn, tx, "DROP TABLE #osm_nodes;", ct);
    return count;
  }

  private async Task<long> InsertSegmentsAsync(SqlConnection conn, SqlTransaction tx, IEnumerable<SegmentRow> rows, string spatial, DateTime now,
    int highwayLength, int nameLength, CancellationToken ct, Dictionary<string, ColumnInfo>? segCols = null)
  {
    await CreateSegmentStageAsync(conn, tx, "#osm_segs", ct);
    var count = await BulkAsync(conn, tx, "#osm_segs", SegmentStageColumns(highwayLength, nameLength), rows, ct);

    if (count > 0)
    {
      var (cols, vals) = SegmentWriteColumns(spatial, segCols);
      cols.Insert(0, Q(_o.Segments.Id));
      vals.Insert(0, "s.id");
      if (_o.Segments.CreatedAt is { } c && (segCols == null || segCols.ContainsKey(c))) { cols.Add(Q(c)); vals.Add("@now"); }
      await ExecAsync(conn, tx, $"""
        SET IDENTITY_INSERT {SegsQ} ON;
        INSERT INTO {SegsQ} ({string.Join(", ", cols)}) SELECT {string.Join(", ", vals)} FROM #osm_segs s;
        SET IDENTITY_INSERT {SegsQ} OFF;
        """, ct, ("@now", now));
    }

    await ExecAsync(conn, tx, "DROP TABLE #osm_segs;", ct);
    return count;
  }

  private async Task UpdateNodesAsync(SqlConnection conn, SqlTransaction tx, List<NodeRow> rows, string spatial, CancellationToken ct, Dictionary<string, ColumnInfo>? nodeCols = null)
  {
    if (rows.Count == 0) return;
    var n = _o.Nodes;
    await ExecAsync(conn, tx, "CREATE TABLE #osm_upd_nodes (id bigint PRIMARY KEY, wkb varbinary(64) NOT NULL, valence int NOT NULL, loc_changed bit NOT NULL);", ct);
    await BulkAsync<NodeRow>(conn, tx, "#osm_upd_nodes",
      [("id", typeof(long), r => r.Id), ("wkb", typeof(byte[]), r => Ewkb.PointWkb(r.Lon, r.Lat)), ("valence", typeof(int), r => r.Valence), ("loc_changed", typeof(bool), r => r.LocationChanged)], rows, ct);

    // geometría solo donde cambió (evita mantener el índice espacial por nada)
    await ExecAsync(conn, tx, $"UPDATE t SET t.{Q(n.Location)} = {spatial}::STGeomFromWKB(u.wkb, {Srid}) FROM {NodesQ} t JOIN #osm_upd_nodes u ON t.{Q(n.Id)} = u.id WHERE u.loc_changed = 1;", ct);
    if (n.Valence is { } v && (nodeCols == null || nodeCols.ContainsKey(v)))
      await ExecAsync(conn, tx, $"UPDATE t SET t.{Q(v)} = u.valence FROM {NodesQ} t JOIN #osm_upd_nodes u ON t.{Q(n.Id)} = u.id WHERE t.{Q(v)} <> u.valence;", ct);
    await ExecAsync(conn, tx, "DROP TABLE #osm_upd_nodes;", ct);
  }

  private async Task UpdateSegmentsAsync(SqlConnection conn, SqlTransaction tx, List<SegmentRow> rows, TableMeta meta, CancellationToken ct)
  {
    if (rows.Count == 0) return;
    var s = _o.Segments;
    await CreateSegmentStageAsync(conn, tx, "#osm_upd_segs", ct);
    await BulkAsync(conn, tx, "#osm_upd_segs", SegmentStageColumns(meta.HighwayLength, meta.NameLength), rows, ct);

    bool HasCol(string? col) => col is not null && (meta.SegCols == null || meta.SegCols.ContainsKey(col));

    var sets = new List<string>
    {
      $"t.{Q(s.OsmId)} = u.osm_id", $"t.{Q(s.HighwayType)} = u.highway",
      $"t.{Q(s.FromNodeId)} = u.from_id", $"t.{Q(s.ToNodeId)} = u.to_id",
    };
    if (HasCol(s.Name)) sets.Add($"t.{Q(s.Name!)} = u.name");
    if (HasCol(s.IsOneWay)) sets.Add($"t.{Q(s.IsOneWay!)} = u.is_oneway");
    if (HasCol(s.Lanes)) sets.Add($"t.{Q(s.Lanes!)} = u.lanes");
    if (HasCol(s.MaxSpeed)) sets.Add($"t.{Q(s.MaxSpeed!)} = u.max_speed");
    if (HasCol(s.Surface)) sets.Add($"t.{Q(s.Surface!)} = u.surface");
    if (HasCol(s.Smoothness)) sets.Add($"t.{Q(s.Smoothness!)} = u.smoothness");
    if (HasCol(s.TrackType)) sets.Add($"t.{Q(s.TrackType!)} = u.tracktype");
    if (HasCol(s.Bridge)) sets.Add($"t.{Q(s.Bridge!)} = u.bridge");
    if (HasCol(s.Tunnel)) sets.Add($"t.{Q(s.Tunnel!)} = u.tunnel");
    if (HasCol(s.Layer)) sets.Add($"t.{Q(s.Layer!)} = u.layer");
    if (HasCol(s.MaxWeight)) sets.Add($"t.{Q(s.MaxWeight!)} = u.max_weight");
    if (HasCol(s.MaxHeight)) sets.Add($"t.{Q(s.MaxHeight!)} = u.max_height");
    if (HasCol(s.MaxWidth)) sets.Add($"t.{Q(s.MaxWidth!)} = u.max_width");
    if (HasCol(s.LengthMeters)) sets.Add($"t.{Q(s.LengthMeters!)} = u.length_m");
    if (HasCol(s.TravelCost)) sets.Add($"t.{Q(s.TravelCost!)} = u.travel_cost");

    await ExecAsync(conn, tx, $"UPDATE t SET {string.Join(", ", sets)} FROM {SegsQ} t JOIN #osm_upd_segs u ON t.{Q(s.Id)} = u.id;", ct);
    await ExecAsync(conn, tx, $"UPDATE t SET t.{Q(s.Geometry)} = {meta.SegSpatial}::STGeomFromWKB(u.wkb, {Srid}) FROM {SegsQ} t JOIN #osm_upd_segs u ON t.{Q(s.Id)} = u.id WHERE u.geom_changed = 1;", ct);
    await ExecAsync(conn, tx, "DROP TABLE #osm_upd_segs;", ct);
  }

  private static Task CreateSegmentStageAsync(SqlConnection conn, SqlTransaction tx, string name, CancellationToken ct) =>
    ExecAsync(conn, tx, $"""
      CREATE TABLE {name} (
        id bigint PRIMARY KEY, osm_id bigint NOT NULL, name nvarchar(4000) NULL, highway nvarchar(400) NOT NULL,
        is_oneway bit NOT NULL, lanes int NULL, max_speed int NULL,
        surface nvarchar(100) NULL, smoothness nvarchar(100) NULL, tracktype nvarchar(50) NULL,
        bridge bit NOT NULL, tunnel bit NOT NULL, layer int NOT NULL,
        max_weight float NULL, max_height float NULL, max_width float NULL,
        wkb varbinary(64) NOT NULL, length_m float NOT NULL, travel_cost float NOT NULL,
        from_id bigint NOT NULL, to_id bigint NOT NULL, geom_changed bit NOT NULL);
      """, ct);

  private static (string, Type, Func<SegmentRow, object?>)[] SegmentStageColumns(int highwayLength, int nameLength) =>
  [
    ("id", typeof(long), r => r.Id),
    ("osm_id", typeof(long), r => r.Src.OsmId),
    ("name", typeof(string), r => r.Src.Name is { } n && n.Length > nameLength ? n[..nameLength] : r.Src.Name),
    ("highway", typeof(string), r => r.Src.Highway.Length > highwayLength ? r.Src.Highway[..highwayLength] : r.Src.Highway),
    ("is_oneway", typeof(bool), r => r.Src.IsOneWay),
    ("lanes", typeof(int), r => r.Src.Lanes),
    ("max_speed", typeof(int), r => r.Src.MaxSpeed),
    ("surface", typeof(string), r => r.Src.Surface),
    ("smoothness", typeof(string), r => r.Src.Smoothness),
    ("tracktype", typeof(string), r => r.Src.TrackType),
    ("bridge", typeof(bool), r => r.Src.Bridge),
    ("tunnel", typeof(bool), r => r.Src.Tunnel),
    ("layer", typeof(int), r => r.Src.Layer),
    ("max_weight", typeof(double), r => r.Src.MaxWeight),
    ("max_height", typeof(double), r => r.Src.MaxHeight),
    ("max_width", typeof(double), r => r.Src.MaxWidth),
    ("wkb", typeof(byte[]), r => Ewkb.LineWkb(r.Src.X1, r.Src.Y1, r.Src.X2, r.Src.Y2)),
    ("length_m", typeof(double), r => r.Src.LengthMeters),
    ("travel_cost", typeof(double), r => r.Src.TravelCost),
    ("from_id", typeof(long), r => r.FromId),
    ("to_id", typeof(long), r => r.ToId),
    ("geom_changed", typeof(bool), r => r.GeometryChanged),
  ];

  private (List<string> Cols, List<string> Vals) SegmentWriteColumns(string spatial, Dictionary<string, ColumnInfo>? segCols = null)
  {
    var s = _o.Segments;
    var cols = new List<string> { Q(s.OsmId), Q(s.HighwayType), Q(s.Geometry), Q(s.FromNodeId), Q(s.ToNodeId) };
    var vals = new List<string> { "s.osm_id", "s.highway", $"{spatial}::STGeomFromWKB(s.wkb, {Srid})", "s.from_id", "s.to_id" };
    void Opt(string? col, string expr)
    {
      if (col is not null && (segCols == null || segCols.ContainsKey(col)))
      {
        cols.Add(Q(col));
        vals.Add(expr);
      }
    }
    Opt(s.Name, "s.name");
    Opt(s.IsOneWay, "s.is_oneway");
    Opt(s.Lanes, "s.lanes");
    Opt(s.MaxSpeed, "s.max_speed");
    Opt(s.Surface, "s.surface");
    Opt(s.Smoothness, "s.smoothness");
    Opt(s.TrackType, "s.tracktype");
    Opt(s.Bridge, "s.bridge");
    Opt(s.Tunnel, "s.tunnel");
    Opt(s.Layer, "s.layer");
    Opt(s.MaxWeight, "s.max_weight");
    Opt(s.MaxHeight, "s.max_height");
    Opt(s.MaxWidth, "s.max_width");
    Opt(s.LengthMeters, "s.length_m");
    Opt(s.TravelCost, "s.travel_cost");
    return (cols, vals);
  }

  private static async Task<long> BulkAsync<T>(SqlConnection conn, SqlTransaction tx, string table,
    (string Name, Type Type, Func<T, object?> Get)[] cols, IEnumerable<T> rows, CancellationToken ct)
  {
    using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx) { DestinationTableName = table, BulkCopyTimeout = 0, BatchSize = BulkChunk };
    var dt = new DataTable();
    foreach (var (name, type, _) in cols)
    {
      dt.Columns.Add(name, type).AllowDBNull = true;
      bulk.ColumnMappings.Add(name, name);
    }

    long total = 0;
    var values = new object?[cols.Length];
    foreach (var row in rows)
    {
      for (var i = 0; i < cols.Length; i++) values[i] = cols[i].Get(row) ?? DBNull.Value;
      dt.Rows.Add(values);
      total++;
      if (dt.Rows.Count == BulkChunk)
      {
        await bulk.WriteToServerAsync(dt, ct);
        dt.Clear();
        if (total % 500_000 == 0) Log.Debug($"    {total:N0}...");
      }
    }
    if (dt.Rows.Count > 0) await bulk.WriteToServerAsync(dt, ct);
    return total;
  }

  private static async Task DeleteIdsAsync(SqlConnection conn, SqlTransaction tx, string table, string idCol, IEnumerable<long> ids, CancellationToken ct)
  {
    await ExecAsync(conn, tx, "CREATE TABLE #osm_del (id bigint PRIMARY KEY);", ct);
    var count = await BulkAsync<long>(conn, tx, "#osm_del", [("id", typeof(long), id => id)], ids, ct);
    if (count > 0) await ExecAsync(conn, tx, $"DELETE t FROM {table} t JOIN #osm_del d ON t.{Q(idCol)} = d.id;", ct);
    await ExecAsync(conn, tx, "DROP TABLE #osm_del;", ct);
  }

  private async Task ApplyDependencyMapAsync(SqlConnection conn, SqlTransaction tx, DependencyTarget dep, Dictionary<long, long?> map, CancellationToken ct)
  {
    await ExecAsync(conn, tx, "CREATE TABLE #osm_map (old_id bigint PRIMARY KEY, new_id bigint NULL);", ct);
    await BulkAsync<KeyValuePair<long, long?>>(conn, tx, "#osm_map", [("old_id", typeof(long), kv => kv.Key), ("new_id", typeof(long), kv => kv.Value)], map, ct);
    var col = $"d.{Q(dep.Column)}";
    var join = dep.IsText ? $"{col} = CAST(m.old_id AS nvarchar(40))" : $"{col} = m.old_id";
    var value = dep.IsText ? "CAST(m.new_id AS nvarchar(40))" : "m.new_id";
    await ExecAsync(conn, tx, $"UPDATE d SET {col} = {value} FROM {Qualified(dep.Table, dep.Schema)} d JOIN #osm_map m ON {join} WHERE 1 = 1{Filter(dep)};", ct);
    await ExecAsync(conn, tx, "DROP TABLE #osm_map;", ct);
  }

  // ==================================================================== índices

  /// <summary>Deshabilita índices no únicos (incluye espaciales) de ambas tablas; se reconstruyen en la misma transacción.</summary>
  private async Task<List<(string Table, string Index)>> DisableIndexesAsync(SqlConnection conn, SqlTransaction tx, CancellationToken ct)
  {
    var list = new List<(string, string)>();
    foreach (var table in new[] { NodesQ, SegsQ })
    {
      await using var cmd = new SqlCommand("""
        SELECT name FROM sys.indexes
        WHERE object_id = OBJECT_ID(@t) AND type_desc IN ('NONCLUSTERED', 'SPATIAL') AND is_unique = 0 AND is_disabled = 0
        """, conn, tx);
      cmd.Parameters.AddWithValue("@t", table);
      await using (var r = await cmd.ExecuteReaderAsync(ct))
        while (await r.ReadAsync(ct)) list.Add((table, r.GetString(0)));
    }

    if (list.Count > 0) Log.Info($"  deshabilitando {list.Count} índices durante la carga...");
    foreach (var (table, index) in list) await ExecAsync(conn, tx, $"ALTER INDEX {Q(index)} ON {table} DISABLE;", ct);
    return list;
  }

  private static async Task RebuildIndexesAsync(SqlConnection conn, SqlTransaction tx, List<(string Table, string Index)> indexes, CancellationToken ct)
  {
    if (indexes.Count > 0) Log.Info($"  reconstruyendo {indexes.Count} índices...");
    foreach (var (table, index) in indexes) await ExecAsync(conn, tx, $"ALTER INDEX {Q(index)} ON {table} REBUILD;", ct);
  }

  // ==================================================================== DDL (create)

  private string CreateTablesDdl(string spatial)
  {
    var n = _o.Nodes;
    var s = _o.Segments;
    var nodeLines = new List<string> { $"{Q(n.Id)} bigint IDENTITY(1,1) NOT NULL", $"{Q(n.Location)} {spatial} NOT NULL" };
    if (n.Valence is { } v) nodeLines.Add($"{Q(v)} int NOT NULL CONSTRAINT {Q($"DF_{n.Table}_{v}")} DEFAULT 0");
    if (n.CreatedAt is { } nc) nodeLines.Add($"{Q(nc)} datetime2(6) NOT NULL");
    nodeLines.Add($"CONSTRAINT {Q("PK_" + n.Table)} PRIMARY KEY CLUSTERED ({Q(n.Id)})");

    var segLines = new List<string> { $"{Q(s.Id)} bigint IDENTITY(1,1) NOT NULL", $"{Q(s.OsmId)} bigint NOT NULL" };
    if (s.Name is { } name) segLines.Add($"{Q(name)} nvarchar(500) NULL");
    segLines.Add($"{Q(s.HighwayType)} nvarchar(100) NOT NULL");
    if (s.IsOneWay is { } ow) segLines.Add($"{Q(ow)} bit NOT NULL");
    if (s.Lanes is { } lanes) segLines.Add($"{Q(lanes)} int NULL");
    if (s.MaxSpeed is { } ms) segLines.Add($"{Q(ms)} int NULL");
    if (s.Surface is { } surf) segLines.Add($"{Q(surf)} nvarchar(100) NULL");
    if (s.Smoothness is { } sm) segLines.Add($"{Q(sm)} nvarchar(100) NULL");
    if (s.TrackType is { } tt) segLines.Add($"{Q(tt)} nvarchar(50) NULL");
    if (s.Bridge is { } br) segLines.Add($"{Q(br)} bit NOT NULL CONSTRAINT {Q($"DF_{s.Table}_{br}")} DEFAULT 0");
    if (s.Tunnel is { } tu) segLines.Add($"{Q(tu)} bit NOT NULL CONSTRAINT {Q($"DF_{s.Table}_{tu}")} DEFAULT 0");
    if (s.Layer is { } lay) segLines.Add($"{Q(lay)} int NOT NULL CONSTRAINT {Q($"DF_{s.Table}_{lay}")} DEFAULT 0");
    if (s.MaxWeight is { } mw) segLines.Add($"{Q(mw)} float NULL");
    if (s.MaxHeight is { } mh) segLines.Add($"{Q(mh)} float NULL");
    if (s.MaxWidth is { } mwd) segLines.Add($"{Q(mwd)} float NULL");
    segLines.Add($"{Q(s.Geometry)} {spatial} NOT NULL");
    if (s.LengthMeters is { } len) segLines.Add($"{Q(len)} float NOT NULL");
    if (s.CreatedAt is { } sc) segLines.Add($"{Q(sc)} datetime2(6) NOT NULL CONSTRAINT {Q($"DF_{s.Table}_{sc}")} DEFAULT SYSUTCDATETIME()");
    segLines.Add($"{Q(s.FromNodeId)} bigint NULL");
    segLines.Add($"{Q(s.ToNodeId)} bigint NULL");
    if (s.TravelCost is { } cost) segLines.Add($"{Q(cost)} float NOT NULL");
    segLines.Add($"CONSTRAINT {Q("PK_" + s.Table)} PRIMARY KEY CLUSTERED ({Q(s.Id)})");
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

  private IEnumerable<string> CreateIndexesDdl(string spatial)
  {
    var n = _o.Nodes;
    var s = _o.Segments;
    var grid = spatial == "geography"
      ? "USING GEOGRAPHY_AUTO_GRID"
      : "USING GEOMETRY_AUTO_GRID WITH (BOUNDING_BOX = (-180, -90, 180, 90))";
    string Ix(string table, string suffix) => Q($"IX_{table}_{suffix}");

    yield return $"CREATE SPATIAL INDEX {Ix(n.Table, n.Location)} ON {NodesQ} ({Q(n.Location)}) {grid};";
    yield return $"CREATE SPATIAL INDEX {Ix(s.Table, s.Geometry)} ON {SegsQ} ({Q(s.Geometry)}) {grid};";
    yield return $"CREATE INDEX {Ix(s.Table, s.FromNodeId)} ON {SegsQ} ({Q(s.FromNodeId)});";
    yield return $"CREATE INDEX {Ix(s.Table, s.ToNodeId)} ON {SegsQ} ({Q(s.ToNodeId)});";
    yield return $"CREATE INDEX {Ix(s.Table, s.OsmId)} ON {SegsQ} ({Q(s.OsmId)});";
    yield return $"CREATE INDEX {Ix(s.Table, s.HighwayType)} ON {SegsQ} ({Q(s.HighwayType)});";
    if (s.IsOneWay is { } ow) yield return $"CREATE INDEX {Ix(s.Table, $"{s.HighwayType}_{ow}")} ON {SegsQ} ({Q(s.HighwayType)}, {Q(ow)});";
  }

  // ==================================================================== catálogo

  private sealed record ColumnInfo(string Name, string DataType, bool Nullable, int? MaxLength, bool Computed);

  private sealed record TableMeta(string NodeSpatial, string SegSpatial, int HighwayLength, int NameLength,
    Dictionary<string, ColumnInfo>? SegCols = null, Dictionary<string, ColumnInfo>? NodeCols = null);

  private async Task<Dictionary<string, ColumnInfo>> ColumnsAsync(SqlConnection conn, SqlTransaction? tx, string? schema, string table, CancellationToken ct)
  {
    await using var cmd = new SqlCommand("""
      SELECT c.name, t.name, c.is_nullable,
             CASE WHEN t.name IN ('nvarchar', 'nchar') AND c.max_length > 0 THEN c.max_length / 2 WHEN c.max_length > 0 THEN c.max_length ELSE NULL END,
             c.is_computed
      FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
      WHERE c.object_id = OBJECT_ID(@t)
      """, conn, tx);
    cmd.Parameters.AddWithValue("@t", Qualified(table, schema));
    var d = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    while (await r.ReadAsync(ct))
      d[r.GetString(0)] = new ColumnInfo(r.GetString(0), r.GetString(1).ToLowerInvariant(), r.GetBoolean(2),
        r.IsDBNull(3) ? null : Convert.ToInt32(r.GetValue(3)), r.GetBoolean(4));
    return d;
  }

  private async Task<TableMeta> PreflightExistingAsync(SqlConnection conn, CancellationToken ct)
  {
    if (!await ExistsAsync(conn, null, _o.Nodes.Table, ct) || !await ExistsAsync(conn, null, _o.Segments.Table, ct))
      throw new InvalidOperationException($"No existen {NodesQ} / {SegsQ}. Para una base nueva usa --mode create.");

    var nodeCols = await ColumnsAsync(conn, null, null, _o.Nodes.Table, ct);
    var segCols = await ColumnsAsync(conn, null, null, _o.Segments.Table, ct);
    var s = _o.Segments;
    Require(_o.Nodes.Table, nodeCols,
      new[] { _o.Nodes.Id, _o.Nodes.Location }.OfType<string>(),
      new[] { _o.Nodes.Valence, _o.Nodes.CreatedAt }.OfType<string>());
    Require(s.Table, segCols,
      new[] { s.Id, s.OsmId, s.HighwayType, s.Geometry, s.FromNodeId, s.ToNodeId }.OfType<string>(),
      new[] {
        s.Name, s.IsOneWay, s.Lanes, s.MaxSpeed,
        s.Surface, s.Smoothness, s.TrackType, s.Bridge, s.Tunnel, s.Layer,
        s.MaxWeight, s.MaxHeight, s.MaxWidth,
        s.LengthMeters, s.TravelCost, s.CreatedAt
      }.OfType<string>());

    string Spatial(Dictionary<string, ColumnInfo> cols, string col, string table) => cols[col].DataType is "geography" or "geometry"
      ? cols[col].DataType
      : throw new InvalidOperationException($"{table}.{col} es de tipo {cols[col].DataType}; se espera geography o geometry.");

    return new TableMeta(
      Spatial(nodeCols, _o.Nodes.Location, _o.Nodes.Table),
      Spatial(segCols, s.Geometry, s.Table),
      segCols[s.HighwayType].MaxLength ?? 4000,
      s.Name is { } nm && segCols.TryGetValue(nm, out var nmInfo) ? nmInfo.MaxLength ?? 4000 : 4000,
      segCols,
      nodeCols);
  }

  private static void Require(string table, Dictionary<string, ColumnInfo> existing, IEnumerable<string> mandatory, IEnumerable<string> optional)
  {
    var missing = mandatory.Where(c => !existing.ContainsKey(c)).ToList();
    if (missing.Count > 0)
      throw new InvalidOperationException($"La tabla {table} no tiene las columnas requeridas del perfil: {string.Join(", ", missing)}.");
    var computed = mandatory.Concat(optional).Where(c => existing.ContainsKey(c) && existing[c].Computed).ToList();
    if (computed.Count > 0)
      throw new InvalidOperationException($"{table}: {string.Join(", ", computed)} son columnas calculadas y no se pueden escribir; quítalas del mapeo.");

    foreach (var opt in optional)
    {
      if (!existing.ContainsKey(opt))
        Log.Warn($"Columna opcional '{opt}' configurada en perfil pero no existe en tabla {table}; se omitirá.");
    }
  }

  private async Task<bool> ExistsAsync(SqlConnection conn, SqlTransaction? tx, string table, CancellationToken ct, string? schema = null)
  {
    await using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@t, 'U') IS NULL THEN 0 ELSE 1 END", conn, tx);
    cmd.Parameters.AddWithValue("@t", Qualified(table, schema));
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
  }

  /// <summary>Primer Id libre por encima de MAX(Id) y del valor actual de IDENTITY (nunca reutiliza). Devuelve también el IDENTITY actual.</summary>
  private async Task<(long Next, long? Ident)> NextFreeIdAsync(SqlConnection conn, SqlTransaction tx, string table, string idCol, CancellationToken ct)
  {
    var max = await ScalarAsync(conn, tx, $"SELECT COALESCE(MAX({Q(idCol)}), 0) FROM {Qualified(table)}", ct);
    await using var cmd = new SqlCommand("SELECT IDENT_CURRENT(@t)", conn, tx);
    cmd.Parameters.AddWithValue("@t", Qualified(table));
    var ident = await cmd.ExecuteScalarAsync(ct) is { } v and not DBNull ? Convert.ToInt64(v) : (long?)null;
    return (Math.Max(max, ident ?? 0) + 1, ident);
  }

  private static async Task ReseedAsync(SqlConnection conn, string table, long? ident, CancellationToken ct)
  {
    if (ident is not { } value) return;
    await ExecAsync(conn, null, $"DBCC CHECKIDENT ('{table.Replace("'", "''")}', RESEED, {value}) WITH NO_INFOMSGS;", ct);
  }

  private async Task<List<string>> BackupAsync(SqlConnection conn, SqlTransaction tx, CancellationToken ct)
  {
    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    var list = new List<string>();
    foreach (var table in new[] { _o.Nodes.Table, _o.Segments.Table })
    {
      var bak = $"{table}_bak_{stamp}";
      Log.Info($"  respaldando {table} → {bak}...");
      await ExecAsync(conn, tx, $"SELECT * INTO {Qualified(bak)} FROM {Qualified(table)};", ct);
      list.Add($"{_schema}.{bak}");
    }
    return list;
  }

  // ==================================================================== dependencias

  private sealed record DependencyTarget(string Source, string Schema, string Table, string Column, string References,
    string? Filter, DependencyStrategy Strategy, bool IsText, bool IsNullable);

  private static string Filter(DependencyTarget dep) => string.IsNullOrWhiteSpace(dep.Filter) ? "" : $" AND ({dep.Filter})";

  private async Task<List<DependencyTarget>> ListDependenciesAsync(SqlConnection conn, CancellationToken ct, SqlTransaction? tx = null)
  {
    var list = new List<DependencyTarget>();
    var fks = new List<(string Schema, string Table, string Column, string Target)>();
    await using (var cmd = new SqlCommand("""
      SELECT OBJECT_SCHEMA_NAME(fkc.parent_object_id), OBJECT_NAME(fkc.parent_object_id), pc.name, OBJECT_NAME(fkc.referenced_object_id)
      FROM sys.foreign_key_columns fkc
      JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
      WHERE fkc.referenced_object_id IN (OBJECT_ID(@n), OBJECT_ID(@s))
        AND fkc.parent_object_id NOT IN (OBJECT_ID(@n), OBJECT_ID(@s))
      """, conn, tx))
    {
      cmd.Parameters.AddWithValue("@n", NodesQ);
      cmd.Parameters.AddWithValue("@s", SegsQ);
      await using var r = await cmd.ExecuteReaderAsync(ct);
      while (await r.ReadAsync(ct)) fks.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
    }

    foreach (var fk in fks)
    {
      var refs = string.Equals(fk.Target, _o.Segments.Table, StringComparison.OrdinalIgnoreCase) ? "segments" : "nodes";
      var declared = _o.Dependencies.FirstOrDefault(d => (d.Schema ?? _schema) == fk.Schema && d.Table == fk.Table && d.Column == fk.Column);
      var col = (await ColumnsAsync(conn, tx, fk.Schema, fk.Table, ct))[fk.Column];
      list.Add(new DependencyTarget("fk", fk.Schema, fk.Table, fk.Column, refs, declared?.Filter,
        (DependencyStrategy?)declared ?? _o.ForeignKeyDefaults, IsText(col), col.Nullable));
    }

    foreach (var d in _o.Dependencies)
    {
      var schema = d.Schema ?? _schema;
      if (fks.Any(f => f.Schema == schema && f.Table == d.Table && f.Column == d.Column)) continue;
      if (!await ExistsAsync(conn, tx, d.Table, ct, schema))
      {
        Log.Warn($"dependencia declarada {schema}.{d.Table}.{d.Column}: la tabla no existe en el destino, se omite.");
        continue;
      }
      var cols = await ColumnsAsync(conn, tx, schema, d.Table, ct);
      if (!cols.TryGetValue(d.Column, out var col))
        throw new InvalidOperationException($"La dependencia declarada {schema}.{d.Table}.{d.Column} no existe.");
      list.Add(new DependencyTarget("declarada", schema, d.Table, d.Column, d.References, d.Filter, d, IsText(col), col.Nullable));
    }

    return list;
  }

  private static bool IsText(ColumnInfo c) => c.DataType is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext";

  private async Task<Dictionary<long, long>> ReferenceCountsAsync(SqlConnection conn, SqlTransaction tx, DependencyTarget dep, CancellationToken ct)
  {
    var counts = new Dictionary<long, long>();
    var col = $"d.{Q(dep.Column)}";
    await using var cmd = new SqlCommand(
      $"SELECT CAST({col} AS nvarchar(100)), COUNT_BIG(*) FROM {Qualified(dep.Table, dep.Schema)} d WHERE {col} IS NOT NULL{Filter(dep)} GROUP BY {col}", conn, tx);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    while (await r.ReadAsync(ct))
      if (long.TryParse(r.GetString(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        counts[id] = counts.GetValueOrDefault(id) + r.GetInt64(1);
    return counts;
  }

  // ==================================================================== lectura / diagnóstico

  public async Task<ExistingNetwork> ReadExistingAsync(CancellationToken ct = default)
  {
    await using var conn = await OpenAsync(ct);
    var meta = await PreflightExistingAsync(conn, ct);
    return await ReadExistingAsync(conn, null, meta, full: false, ct);
  }

  private async Task<ExistingNetwork> ReadExistingAsync(SqlConnection conn, SqlTransaction? tx, TableMeta meta, bool full, CancellationToken ct)
  {
    var n = _o.Nodes;
    var s = _o.Segments;
    string Lon(string g, string spatial) => spatial == "geography" ? $"{g}.Long" : $"{g}.STX";
    string Lat(string g, string spatial) => spatial == "geography" ? $"{g}.Lat" : $"{g}.STY";
    string Opt(string? c, string fallback) => full && c is not null && (meta.SegCols == null || meta.SegCols.ContainsKey(c)) ? Q(c) : fallback;
    string OptNode(string? c, string fallback) => full && c is not null && (meta.NodeCols == null || meta.NodeCols.ContainsKey(c)) ? Q(c) : fallback;

    var nodes = new List<ExistingNode>();
    var loc = Q(n.Location);
    await using (var cmd = new SqlCommand(
      $"SELECT {Q(n.Id)}, {Lon(loc, meta.NodeSpatial)}, {Lat(loc, meta.NodeSpatial)}, {OptNode(n.Valence, "0")} FROM {NodesQ}", conn, tx))
    await using (var r = await cmd.ExecuteReaderAsync(ct))
      while (await r.ReadAsync(ct))
        nodes.Add(new ExistingNode(r.GetInt64(0), r.GetDouble(1), r.GetDouble(2), Convert.ToInt32(r.GetValue(3))));

    var g = Q(s.Geometry);
    var sp = meta.SegSpatial;
    var len = s.LengthMeters is { } l && (meta.SegCols == null || meta.SegCols.ContainsKey(l)) ? Q(l) : (sp == "geography" ? $"{g}.STLength()" : "0");
    var segments = new List<ExistingSegment>();
    await using (var cmd = new SqlCommand($"""
      SELECT {Q(s.Id)}, {Q(s.OsmId)}, {Q(s.HighwayType)},
             {Lon($"{g}.STStartPoint()", sp)}, {Lat($"{g}.STStartPoint()", sp)},
             {Lon($"{g}.STEndPoint()", sp)}, {Lat($"{g}.STEndPoint()", sp)}, {len},
             {Opt(s.Name, "NULL")}, {Opt(s.IsOneWay, "CAST(0 AS bit)")}, {Opt(s.Lanes, "NULL")}, {Opt(s.MaxSpeed, "NULL")},
             {Opt(s.TravelCost, "CAST(0 AS float)")}, {Q(s.FromNodeId)}, {Q(s.ToNodeId)}
      FROM {SegsQ}
      """, conn, tx))
    await using (var r = await cmd.ExecuteReaderAsync(ct))
    {
      while (await r.ReadAsync(ct))
      {
        segments.Add(new ExistingSegment(
          r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), Convert.ToDouble(r.GetValue(7)),
          r.IsDBNull(8) ? null : r.GetString(8), Convert.ToBoolean(r.GetValue(9)),
          r.IsDBNull(10) ? null : Convert.ToInt32(r.GetValue(10)), r.IsDBNull(11) ? null : Convert.ToInt32(r.GetValue(11)),
          Convert.ToDouble(r.GetValue(12)), r.IsDBNull(13) ? null : r.GetInt64(13), r.IsDBNull(14) ? null : r.GetInt64(14)));
      }
    }

    return new ExistingNetwork { Nodes = nodes, Segments = segments };
  }

  public async Task<Dictionary<string, string>> DiagnosticsAsync(CancellationToken ct = default)
  {
    await using var conn = await OpenAsync(ct);
    await using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) + ' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(100))", conn);
    return new Dictionary<string, string> { ["sqlserver"] = Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? "?" };
  }

  // ==================================================================== utilidades

  private static List<long> Range(long start, int count)
  {
    var list = new List<long>(count);
    for (var i = 0; i < count; i++) list.Add(start + i);
    return list;
  }

  private static async Task ExecAsync(SqlConnection conn, SqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
  {
    await using var cmd = new SqlCommand(sql, conn, tx);
    foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task<long> ScalarAsync(SqlConnection conn, SqlTransaction? tx, string sql, CancellationToken ct)
  {
    await using var cmd = new SqlCommand(sql, conn, tx);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
  }

  public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
