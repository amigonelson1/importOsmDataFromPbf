using System.Diagnostics;
using System.Globalization;
using System.Text;
using MySqlConnector;
using OsmImporter.Core;
using OsmImporter.Core.Output;
using OsmImporter.Publish.Sync;

namespace OsmImporter.Publish.MySql;

/// <summary>
/// Publica en MySQL 8 (InnoDB). Diferencias con Postgres que condicionan el diseño:
/// - SRID 4326 en MySQL es lat-long: se escribe con ST_GeomFromWKB(wkb, 4326, 'axis-order=long-lat') y se lee con
///   ST_Longitude/ST_Latitude, así el dato queda idéntico a lo que guarda Pomelo/EF.
/// - El DDL hace COMMIT implícito: create no es atómico (si falla se borran las tablas creadas); replace y sync
///   no usan DDL dentro de la transacción (los índices quedan vivos; InnoDB no toca índices secundarios al
///   actualizar columnas no indexadas, así que no hace falta quitarlos).
/// - Sin KNN ni índices espaciales en tablas temporales: el diff, los cambios de atributos y las reasignaciones
///   de dependencias se calculan en memoria (SyncPlanner / DependencyPlanner) y aquí solo se ejecutan.
/// - Las filas se envían como literales en lotes (strings en hex con _utf8mb4: inmune a sql_mode).
/// </summary>
public sealed class MySqlPublisher : IPublisher
{
  private const int Srid = 4326;
  private const int BatchRows = 2000;

  private readonly PublishOptions _o;
  private readonly string _connectionString;
  private readonly string _database;

  public string Target { get; }

  public MySqlPublisher(string connectionString, PublishOptions options)
  {
    options.Validate();
    _o = options;
    var b = new MySqlConnectionStringBuilder(connectionString) { DefaultCommandTimeout = 0, ConnectionTimeout = 30 };
    _connectionString = b.ConnectionString;
    _database = !string.IsNullOrWhiteSpace(_o.Schema) && _o.Schema != "public" ? _o.Schema! : b.Database;
    if (string.IsNullOrWhiteSpace(_database))
      throw new ArgumentException("MySQL: indica la base de datos en la cadena de conexión (Database=...) o en publish.schema.");
    Target = $"mysql://{b.Server}:{b.Port}/{_database}  [{_o.Nodes.Table}, {_o.Segments.Table}]";
  }

  private static string Q(string ident) => "`" + ident.Replace("`", "``") + "`";
  private string Qualified(string table, string? schema = null) => Q(schema ?? _database) + "." + Q(table);
  private string NodesQ => Qualified(_o.Nodes.Table);
  private string SegsQ => Qualified(_o.Segments.Table);

  private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
  {
    var conn = new MySqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    await ExecAsync(conn, null, "SET SESSION time_zone = '+00:00';", ct);
    return conn;
  }

  // ==================================================================== publish

  public async Task<PublishResult> PublishAsync(GeoPackageReader gpkg, PublishMode mode, PublishFlags flags, CancellationToken ct = default)
  {
    return mode switch
    {
      PublishMode.Create => await CreateAsync(gpkg, flags, ct),
      PublishMode.Replace => await ReplaceAsync(gpkg, flags, ct),
      _ => await SyncAsync(gpkg, flags, ct),
    };
  }

  private async Task<PublishResult> CreateAsync(GeoPackageReader gpkg, PublishFlags flags, CancellationToken ct)
  {
    var sw = Stopwatch.StartNew();
    var result = new PublishResult { Target = Target, Mode = PublishMode.Create, DryRun = flags.DryRun };
    await using var conn = await OpenAsync(ct);

    if (await ExistsAsync(conn, _o.Nodes.Table, ct) || await ExistsAsync(conn, _o.Segments.Table, ct))
      throw new InvalidOperationException($"Las tablas ya existen en {_database}. Usa --mode replace o sync.");

    result.Steps.Add($"Crear {NodesQ} y {SegsQ} (esquema estándar de red vial; columnas generadas de extremos: {(_o.Segments.StartPoint is null ? "no" : "sí")})");
    result.Steps.Add($"Cargar {gpkg.NodeCount:N0} nodos y {gpkg.SegmentCount:N0} segmentos (lotes de {BatchRows})");
    result.Steps.Add("Crear índices espaciales y btree");
    result.Steps.Add("Nota: en MySQL el DDL no es transaccional; si algo falla se borran las tablas creadas.");
    if (flags.DryRun) { result.Elapsed = sw.Elapsed; return result; }

    Log.Info("  creando tablas...");
    await ExecAsync(conn, null, CreateTablesDdl(), ct);
    try
    {
      var now = DateTime.UtcNow;
      var nodeCols = await ColumnsAsync(conn, _o.Nodes.Table, ct);
      var segCols = await ColumnsAsync(conn, _o.Segments.Table, ct);

      // Desactivar temporalmente foreign keys y unique checks para carga masiva ultra-rápida
      await ExecAsync(conn, null, "SET SESSION foreign_key_checks = 0; SET SESSION unique_checks = 0;", ct);
      try
      {
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
          Log.Info("  cargando nodos...");
          result.NodesWritten = await InsertNodesAsync(conn, tx, gpkg.Nodes().Select(n => new NodeRow(n.Fid, n.Lon, n.Lat, n.Valence)), nodeCols, now, ct);
          Log.Info("  cargando segmentos...");
          result.SegmentsWritten = await InsertSegmentsAsync(conn, tx, gpkg.Segments().Select(s => new SegmentRow(s.Fid, s, s.FromNodeFid, s.ToNodeFid)), segCols, now, ct);
          await tx.CommitAsync(ct);
        }
      }
      finally
      {
        await ExecAsync(conn, null, "SET SESSION foreign_key_checks = 1; SET SESSION unique_checks = 1;", ct);
      }

      Log.Info("  creando índices...");
      try { await ExecAsync(conn, null, "SET SESSION innodb_ddl_buffer_size = 268435456;", ct); } catch { /* ignorar si versión < 8.0.27 */ }
      try { await ExecAsync(conn, null, "SET SESSION innodb_sort_buffer_size = 67108864;", ct); } catch { /* ignorar */ }

      foreach (var ddl in CreateIndexesDdl()) await ExecAsync(conn, null, ddl, ct);
      await ExecAsync(conn, null, $"ANALYZE TABLE {NodesQ}, {SegsQ};", ct);
    }
    catch
    {
      Log.Warn("falló la carga: se borran las tablas recién creadas.");
      try { await ExecAsync(conn, null, $"DROP TABLE IF EXISTS {SegsQ}, {NodesQ};", CancellationToken.None); } catch { /* ignorar */ }
      throw;
    }

    result.Elapsed = sw.Elapsed;
    return result;
  }

  private async Task<PublishResult> ReplaceAsync(GeoPackageReader gpkg, PublishFlags flags, CancellationToken ct)
  {
    var sw = Stopwatch.StartNew();
    var result = new PublishResult { Target = Target, Mode = PublishMode.Replace, DryRun = flags.DryRun };
    await using var conn = await OpenAsync(ct);
    var (nodeCols, segCols) = await PreflightExistingAsync(conn, ct);

    result.PreviousNodes = await ScalarAsync(conn, $"SELECT COUNT(*) FROM {NodesQ}", ct);
    result.PreviousSegments = await ScalarAsync(conn, $"SELECT COUNT(*) FROM {SegsQ}", ct);

    foreach (var dep in await ListDependenciesAsync(conn, ct))
    {
      var count = await ScalarAsync(conn, $"SELECT COUNT(*) FROM {Qualified(dep.Table, dep.Schema)} d WHERE NULLIF(CAST(d.{Q(dep.Column)} AS CHAR), '') IS NOT NULL{Filter(dep)}", ct);
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

    if (flags.Backup) result.Backups.AddRange(await BackupAsync(conn, ct));

    var now = DateTime.UtcNow;
    await using (var tx = await conn.BeginTransactionAsync(ct))
    {
      Log.Info("  borrando red actual...");
      await ExecAsync(conn, tx, $"DELETE FROM {SegsQ};", ct);
      await ExecAsync(conn, tx, $"DELETE FROM {NodesQ};", ct);
      Log.Info("  cargando nodos...");
      result.NodesWritten = await InsertNodesAsync(conn, tx, gpkg.Nodes().Select(n => new NodeRow(n.Fid, n.Lon, n.Lat, n.Valence)), nodeCols, now, ct);
      Log.Info("  cargando segmentos...");
      result.SegmentsWritten = await InsertSegmentsAsync(conn, tx, gpkg.Segments().Select(s => new SegmentRow(s.Fid, s, s.FromNodeFid, s.ToNodeFid)), segCols, now, ct);
      await tx.CommitAsync(ct);
    }

    await ExecAsync(conn, null, $"ANALYZE TABLE {NodesQ}, {SegsQ};", ct);
    result.Elapsed = sw.Elapsed;
    return result;
  }

  private async Task<PublishResult> SyncAsync(GeoPackageReader gpkg, PublishFlags flags, CancellationToken ct)
  {
    var sw = Stopwatch.StartNew();
    var result = new PublishResult { Target = Target, Mode = PublishMode.Sync, DryRun = flags.DryRun };
    await using var conn = await OpenAsync(ct);
    var (nodeCols, segCols) = await PreflightExistingAsync(conn, ct);

    // ---------------------------------------------------------------- diff + plan (en memoria)
    Log.Info("  leyendo red existente...");
    var existing = await ReadExistingAsync(conn, full: true, ct);
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

    var nodeStart = await NextFreeIdAsync(conn, _o.Nodes.Table, _o.Nodes.Id, ct);
    var segStart = await NextFreeIdAsync(conn, _o.Segments.Table, _o.Segments.Id, ct);
    diff.AssignNewIds(Range(nodeStart, diff.NewNodeCount), Range(segStart, diff.NewSegmentCount));

    var plan = SyncPlanner.Plan(diff, existing, MappedColumns.From(_o));
    foreach (var (col, count) in plan.ColumnChanges.Where(kv => kv.Value > 0)) Log.Info($"    cambia {col}: {count:N0} filas");

    // Dependencias: se deciden TODAS antes de escribir (block/notNull fallan sin tocar nada)
    Log.Info("  analizando dependencias...");
    var planner = new DependencyPlanner(diff, existing, plan);
    var decisions = new List<(DependencyTarget Dep, DependencyDecision Decision)>();
    foreach (var dep in await ListDependenciesAsync(conn, ct))
    {
      var input = new DependencyInput
      {
        Source = dep.Source, Table = $"{dep.Schema}.{dep.Table}", Column = dep.Column, References = dep.References,
        Strategy = dep.Strategy, IsNullable = dep.IsNullable,
        RowsByReferencedId = await ReferenceCountsAsync(conn, dep, ct),
      };
      var decision = planner.Decide(input);
      decisions.Add((dep, decision));
      result.DependencyOutcomes.Add(decision.Outcome);
    }

    var retainNodes = decisions.Where(d => d.Dep.References == "nodes").SelectMany(d => d.Decision.Retain);
    var retainSegs = decisions.Where(d => d.Dep.References == "segments").SelectMany(d => d.Decision.Retain);
    SyncPlanner.ApplyRetention(plan, existing, retainNodes, retainSegs, _o.Nodes.Valence is not null);
    stats.NodesRetained = plan.RetainedNodes;
    stats.SegmentsRetained = plan.RetainedSegments;
    stats.NodesUpdated = plan.NodeUpdates.Count;
    stats.SegmentsUpdated = plan.SegmentUpdates.Count;
    result.ColumnChanges = plan.ColumnChanges;
    existing = null!;

    // ---------------------------------------------------------------- aplicar
    if (flags.Backup && !flags.DryRun) result.Backups.AddRange(await BackupAsync(conn, ct));

    var now = DateTime.UtcNow;
    await using (var tx = await conn.BeginTransactionAsync(ct))
    {
      Log.Info("  insertando y actualizando...");
      await InsertNodesAsync(conn, tx, plan.NodeInserts, nodeCols, now, ct);
      await UpdateNodesAsync(conn, tx, plan.NodeUpdates, ct);
      await UpdateSegmentsAsync(conn, tx, plan.SegmentUpdates, segCols, ct);
      await InsertSegmentsAsync(conn, tx, plan.SegmentInserts, segCols, now, ct);

      Log.Info("  aplicando dependencias...");
      foreach (var (dep, decision) in decisions.Where(d => d.Decision.Map.Count > 0))
        await ApplyDependencyMapAsync(conn, tx, dep, decision.Map, ct);

      Log.Info("  retirando elementos...");
      await DeleteIdsAsync(conn, tx, _o.Segments.Table, _o.Segments.Id, plan.RetiredSegmentIds, ct);
      await DeleteIdsAsync(conn, tx, _o.Nodes.Table, _o.Nodes.Id, plan.RetiredNodeIds, ct);

      result.NodesWritten = plan.NodeInserts.Count;
      result.SegmentsWritten = plan.SegmentInserts.Count;

      if (flags.DryRun)
      {
        await tx.RollbackAsync(ct);
        // AUTO_INCREMENT no es transaccional en InnoDB: tras el rollback se devuelve a donde estaba para que la
        // simulación no deje un hueco de Ids (MySQL nunca lo baja por debajo de MAX(Id)+1).
        await ExecAsync(conn, null, $"ALTER TABLE {NodesQ} AUTO_INCREMENT = {nodeStart};", ct);
        await ExecAsync(conn, null, $"ALTER TABLE {SegsQ} AUTO_INCREMENT = {segStart};", ct);
        result.Steps.Add("Simulación completa dentro de una transacción y revertida: no se modificó nada.");
      }
      else
      {
        await tx.CommitAsync(ct);
      }
    }

    if (!flags.DryRun) await ExecAsync(conn, null, $"ANALYZE TABLE {NodesQ}, {SegsQ};", ct);
    result.Elapsed = sw.Elapsed;
    return result;
  }

  // ==================================================================== escritura por lotes

  private async Task<long> InsertNodesAsync(MySqlConnection conn, MySqlTransaction? tx, IEnumerable<NodeRow> rows,
    Dictionary<string, ColumnInfo> cols, DateTime now, CancellationToken ct)
  {
    var n = _o.Nodes;
    var fields = new List<(string Col, Func<NodeRow, string> Lit)>
    {
      (n.Id, r => Num(r.Id)),
      (n.Location, r => Geom(Ewkb.PointWkb(r.Lon, r.Lat))),
    };
    if (n.Valence is { } v) fields.Add((v, r => Num(r.Valence)));
    if (n.CreatedAt is { } c) fields.Add((c, _ => Date(now)));
    return await InsertAsync(conn, tx, NodesQ, fields, rows, ct);
  }

  private async Task<long> InsertSegmentsAsync(MySqlConnection conn, MySqlTransaction? tx, IEnumerable<SegmentRow> rows,
    Dictionary<string, ColumnInfo> cols, DateTime now, CancellationToken ct)
  {
    var s = _o.Segments;
    var nameLen = s.Name is { } nm && cols.TryGetValue(nm, out var nc) ? nc.MaxLength : null;
    var hwLen = cols.TryGetValue(s.HighwayType, out var hc) ? hc.MaxLength : null;
    var fields = new List<(string Col, Func<SegmentRow, string> Lit)>
    {
      (s.Id, r => Num(r.Id)),
      (s.OsmId, r => Num(r.Src.OsmId)),
      (s.HighwayType, r => Str(r.Src.Highway, hwLen)),
      (s.Geometry, r => Geom(Ewkb.LineWkb(r.Src.X1, r.Src.Y1, r.Src.X2, r.Src.Y2))),
      (s.FromNodeId, r => Num(r.FromId)),
      (s.ToNodeId, r => Num(r.ToId)),
    };
    if (s.Name is { } name && cols.ContainsKey(name)) fields.Add((name, r => Str(r.Src.Name, nameLen)));
    if (s.IsOneWay is { } ow && cols.ContainsKey(ow)) fields.Add((ow, r => r.Src.IsOneWay ? "1" : "0"));
    if (s.Lanes is { } l && cols.ContainsKey(l)) fields.Add((l, r => NumOrNull(r.Src.Lanes)));
    if (s.MaxSpeed is { } ms && cols.ContainsKey(ms)) fields.Add((ms, r => NumOrNull(r.Src.MaxSpeed)));
    if (s.Surface is { } surf && cols.ContainsKey(surf)) fields.Add((surf, r => Str(r.Src.Surface, 100)));
    if (s.Smoothness is { } sm && cols.ContainsKey(sm)) fields.Add((sm, r => Str(r.Src.Smoothness, 100)));
    if (s.TrackType is { } tt && cols.ContainsKey(tt)) fields.Add((tt, r => Str(r.Src.TrackType, 50)));
    if (s.Bridge is { } br && cols.ContainsKey(br)) fields.Add((br, r => r.Src.Bridge ? "1" : "0"));
    if (s.Tunnel is { } tu && cols.ContainsKey(tu)) fields.Add((tu, r => r.Src.Tunnel ? "1" : "0"));
    if (s.Layer is { } lay && cols.ContainsKey(lay)) fields.Add((lay, r => Num(r.Src.Layer)));
    if (s.MaxWeight is { } mw && cols.ContainsKey(mw)) fields.Add((mw, r => DblOrNull(r.Src.MaxWeight)));
    if (s.MaxHeight is { } mh && cols.ContainsKey(mh)) fields.Add((mh, r => DblOrNull(r.Src.MaxHeight)));
    if (s.MaxWidth is { } mwd && cols.ContainsKey(mwd)) fields.Add((mwd, r => DblOrNull(r.Src.MaxWidth)));
    if (s.LengthMeters is { } len && cols.ContainsKey(len)) fields.Add((len, r => Dbl(r.Src.LengthMeters)));
    if (s.TravelCost is { } cost && cols.ContainsKey(cost)) fields.Add((cost, r => Dbl(r.Src.TravelCost)));
    if (s.CreatedAt is { } c && cols.ContainsKey(c)) fields.Add((c, _ => Date(now)));
    return await InsertAsync(conn, tx, SegsQ, fields, rows, ct);
  }

  private static async Task<long> InsertAsync<T>(MySqlConnection conn, MySqlTransaction? tx, string table,
    List<(string Col, Func<T, string> Lit)> fields, IEnumerable<T> rows, CancellationToken ct)
  {
    var head = $"INSERT INTO {table} ({string.Join(", ", fields.Select(f => Q(f.Col)))}) VALUES ";
    var sb = new StringBuilder(1 << 20);
    long total = 0;
    var inBatch = 0;

    foreach (var row in rows)
    {
      sb.Append(inBatch == 0 ? head : ",").Append('(');
      for (var i = 0; i < fields.Count; i++)
      {
        if (i > 0) sb.Append(',');
        sb.Append(fields[i].Lit(row));
      }
      sb.Append(')');
      total++;

      if (++inBatch == BatchRows)
      {
        await ExecAsync(conn, tx, sb.ToString(), ct);
        sb.Clear();
        inBatch = 0;
        if (total % 200_000 == 0) Log.Debug($"    {total:N0}...");
      }
    }

    if (inBatch > 0) await ExecAsync(conn, tx, sb.ToString(), ct);
    return total;
  }

  private async Task UpdateNodesAsync(MySqlConnection conn, MySqlTransaction tx, List<NodeRow> rows, CancellationToken ct)
  {
    if (rows.Count == 0) return;
    var n = _o.Nodes;
    await ExecAsync(conn, tx, $"CREATE TEMPORARY TABLE _osm_upd_nodes (id BIGINT PRIMARY KEY, loc POINT NOT NULL SRID {Srid}, valence INT NOT NULL, loc_changed TINYINT NOT NULL);", ct);
    await InsertAsync(conn, tx, "_osm_upd_nodes",
      [("id", r => Num(r.Id)), ("loc", r => Geom(Ewkb.PointWkb(r.Lon, r.Lat))), ("valence", r => Num(r.Valence)), ("loc_changed", r => r.LocationChanged ? "1" : "0")], rows, ct);

    // Asignar el mismo valor es un no-op para InnoDB: la geometría (y su índice espacial) solo se toca si cambió
    var sets = new List<string> { $"t.{Q(n.Location)} = IF(u.loc_changed = 1, u.loc, t.{Q(n.Location)})" };
    if (n.Valence is { } v) sets.Add($"t.{Q(v)} = u.valence");
    await ExecAsync(conn, tx, $"UPDATE {NodesQ} t JOIN _osm_upd_nodes u ON t.{Q(n.Id)} = u.id SET {string.Join(", ", sets)};", ct);
    await ExecAsync(conn, tx, "DROP TEMPORARY TABLE _osm_upd_nodes;", ct);
  }

  private async Task UpdateSegmentsAsync(MySqlConnection conn, MySqlTransaction tx, List<SegmentRow> rows, Dictionary<string, ColumnInfo> segCols, CancellationToken ct)
  {
    if (rows.Count == 0) return;
    var s = _o.Segments;
    await ExecAsync(conn, tx, $"""
      CREATE TEMPORARY TABLE _osm_upd_segs (
        id BIGINT PRIMARY KEY, osm_id BIGINT NOT NULL, name VARCHAR(2000) NULL, highway VARCHAR(200) NOT NULL,
        is_oneway TINYINT NOT NULL, lanes INT NULL, max_speed INT NULL,
        surface VARCHAR(100) NULL, smoothness VARCHAR(100) NULL, tracktype VARCHAR(50) NULL,
        bridge TINYINT NOT NULL, tunnel TINYINT NOT NULL, layer INT NOT NULL,
        max_weight DOUBLE NULL, max_height DOUBLE NULL, max_width DOUBLE NULL,
        geom LINESTRING NOT NULL SRID {Srid},
        length_m DOUBLE NOT NULL, travel_cost DOUBLE NOT NULL, from_id BIGINT NOT NULL, to_id BIGINT NOT NULL,
        geom_changed TINYINT NOT NULL);
      """, ct);
    await InsertAsync<SegmentRow>(conn, tx, "_osm_upd_segs",
    [
      ("id", r => Num(r.Id)), ("osm_id", r => Num(r.Src.OsmId)), ("name", r => Str(r.Src.Name, 2000)), ("highway", r => Str(r.Src.Highway, 200)),
      ("is_oneway", r => r.Src.IsOneWay ? "1" : "0"), ("lanes", r => NumOrNull(r.Src.Lanes)), ("max_speed", r => NumOrNull(r.Src.MaxSpeed)),
      ("surface", r => Str(r.Src.Surface, 100)), ("smoothness", r => Str(r.Src.Smoothness, 100)), ("tracktype", r => Str(r.Src.TrackType, 50)),
      ("bridge", r => r.Src.Bridge ? "1" : "0"), ("tunnel", r => r.Src.Tunnel ? "1" : "0"), ("layer", r => Num(r.Src.Layer)),
      ("max_weight", r => DblOrNull(r.Src.MaxWeight)), ("max_height", r => DblOrNull(r.Src.MaxHeight)), ("max_width", r => DblOrNull(r.Src.MaxWidth)),
      ("geom", r => Geom(Ewkb.LineWkb(r.Src.X1, r.Src.Y1, r.Src.X2, r.Src.Y2))), ("length_m", r => Dbl(r.Src.LengthMeters)),
      ("travel_cost", r => Dbl(r.Src.TravelCost)), ("from_id", r => Num(r.FromId)), ("to_id", r => Num(r.ToId)),
      ("geom_changed", r => r.GeometryChanged ? "1" : "0"),
    ], rows, ct);

    var sets = new List<string>
    {
      // geometría solo si cambió: si no, MySQL recalcularía las columnas generadas y 3 índices espaciales por fila
      $"t.{Q(s.OsmId)} = u.osm_id", $"t.{Q(s.HighwayType)} = u.highway",
      $"t.{Q(s.Geometry)} = IF(u.geom_changed = 1, u.geom, t.{Q(s.Geometry)})",
      $"t.{Q(s.FromNodeId)} = u.from_id", $"t.{Q(s.ToNodeId)} = u.to_id",
    };
    if (s.Name is { } name && segCols.ContainsKey(name)) sets.Add($"t.{Q(name)} = u.name");
    if (s.IsOneWay is { } ow && segCols.ContainsKey(ow)) sets.Add($"t.{Q(ow)} = u.is_oneway");
    if (s.Lanes is { } l && segCols.ContainsKey(l)) sets.Add($"t.{Q(l)} = u.lanes");
    if (s.MaxSpeed is { } ms && segCols.ContainsKey(ms)) sets.Add($"t.{Q(ms)} = u.max_speed");
    if (s.Surface is { } surf && segCols.ContainsKey(surf)) sets.Add($"t.{Q(surf)} = u.surface");
    if (s.Smoothness is { } sm && segCols.ContainsKey(sm)) sets.Add($"t.{Q(sm)} = u.smoothness");
    if (s.TrackType is { } tt && segCols.ContainsKey(tt)) sets.Add($"t.{Q(tt)} = u.tracktype");
    if (s.Bridge is { } br && segCols.ContainsKey(br)) sets.Add($"t.{Q(br)} = u.bridge");
    if (s.Tunnel is { } tu && segCols.ContainsKey(tu)) sets.Add($"t.{Q(tu)} = u.tunnel");
    if (s.Layer is { } lay && segCols.ContainsKey(lay)) sets.Add($"t.{Q(lay)} = u.layer");
    if (s.MaxWeight is { } mw && segCols.ContainsKey(mw)) sets.Add($"t.{Q(mw)} = u.max_weight");
    if (s.MaxHeight is { } mh && segCols.ContainsKey(mh)) sets.Add($"t.{Q(mh)} = u.max_height");
    if (s.MaxWidth is { } mwd && segCols.ContainsKey(mwd)) sets.Add($"t.{Q(mwd)} = u.max_width");
    if (s.LengthMeters is { } len && segCols.ContainsKey(len)) sets.Add($"t.{Q(len)} = u.length_m");
    if (s.TravelCost is { } cost && segCols.ContainsKey(cost)) sets.Add($"t.{Q(cost)} = u.travel_cost");
    await ExecAsync(conn, tx, $"UPDATE {SegsQ} t JOIN _osm_upd_segs u ON t.{Q(s.Id)} = u.id SET {string.Join(", ", sets)};", ct);
    await ExecAsync(conn, tx, "DROP TEMPORARY TABLE _osm_upd_segs;", ct);
  }

  private async Task DeleteIdsAsync(MySqlConnection conn, MySqlTransaction tx, string table, string idCol, IEnumerable<long> ids, CancellationToken ct)
  {
    foreach (var chunk in ids.Order().Chunk(1000))
      await ExecAsync(conn, tx, $"DELETE FROM {Qualified(table)} WHERE {Q(idCol)} IN ({string.Join(",", chunk)});", ct);
  }

  private async Task ApplyDependencyMapAsync(MySqlConnection conn, MySqlTransaction tx, DependencyTarget dep, Dictionary<long, long?> map, CancellationToken ct)
  {
    await ExecAsync(conn, tx, "CREATE TEMPORARY TABLE _osm_map (old_id BIGINT PRIMARY KEY, new_id BIGINT NULL);", ct);
    await InsertAsync<KeyValuePair<long, long?>>(conn, tx, "_osm_map", [("old_id", kv => Num(kv.Key)), ("new_id", kv => NumOrNull(kv.Value))], map, ct);

    var col = $"d.{Q(dep.Column)}";
    var join = dep.IsText ? $"{col} = CAST(m.old_id AS CHAR)" : $"{col} = m.old_id";
    var value = dep.IsText ? "CAST(m.new_id AS CHAR)" : "m.new_id";
    await ExecAsync(conn, tx, $"UPDATE {Qualified(dep.Table, dep.Schema)} d JOIN _osm_map m ON {join} SET {col} = {value} WHERE 1=1{Filter(dep)};", ct);
    await ExecAsync(conn, tx, "DROP TEMPORARY TABLE _osm_map;", ct);
  }

  // ==================================================================== literales

  private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);
  private static string NumOrNull(long? v) => v is { } x ? Num(x) : "NULL";
  private static string NumOrNull(int? v) => v is { } x ? Num(x) : "NULL";
  private static string Dbl(double v) => v.ToString("R", CultureInfo.InvariantCulture);
  private static string DblOrNull(double? v) => v.HasValue ? Dbl(v.Value) : "NULL";
  private static string Date(DateTime v) => "'" + v.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'";
  private static string Geom(byte[] wkb) => $"ST_GeomFromWKB(X'{Convert.ToHexString(wkb)}', {Srid}, 'axis-order=long-lat')";

  /// <summary>Texto como hex con introductor _utf8mb4: no depende de sql_mode (NO_BACKSLASH_ESCAPES, ANSI_QUOTES).</summary>
  private static string Str(string? v, int? maxLength)
  {
    if (v is null) return "NULL";
    if (maxLength is { } m && v.Length > m) v = v[..m];
    return v.Length == 0 ? "''" : "_utf8mb4 X'" + Convert.ToHexString(Encoding.UTF8.GetBytes(v)) + "'";
  }

  // ==================================================================== DDL (create)

  private string CreateTablesDdl()
  {
    var n = _o.Nodes;
    var s = _o.Segments;
    var nodeLines = new List<string>
    {
      $"{Q(n.Id)} BIGINT NOT NULL AUTO_INCREMENT",
      $"{Q(n.Location)} POINT NOT NULL SRID {Srid}",
    };
    if (n.Valence is { } v) nodeLines.Add($"{Q(v)} INT NOT NULL DEFAULT 0");
    if (n.CreatedAt is { } nc) nodeLines.Add($"{Q(nc)} DATETIME(6) NOT NULL");
    nodeLines.Add($"PRIMARY KEY ({Q(n.Id)})");

    var segLines = new List<string>
    {
      $"{Q(s.Id)} BIGINT NOT NULL AUTO_INCREMENT",
      $"{Q(s.OsmId)} BIGINT NOT NULL",
    };
    if (s.Name is { } name) segLines.Add($"{Q(name)} VARCHAR(500) NULL");
    segLines.Add($"{Q(s.HighwayType)} VARCHAR(100) NOT NULL");
    if (s.IsOneWay is { } ow) segLines.Add($"{Q(ow)} TINYINT(1) NOT NULL");
    if (s.Lanes is { } lanes) segLines.Add($"{Q(lanes)} INT NULL");
    if (s.MaxSpeed is { } ms) segLines.Add($"{Q(ms)} INT NULL");
    if (s.Surface is { } surf) segLines.Add($"{Q(surf)} VARCHAR(100) NULL");
    if (s.Smoothness is { } sm) segLines.Add($"{Q(sm)} VARCHAR(100) NULL");
    if (s.TrackType is { } tt) segLines.Add($"{Q(tt)} VARCHAR(50) NULL");
    if (s.Bridge is { } br) segLines.Add($"{Q(br)} TINYINT(1) NOT NULL DEFAULT 0");
    if (s.Tunnel is { } tu) segLines.Add($"{Q(tu)} TINYINT(1) NOT NULL DEFAULT 0");
    if (s.Layer is { } lay) segLines.Add($"{Q(lay)} INT NOT NULL DEFAULT 0");
    if (s.MaxWeight is { } mw) segLines.Add($"{Q(mw)} DOUBLE NULL");
    if (s.MaxHeight is { } mh) segLines.Add($"{Q(mh)} DOUBLE NULL");
    if (s.MaxWidth is { } mwd) segLines.Add($"{Q(mwd)} DOUBLE NULL");
    segLines.Add($"{Q(s.Geometry)} LINESTRING NOT NULL SRID {Srid}");
    if (s.LengthMeters is { } len) segLines.Add($"{Q(len)} DOUBLE NOT NULL");
    if (s.CreatedAt is { } sc) segLines.Add($"{Q(sc)} DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)");
    segLines.Add($"{Q(s.FromNodeId)} BIGINT NULL");
    segLines.Add($"{Q(s.ToNodeId)} BIGINT NULL");
    if (s.TravelCost is { } cost) segLines.Add($"{Q(cost)} DOUBLE NOT NULL");
    // En columnas GENERADAS el SRID va después de STORED/NOT NULL (sintaxis validada por SherpaSlb, Spike B)
    if (s.StartPoint is { } sp) segLines.Add($"{Q(sp)} POINT GENERATED ALWAYS AS (ST_StartPoint({Q(s.Geometry)})) STORED NOT NULL SRID {Srid}");
    if (s.EndPoint is { } ep) segLines.Add($"{Q(ep)} POINT GENERATED ALWAYS AS (ST_EndPoint({Q(s.Geometry)})) STORED NOT NULL SRID {Srid}");
    segLines.Add($"PRIMARY KEY ({Q(s.Id)})");
    segLines.Add($"KEY {Q($"IX_{s.Table}_{s.FromNodeId}")} ({Q(s.FromNodeId)})");
    segLines.Add($"KEY {Q($"IX_{s.Table}_{s.ToNodeId}")} ({Q(s.ToNodeId)})");
    segLines.Add($"CONSTRAINT {Q($"FK_{s.Table}_{n.Table}_{s.FromNodeId}")} FOREIGN KEY ({Q(s.FromNodeId)}) REFERENCES {NodesQ} ({Q(n.Id)})");
    segLines.Add($"CONSTRAINT {Q($"FK_{s.Table}_{n.Table}_{s.ToNodeId}")} FOREIGN KEY ({Q(s.ToNodeId)}) REFERENCES {NodesQ} ({Q(n.Id)})");

    return $"""
      CREATE TABLE {NodesQ} (
        {string.Join(",\n  ", nodeLines)}
      ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
      CREATE TABLE {SegsQ} (
        {string.Join(",\n  ", segLines)}
      ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
      """;
  }

  private IEnumerable<string> CreateIndexesDdl()
  {
    var n = _o.Nodes;
    var s = _o.Segments;
    yield return $"ALTER TABLE {NodesQ} ADD SPATIAL INDEX {Q($"IX_{n.Table}_{n.Location}")} ({Q(n.Location)});";

    var parts = new List<string>
    {
      $"ADD SPATIAL INDEX {Q($"IX_{s.Table}_{s.Geometry}")} ({Q(s.Geometry)})",
      $"ADD INDEX {Q($"IX_{s.Table}_{s.OsmId}")} ({Q(s.OsmId)})",
      $"ADD INDEX {Q($"IX_{s.Table}_{s.HighwayType}")} ({Q(s.HighwayType)})",
    };
    if (s.IsOneWay is { } ow) parts.Add($"ADD INDEX {Q($"IX_{s.Table}_{s.HighwayType}_{ow}")} ({Q(s.HighwayType)}, {Q(ow)})");
    if (s.StartPoint is { } sp) parts.Add($"ADD SPATIAL INDEX {Q($"IX_{s.Table}_{sp}")} ({Q(sp)})");
    if (s.EndPoint is { } ep) parts.Add($"ADD SPATIAL INDEX {Q($"IX_{s.Table}_{ep}")} ({Q(ep)})");
    yield return $"ALTER TABLE {SegsQ} {string.Join(", ", parts)};";
  }

  // ==================================================================== catálogo

  private sealed record ColumnInfo(string Name, string DataType, bool Nullable, int? MaxLength, bool Generated, uint? SrsId);

  private async Task<Dictionary<string, ColumnInfo>> ColumnsAsync(MySqlConnection conn, string table, CancellationToken ct, string? schema = null)
  {
    await using var cmd = new MySqlCommand("""
      SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, CHARACTER_MAXIMUM_LENGTH, EXTRA, SRS_ID
      FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t
      """, conn);
    cmd.Parameters.AddWithValue("@s", schema ?? _database);
    cmd.Parameters.AddWithValue("@t", table);
    var d = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    while (await r.ReadAsync(ct))
    {
      var maxLen = r.IsDBNull(3) ? (int?)null : (int)Math.Min(int.MaxValue, Convert.ToInt64(r.GetValue(3)));
      var extra = r.IsDBNull(4) ? "" : r.GetString(4);
      var srs = r.IsDBNull(5) ? (uint?)null : Convert.ToUInt32(r.GetValue(5));
      d[r.GetString(0)] = new ColumnInfo(r.GetString(0), r.GetString(1).ToLowerInvariant(), r.GetString(2) == "YES", maxLen,
        extra.Contains("STORED GENERATED", StringComparison.OrdinalIgnoreCase) || extra.Contains("VIRTUAL GENERATED", StringComparison.OrdinalIgnoreCase), srs);
    }
    return d;
  }

  private async Task<(Dictionary<string, ColumnInfo> Nodes, Dictionary<string, ColumnInfo> Segs)> PreflightExistingAsync(MySqlConnection conn, CancellationToken ct)
  {
    if (!await ExistsAsync(conn, _o.Nodes.Table, ct) || !await ExistsAsync(conn, _o.Segments.Table, ct))
      throw new InvalidOperationException($"No existen {_o.Nodes.Table} / {_o.Segments.Table} en {_database}. Para una base nueva usa --mode create.");

    var nodeCols = await ColumnsAsync(conn, _o.Nodes.Table, ct);
    var segCols = await ColumnsAsync(conn, _o.Segments.Table, ct);
    Require(_o.Nodes.Table, nodeCols,
      new[] { _o.Nodes.Id, _o.Nodes.Location }.OfType<string>(),
      new[] { _o.Nodes.Valence, _o.Nodes.CreatedAt }.OfType<string>());
    var s = _o.Segments;
    Require(s.Table, segCols,
      new[] { s.Id, s.OsmId, s.HighwayType, s.Geometry, s.FromNodeId, s.ToNodeId }.OfType<string>(),
      new[] {
        s.Name, s.IsOneWay, s.Lanes, s.MaxSpeed,
        s.Surface, s.Smoothness, s.TrackType, s.Bridge, s.Tunnel, s.Layer,
        s.MaxWeight, s.MaxHeight, s.MaxWidth,
        s.LengthMeters, s.TravelCost, s.CreatedAt
      }.OfType<string>());

    foreach (var (table, col, cols) in new[] { (_o.Nodes.Table, _o.Nodes.Location, nodeCols), (s.Table, s.Geometry, segCols) })
      if (cols[col].SrsId is { } srs && srs != Srid)
        throw new InvalidOperationException($"{table}.{col} usa SRID {srs}; osmimport solo escribe SRID {Srid}.");

    return (nodeCols, segCols);
  }

  private static void Require(string table, Dictionary<string, ColumnInfo> existing, IEnumerable<string> mandatory, IEnumerable<string> optional)
  {
    var missing = mandatory.Where(c => !existing.ContainsKey(c)).ToList();
    if (missing.Count > 0)
      throw new InvalidOperationException($"La tabla {table} no tiene las columnas requeridas del perfil: {string.Join(", ", missing)}.");

    foreach (var opt in optional)
    {
      if (!existing.ContainsKey(opt))
        Log.Warn($"Columna opcional '{opt}' configurada en perfil pero no existe en tabla {table}; se omitirá.");
      else if (existing[opt].Generated)
        throw new InvalidOperationException($"{table}: {opt} es una columna generada y no se puede escribir; quítala del mapeo.");
    }
  }

  private async Task<bool> ExistsAsync(MySqlConnection conn, string table, CancellationToken ct, string? schema = null)
  {
    await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t", conn);
    cmd.Parameters.AddWithValue("@s", schema ?? _database);
    cmd.Parameters.AddWithValue("@t", table);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
  }

  private async Task<long> NextFreeIdAsync(MySqlConnection conn, string table, string idCol, CancellationToken ct)
  {
    var max = await ScalarAsync(conn, $"SELECT COALESCE(MAX({Q(idCol)}), 0) FROM {Qualified(table)}", ct);
    // information_schema.TABLES.AUTO_INCREMENT puede estar cacheado: se fuerza la lectura actual
    await ExecAsync(conn, null, "SET SESSION information_schema_stats_expiry = 0;", ct);
    await using var cmd = new MySqlCommand("SELECT AUTO_INCREMENT FROM information_schema.TABLES WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t", conn);
    cmd.Parameters.AddWithValue("@s", _database);
    cmd.Parameters.AddWithValue("@t", table);
    var auto = await cmd.ExecuteScalarAsync(ct) is { } v and not DBNull ? Convert.ToInt64(v) : 0;
    return Math.Max(max + 1, auto);
  }

  private async Task<List<string>> BackupAsync(MySqlConnection conn, CancellationToken ct)
  {
    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    var list = new List<string>();
    foreach (var table in new[] { _o.Nodes.Table, _o.Segments.Table })
    {
      var bak = $"{table}_bak_{stamp}";
      Log.Info($"  respaldando {table} → {bak}...");
      await ExecAsync(conn, null, $"CREATE TABLE {Qualified(bak)} AS SELECT * FROM {Qualified(table)};", ct);
      list.Add($"{_database}.{bak}");
    }
    return list;
  }

  // ==================================================================== dependencias

  private sealed record DependencyTarget(string Source, string Schema, string Table, string Column, string References,
    string? Filter, DependencyStrategy Strategy, bool IsText, bool IsNullable);

  private static string Filter(DependencyTarget dep) => string.IsNullOrWhiteSpace(dep.Filter) ? "" : $" AND ({dep.Filter})";

  private async Task<List<DependencyTarget>> ListDependenciesAsync(MySqlConnection conn, CancellationToken ct)
  {
    var list = new List<DependencyTarget>();
    var fks = new List<(string Schema, string Table, string Column, string Target)>();
    await using (var cmd = new MySqlCommand("""
      SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, REFERENCED_TABLE_NAME
      FROM information_schema.KEY_COLUMN_USAGE
      WHERE REFERENCED_TABLE_SCHEMA = @s AND REFERENCED_TABLE_NAME IN (@n, @g)
        AND NOT (TABLE_SCHEMA = @s AND TABLE_NAME IN (@n, @g))
      """, conn))
    {
      cmd.Parameters.AddWithValue("@s", _database);
      cmd.Parameters.AddWithValue("@n", _o.Nodes.Table);
      cmd.Parameters.AddWithValue("@g", _o.Segments.Table);
      await using var r = await cmd.ExecuteReaderAsync(ct);
      while (await r.ReadAsync(ct)) fks.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
    }

    foreach (var fk in fks)
    {
      var refs = string.Equals(fk.Target, _o.Segments.Table, StringComparison.OrdinalIgnoreCase) ? "segments" : "nodes";
      var declared = _o.Dependencies.FirstOrDefault(d => (d.Schema ?? _database) == fk.Schema && d.Table == fk.Table && d.Column == fk.Column);
      var col = (await ColumnsAsync(conn, fk.Table, ct, fk.Schema))[fk.Column];
      list.Add(new DependencyTarget("fk", fk.Schema, fk.Table, fk.Column, refs, declared?.Filter,
        (DependencyStrategy?)declared ?? _o.ForeignKeyDefaults, IsText(col), col.Nullable));
    }

    foreach (var d in _o.Dependencies)
    {
      var schema = d.Schema ?? _database;
      if (fks.Any(f => f.Schema == schema && f.Table == d.Table && f.Column == d.Column)) continue;
      if (!await ExistsAsync(conn, d.Table, ct, schema))
      {
        Log.Warn($"dependencia declarada {schema}.{d.Table}.{d.Column}: la tabla no existe en el destino, se omite.");
        continue;
      }
      var cols = await ColumnsAsync(conn, d.Table, ct, schema);
      if (!cols.TryGetValue(d.Column, out var col))
        throw new InvalidOperationException($"La dependencia declarada {schema}.{d.Table}.{d.Column} no existe.");
      list.Add(new DependencyTarget("declarada", schema, d.Table, d.Column, d.References, d.Filter, d, IsText(col), col.Nullable));
    }

    return list;
  }

  private static bool IsText(ColumnInfo c) => c.DataType is "char" or "varchar" or "text" or "tinytext" or "mediumtext" or "longtext";

  private async Task<Dictionary<long, long>> ReferenceCountsAsync(MySqlConnection conn, DependencyTarget dep, CancellationToken ct)
  {
    var counts = new Dictionary<long, long>();
    var col = $"d.{Q(dep.Column)}";
    await using var cmd = new MySqlCommand(
      $"SELECT CAST({col} AS CHAR), COUNT(*) FROM {Qualified(dep.Table, dep.Schema)} d WHERE {col} IS NOT NULL{Filter(dep)} GROUP BY {col}", conn);
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
    await PreflightExistingAsync(conn, ct);
    return await ReadExistingAsync(conn, full: false, ct);
  }

  /// <summary>full = también atributos (para el diff de sync en memoria).</summary>
  private async Task<ExistingNetwork> ReadExistingAsync(MySqlConnection conn, bool full, CancellationToken ct)
  {
    var n = _o.Nodes;
    var s = _o.Segments;
    var nodeCols = await ColumnsAsync(conn, n.Table, ct);
    var segCols = await ColumnsAsync(conn, s.Table, ct);
    var nodeGeo = nodeCols[n.Location].SrsId == Srid;
    var segGeo = segCols[s.Geometry].SrsId == Srid;
    string Lon(string g, bool geo) => geo ? $"ST_Longitude({g})" : $"ST_X({g})";
    string Lat(string g, bool geo) => geo ? $"ST_Latitude({g})" : $"ST_Y({g})";
    string Opt(string? c, string fallback) => full && c is not null ? Q(c) : fallback;

    var nodes = new List<ExistingNode>();
    await using (var cmd = new MySqlCommand(
      $"SELECT {Q(n.Id)}, {Lon(Q(n.Location), nodeGeo)}, {Lat(Q(n.Location), nodeGeo)}, {Opt(n.Valence, "0")} FROM {NodesQ}", conn))
    await using (var r = await cmd.ExecuteReaderAsync(ct))
      while (await r.ReadAsync(ct))
        nodes.Add(new ExistingNode(r.GetInt64(0), r.GetDouble(1), r.GetDouble(2), Convert.ToInt32(r.GetValue(3))));

    var g = Q(s.Geometry);
    var len = s.LengthMeters is { } l ? Q(l) : $"ST_Length({g})";
    var segments = new List<ExistingSegment>();
    await using (var cmd = new MySqlCommand($"""
      SELECT {Q(s.Id)}, {Q(s.OsmId)}, {Q(s.HighwayType)},
             {Lon($"ST_StartPoint({g})", segGeo)}, {Lat($"ST_StartPoint({g})", segGeo)},
             {Lon($"ST_EndPoint({g})", segGeo)}, {Lat($"ST_EndPoint({g})", segGeo)}, {len},
             {Opt(s.Name, "NULL")}, {Opt(s.IsOneWay, "0")}, {Opt(s.Lanes, "NULL")}, {Opt(s.MaxSpeed, "NULL")},
             {Opt(s.TravelCost, "0")}, {Q(s.FromNodeId)}, {Q(s.ToNodeId)}
      FROM {SegsQ}
      """, conn))
    await using (var r = await cmd.ExecuteReaderAsync(ct))
    {
      while (await r.ReadAsync(ct))
      {
        segments.Add(new ExistingSegment(
          r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7),
          r.IsDBNull(8) ? null : r.GetString(8), Convert.ToInt64(r.GetValue(9)) != 0,
          r.IsDBNull(10) ? null : Convert.ToInt32(r.GetValue(10)), r.IsDBNull(11) ? null : Convert.ToInt32(r.GetValue(11)),
          Convert.ToDouble(r.GetValue(12)), r.IsDBNull(13) ? null : r.GetInt64(13), r.IsDBNull(14) ? null : r.GetInt64(14)));
      }
    }

    return new ExistingNetwork { Nodes = nodes, Segments = segments };
  }

  public async Task<Dictionary<string, string>> DiagnosticsAsync(CancellationToken ct = default)
  {
    await using var conn = await OpenAsync(ct);
    return new Dictionary<string, string>
    {
      ["mysql"] = Convert.ToString(await new MySqlCommand("SELECT VERSION()", conn).ExecuteScalarAsync(ct)) ?? "?",
    };
  }

  // ==================================================================== utilidades

  private static List<long> Range(long start, int count)
  {
    var list = new List<long>(count);
    for (var i = 0; i < count; i++) list.Add(start + i);
    return list;
  }

  private static async Task ExecAsync(MySqlConnection conn, MySqlTransaction? tx, string sql, CancellationToken ct)
  {
    await using var cmd = new MySqlCommand(sql, conn, tx);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task<long> ScalarAsync(MySqlConnection conn, string sql, CancellationToken ct)
  {
    await using var cmd = new MySqlCommand(sql, conn);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
  }

  public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
