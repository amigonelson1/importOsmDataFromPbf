using System.Diagnostics;
using Npgsql;
using OsmImporter.Core;
using OsmImporter.Core.Output;
using OsmImporter.Publish.Sync;

namespace OsmImporter.Publish.Postgres;

/// <summary>
/// publish --mode sync para PostgreSQL. Todo ocurre en UNA transacción (en dry-run se revierte al final):
///  1. LOCK de las tablas de la red (lecturas permitidas, escrituras bloqueadas).
///  2. Diff por llave natural (NetworkDiff) → conservados / nuevos / retirados.
///  3. Ids nuevos siempre por encima del máximo y de la secuencia: un Id retirado nunca se reutiliza.
///  4. Staging en tablas temporales (COPY binario).
///  5. Altas y cambios (nodos y segmentos nuevos/modificados).
///  6. Dependencias (FKs detectadas + declaradas): remapNearest | setNull | report | block.
///  7. Bajas de lo retirado (salvo lo que una FK con 'report' obliga a conservar).
/// </summary>
public sealed partial class PostgresPublisher
{
  private const string StageNodes = "_osm_sync_nodes";
  private const string StageSegs = "_osm_sync_segments";
  private const string RetiredNodes = "_osm_sync_retired_nodes";
  private const string RetiredSegs = "_osm_sync_retired_segments";
  private const string RetiredNodesAll = "_osm_sync_retired_nodes_all";
  private const string RetiredSegsAll = "_osm_sync_retired_segments_all";

  private async Task<PublishResult> SyncAsync(GeoPackageReader gpkg, PublishFlags flags, CancellationToken ct)
  {
    var sw = Stopwatch.StartNew();
    var result = new PublishResult { Target = Target, Mode = PublishMode.Sync, DryRun = flags.DryRun };

    await using var conn = await _ds.OpenConnectionAsync(ct);
    await EnsurePostgisAsync(conn, ct);

    if (!await ExistsAsync(conn, NodesQ, ct) || !await ExistsAsync(conn, SegsQ, ct))
      throw new InvalidOperationException($"sync necesita que existan {NodesQ} y {SegsQ}. Para una base nueva usa --mode create.");

    RequireColumns(_o.Nodes.Table, await ColumnsAsync(conn, _o.Nodes.Table, ct), MandatoryNodeColumns(), OptionalNodeColumns());
    RequireColumns(_o.Segments.Table, await ColumnsAsync(conn, _o.Segments.Table, ct), MandatorySegmentColumns(), OptionalSegmentColumns());
    await RequireSridAsync(conn, _o.Nodes.Table, _o.Nodes.Location, ct);
    await RequireSridAsync(conn, _o.Segments.Table, _o.Segments.Geometry, ct);
    var nodeTypes = await FormattedTypesAsync(conn, NodesQ, ct);
    var segTypes = await FormattedTypesAsync(conn, SegsQ, ct);

    await using var tx = await conn.BeginTransactionAsync(ct);
    await ExecAsync(conn, $"LOCK TABLE {NodesQ}, {SegsQ} IN EXCLUSIVE MODE;", ct);

    // ---------------------------------------------------------------- diff
    Log.Info("  leyendo red existente...");
    var existing = await ReadExistingAsync(conn, ct);
    result.PreviousNodes = existing.Nodes.Count;
    result.PreviousSegments = existing.Segments.Count;

    Log.Info("  calculando diff por llave natural...");
    var diff = NetworkDiff.Compute(gpkg, existing);
    existing = null!;

    var stats = result.Sync = new SyncStats
    {
      NodesExisting = result.PreviousNodes,
      NodesKeptExact = diff.NodesKeptExact,
      NodesKeptTolerant = diff.NodesKeptTolerant,
      NodesNew = diff.NewNodeCount,
      NodesRetired = diff.RetiredNodeIds.Count,
      SegmentsExisting = result.PreviousSegments,
      SegmentsKeptExact = diff.SegmentsKeptExact,
      SegmentsKeptTolerant = diff.SegmentsKeptTolerant,
      SegmentsNew = diff.NewSegmentCount,
      SegmentsRetired = diff.RetiredSegmentIds.Count,
    };
    Log.Info($"  segmentos: {stats.SegmentsKeptExact + stats.SegmentsKeptTolerant:N0} se conservan ({stats.SegmentsKeptTolerant:N0} con ±1 celda), " +
             $"{stats.SegmentsNew:N0} nuevos, {stats.SegmentsRetired:N0} retirados ({stats.RetiredSegmentsPercent}%)");
    Log.Info($"  nodos:     {stats.NodesKeptExact + stats.NodesKeptTolerant:N0} se conservan ({stats.NodesKeptTolerant:N0} con ±1 celda), " +
             $"{stats.NodesNew:N0} nuevos, {stats.NodesRetired:N0} retirados");

    var maxRetired = flags.MaxRetiredPercent ?? _o.MaxRetiredPercent;
    if (stats.RetiredSegmentsPercent > maxRetired)
      throw new InvalidOperationException(
        $"sync retiraría el {stats.RetiredSegmentsPercent}% de los segmentos existentes (límite {maxRetired}%). " +
        "¿El gpkg es de otra zona o se construyó con otros parámetros? Revisa con 'compare'. " +
        "Si es intencional usa --max-retired-percent <n>. No se modificó nada.");

    // ---------------------------------------------------------------- respaldo
    if (flags.Backup && !flags.DryRun)
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

    // ---------------------------------------------------------------- Ids nuevos
    var nodeStart = await NextFreeIdAsync(conn, NodesQ, _o.Nodes.Id, ct);
    var segStart = await NextFreeIdAsync(conn, SegsQ, _o.Segments.Id, ct);
    diff.AssignNewIds(Range(nodeStart, diff.NewNodeCount), Range(segStart, diff.NewSegmentCount));

    // ---------------------------------------------------------------- staging
    Log.Info("  cargando staging...");
    await ExecAsync(conn, $"""
      CREATE TEMP TABLE {StageNodes} (id bigint PRIMARY KEY, loc geometry(Point,4326) NOT NULL, valence integer NOT NULL, is_new boolean NOT NULL) ON COMMIT DROP;
      CREATE TEMP TABLE {StageSegs} (
        id bigint PRIMARY KEY, osm_id bigint NOT NULL, name text, highway text NOT NULL, is_oneway boolean NOT NULL,
        lanes integer, max_speed integer, surface text, smoothness text, tracktype text,
        bridge boolean NOT NULL, tunnel boolean NOT NULL, layer integer NOT NULL,
        max_weight double precision, max_height double precision, max_width double precision,
        geom geometry(LineString,4326) NOT NULL, length_m double precision NOT NULL,
        travel_cost double precision NOT NULL, from_id bigint NOT NULL, to_id bigint NOT NULL, is_new boolean NOT NULL) ON COMMIT DROP;
      CREATE TEMP TABLE {RetiredNodes} (id bigint PRIMARY KEY) ON COMMIT DROP;
      CREATE TEMP TABLE {RetiredSegs} (id bigint PRIMARY KEY) ON COMMIT DROP;
      """, ct);

    await CopyStageNodesAsync(conn, diff, ct);
    await CopyStageSegmentsAsync(conn, diff, ct);
    await CopyIdsAsync(conn, RetiredNodes, diff.RetiredNodeIds, ct);
    await CopyIdsAsync(conn, RetiredSegs, diff.RetiredSegmentIds, ct);
    await ExecAsync(conn, $"""
      CREATE INDEX ON {StageNodes} USING GIST (loc);
      CREATE INDEX ON {StageSegs} USING GIST (geom);
      CREATE TEMP TABLE {RetiredNodesAll} ON COMMIT DROP AS TABLE {RetiredNodes};
      CREATE TEMP TABLE {RetiredSegsAll} ON COMMIT DROP AS TABLE {RetiredSegs};
      ANALYZE {StageNodes}; ANALYZE {StageSegs}; ANALYZE {RetiredNodes}; ANALYZE {RetiredSegs};
      """, ct);

    // ---------------------------------------------------------------- aplicar (orden importa)
    // 1) altas y cambios: así las reasignaciones pueden apuntar a nodos/segmentos nuevos (las FKs
    //    se verifican al instante); 2) dependencias; 3) bajas de lo retirado.
    var plan = BuildApplyPlan(nodeTypes, segTypes);
    result.ColumnChanges = await ChangeSummaryAsync(conn, plan, ct);
    foreach (var (col, count) in result.ColumnChanges.Where(kv => kv.Value > 0))
      Log.Info($"    cambia {col}: {count:N0} filas");

    // Índices normales (no PK/UNIQUE) fuera durante las escrituras masivas: actualizar cientos de miles de
    // filas con 3 GIST + btree vivos es órdenes de magnitud más lento. Se recrean ANTES de dependencias y
    // bajas, que sí los necesitan (KNN, chequeo de FKs al borrar nodos).
    var indexes = new List<(string Name, string Definition)>();
    indexes.AddRange(await CaptureIndexesAsync(conn, NodesQ, ct));
    indexes.AddRange(await CaptureIndexesAsync(conn, SegsQ, ct));
    foreach (var (name, _) in indexes) await ExecAsync(conn, $"DROP INDEX {Qualified(name)};", ct);

    Log.Info("  insertando y actualizando...");
    await ApplyUpsertsAsync(conn, plan, stats, ct);

    Log.Info($"  recreando {indexes.Count} índices...");
    foreach (var (_, definition) in indexes) await ExecAsync(conn, definition + ";", ct);
    await ExecAsync(conn, $"ANALYZE {NodesQ}; ANALYZE {SegsQ};", ct);

    Log.Info("  resolviendo dependencias...");
    foreach (var dep in await ListDependencyTargetsAsync(conn, ct))
    {
      var outcome = await ResolveDependencyAsync(conn, dep, ct);
      result.DependencyOutcomes.Add(outcome);
    }

    Log.Info("  retirando elementos...");
    await ApplyDeletesAsync(conn, stats, ct);

    result.NodesWritten = stats.NodesNew;
    result.SegmentsWritten = stats.SegmentsNew;

    if (flags.DryRun)
    {
      await tx.RollbackAsync(ct);
      result.Steps.Add("Simulación completa dentro de una transacción y revertida: no se modificó nada.");
    }
    else
    {
      await ResetSequenceKeepingAheadAsync(conn, NodesQ, _o.Nodes.Id, ct);
      await ResetSequenceKeepingAheadAsync(conn, SegsQ, _o.Segments.Id, ct);
      await tx.CommitAsync(ct);
      Log.Info("  ANALYZE...");
      await ExecAsync(conn, $"ANALYZE {NodesQ}; ANALYZE {SegsQ};", ct);
    }

    result.Elapsed = sw.Elapsed;
    return result;
  }

  // ==================================================================== staging

  private static async Task CopyStageNodesAsync(NpgsqlConnection conn, NetworkDiff diff, CancellationToken ct)
  {
    await using var w = await conn.BeginBinaryImportAsync($"COPY {StageNodes} (id, loc, valence, is_new) FROM STDIN (FORMAT BINARY)", ct);
    for (var i = 0; i < diff.Nodes.Count; i++)
    {
      var n = diff.Nodes[i];
      await w.StartRowAsync(ct);
      await w.WriteAsync(diff.NodeIds[i], NpgsqlTypes.NpgsqlDbType.Bigint, ct);
      await w.WriteAsync(Ewkb.Point(n.Lon, n.Lat, Srid), NpgsqlTypes.NpgsqlDbType.Bytea, ct);
      await w.WriteAsync(n.Valence, NpgsqlTypes.NpgsqlDbType.Integer, ct);
      await w.WriteAsync(diff.NodeIsNew[i], NpgsqlTypes.NpgsqlDbType.Boolean, ct);
    }
    await w.CompleteAsync(ct);
  }

  private static async Task CopyStageSegmentsAsync(NpgsqlConnection conn, NetworkDiff diff, CancellationToken ct)
  {
    await using var w = await conn.BeginBinaryImportAsync(
      $"COPY {StageSegs} (id, osm_id, name, highway, is_oneway, lanes, max_speed, surface, smoothness, tracktype, bridge, tunnel, layer, max_weight, max_height, max_width, geom, length_m, travel_cost, from_id, to_id, is_new) FROM STDIN (FORMAT BINARY)", ct);
    for (var i = 0; i < diff.Segments.Count; i++)
    {
      var s = diff.Segments[i];
      await w.StartRowAsync(ct);
      await w.WriteAsync(diff.SegmentIds[i], NpgsqlTypes.NpgsqlDbType.Bigint, ct);
      await w.WriteAsync(s.OsmId, NpgsqlTypes.NpgsqlDbType.Bigint, ct);
      if (s.Name is null) await w.WriteNullAsync(ct); else await w.WriteAsync(s.Name, NpgsqlTypes.NpgsqlDbType.Text, ct);
      await w.WriteAsync(s.Highway, NpgsqlTypes.NpgsqlDbType.Text, ct);
      await w.WriteAsync(s.IsOneWay, NpgsqlTypes.NpgsqlDbType.Boolean, ct);
      if (s.Lanes is { } l) await w.WriteAsync(l, NpgsqlTypes.NpgsqlDbType.Integer, ct); else await w.WriteNullAsync(ct);
      if (s.MaxSpeed is { } m) await w.WriteAsync(m, NpgsqlTypes.NpgsqlDbType.Integer, ct); else await w.WriteNullAsync(ct);
      if (s.Surface is null) await w.WriteNullAsync(ct); else await w.WriteAsync(s.Surface, NpgsqlTypes.NpgsqlDbType.Text, ct);
      if (s.Smoothness is null) await w.WriteNullAsync(ct); else await w.WriteAsync(s.Smoothness, NpgsqlTypes.NpgsqlDbType.Text, ct);
      if (s.TrackType is null) await w.WriteNullAsync(ct); else await w.WriteAsync(s.TrackType, NpgsqlTypes.NpgsqlDbType.Text, ct);
      await w.WriteAsync(s.Bridge, NpgsqlTypes.NpgsqlDbType.Boolean, ct);
      await w.WriteAsync(s.Tunnel, NpgsqlTypes.NpgsqlDbType.Boolean, ct);
      await w.WriteAsync(s.Layer, NpgsqlTypes.NpgsqlDbType.Integer, ct);
      if (s.MaxWeight is { } mw) await w.WriteAsync(mw, NpgsqlTypes.NpgsqlDbType.Double, ct); else await w.WriteNullAsync(ct);
      if (s.MaxHeight is { } mh) await w.WriteAsync(mh, NpgsqlTypes.NpgsqlDbType.Double, ct); else await w.WriteNullAsync(ct);
      if (s.MaxWidth is { } mwd) await w.WriteAsync(mwd, NpgsqlTypes.NpgsqlDbType.Double, ct); else await w.WriteNullAsync(ct);
      await w.WriteAsync(Ewkb.Line(s.X1, s.Y1, s.X2, s.Y2, Srid), NpgsqlTypes.NpgsqlDbType.Bytea, ct);
      await w.WriteAsync(s.LengthMeters, NpgsqlTypes.NpgsqlDbType.Double, ct);
      await w.WriteAsync(s.TravelCost, NpgsqlTypes.NpgsqlDbType.Double, ct);
      await w.WriteAsync(diff.NodeIdForFid(s.FromNodeFid), NpgsqlTypes.NpgsqlDbType.Bigint, ct);
      await w.WriteAsync(diff.NodeIdForFid(s.ToNodeFid), NpgsqlTypes.NpgsqlDbType.Bigint, ct);
      await w.WriteAsync(diff.SegmentIsNew[i], NpgsqlTypes.NpgsqlDbType.Boolean, ct);
    }
    await w.CompleteAsync(ct);
  }

  private static async Task CopyIdsAsync(NpgsqlConnection conn, string table, List<long> ids, CancellationToken ct)
  {
    await using var w = await conn.BeginBinaryImportAsync($"COPY {table} (id) FROM STDIN (FORMAT BINARY)", ct);
    foreach (var id in ids)
    {
      await w.StartRowAsync(ct);
      await w.WriteAsync(id, NpgsqlTypes.NpgsqlDbType.Bigint, ct);
    }
    await w.CompleteAsync(ct);
  }

  private static List<long> Range(long start, int count)
  {
    var list = new List<long>(count);
    for (var i = 0; i < count; i++) list.Add(start + i);
    return list;
  }

  // ==================================================================== Ids

  /// <summary>Primer Id libre por encima del máximo de la tabla Y de la posición de la secuencia (nunca reutiliza).</summary>
  private static async Task<long> NextFreeIdAsync(NpgsqlConnection conn, string table, string idColumn, CancellationToken ct)
  {
    var max = await CountScalarAsync(conn, $"SELECT COALESCE(max({Q(idColumn)}), 0) FROM {table}", ct);
    var seq = await SequenceOfAsync(conn, table, idColumn, ct);
    if (seq is null) return max + 1;

    var seqPos = await CountScalarAsync(conn, $"SELECT CASE WHEN is_called THEN last_value ELSE last_value - 1 END FROM {seq}", ct);
    return Math.Max(max, seqPos) + 1;
  }

  private static async Task ResetSequenceKeepingAheadAsync(NpgsqlConnection conn, string table, string idColumn, CancellationToken ct)
  {
    var seq = await SequenceOfAsync(conn, table, idColumn, ct);
    if (seq is null) return;
    await ExecAsync(conn, $"""
      SELECT setval('{seq.Replace("'", "''")}',
        GREATEST(COALESCE((SELECT max({Q(idColumn)}) FROM {table}), 0),
                 (SELECT CASE WHEN is_called THEN last_value ELSE last_value - 1 END FROM {seq})) + 1, false);
      """, ct);
  }

  private static async Task<string?> SequenceOfAsync(NpgsqlConnection conn, string table, string idColumn, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand("SELECT pg_get_serial_sequence($1, $2)", conn);
    cmd.Parameters.Add(new() { Value = table });
    cmd.Parameters.Add(new() { Value = idColumn });
    return await cmd.ExecuteScalarAsync(ct) as string;
  }

  // ==================================================================== dependencias

  private sealed record DependencyTarget(
    string Source, string Schema, string Table, string Column, string References, string? Filter,
    DependencyStrategy Strategy, bool IsText, bool IsNullable);

  private async Task<List<DependencyTarget>> ListDependencyTargetsAsync(NpgsqlConnection conn, CancellationToken ct)
  {
    var list = new List<DependencyTarget>();
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
      var declared = _o.Dependencies.FirstOrDefault(d => (d.Schema ?? SchemaName) == fk.Schema && d.Table == fk.Table && d.Column == fk.Column);
      var (isText, nullable) = await ColumnKindAsync(conn, fk.Schema, fk.Table, fk.Column, ct);
      list.Add(new DependencyTarget("fk", fk.Schema, fk.Table, fk.Column, refs, declared?.Filter, (DependencyStrategy?)declared ?? _o.ForeignKeyDefaults, isText, nullable));
    }

    foreach (var d in _o.Dependencies)
    {
      var schema = d.Schema ?? SchemaName;
      if (fks.Any(f => f.Schema == schema && f.Table == d.Table && f.Column == d.Column)) continue;
      if (!await ExistsAsync(conn, Qualified(d.Table, schema), ct))
      {
        Log.Warn($"dependencia declarada {schema}.{d.Table}.{d.Column}: la tabla no existe en el destino, se omite.");
        continue;
      }
      var (isText, nullable) = await ColumnKindAsync(conn, schema, d.Table, d.Column, ct);
      list.Add(new DependencyTarget("declarada", schema, d.Table, d.Column, d.References, d.Filter, d, isText, nullable));
    }

    return list;
  }

  private static async Task<(bool IsText, bool Nullable)> ColumnKindAsync(NpgsqlConnection conn, string schema, string table, string column, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand("""
      SELECT data_type, is_nullable FROM information_schema.columns
      WHERE table_schema = $1 AND table_name = $2 AND column_name = $3
      """, conn);
    cmd.Parameters.Add(new() { Value = schema });
    cmd.Parameters.Add(new() { Value = table });
    cmd.Parameters.Add(new() { Value = column });
    await using var r = await cmd.ExecuteReaderAsync(ct);
    if (!await r.ReadAsync(ct)) throw new InvalidOperationException($"No existe la columna {schema}.{table}.{column}");
    var type = r.GetString(0);
    return (type is "text" or "character varying" or "character", r.GetString(1) == "YES");
  }

  private async Task<DependencyOutcome> ResolveDependencyAsync(NpgsqlConnection conn, DependencyTarget dep, CancellationToken ct)
  {
    var st = dep.Strategy;
    var outcome = new DependencyOutcome
    {
      Source = dep.Source, Table = $"{dep.Schema}.{dep.Table}", Column = dep.Column, References = dep.References,
      Strategy = st.OnRetired, Fallback = st.Fallback, MaxDistanceMeters = st.MaxDistanceMeters,
    };

    var depQ = Qualified(dep.Table, dep.Schema);
    var col = Q(dep.Column);
    var retired = dep.References == "nodes" ? RetiredNodes : RetiredSegs;
    string Cmp(string idExpr) => dep.IsText ? $"d.{col} = ({idExpr})::text" : $"d.{col} = {idExpr}";
    var filter = string.IsNullOrWhiteSpace(dep.Filter) ? "" : $" AND ({dep.Filter})";
    var newValue = dep.IsText ? "m.new_id::text" : "m.new_id";

    outcome.Affected = await CountScalarAsync(conn,
      $"SELECT count(*) FROM {depQ} d WHERE EXISTS (SELECT 1 FROM {retired} r WHERE {Cmp("r.id")}){filter}", ct);
    if (outcome.Affected == 0) return outcome;

    var label = $"{outcome.Table}.{dep.Column} → {dep.References}";
    Log.Info($"    {label}: {outcome.Affected:N0} filas apuntan a elementos retirados ({st.OnRetired})");

    if (st.OnRetired == RetiredStrategy.Block)
      throw new DependentDataException($"{label}: {outcome.Affected:N0} filas apuntan a elementos retirados y la estrategia es 'block'. No se modificó nada.");

    var pending = outcome.Affected;
    var strategy = st.OnRetired;

    if (strategy == RetiredStrategy.RemapNearest)
    {
      // Grados para el prefiltro con índice (holgado); el filtro exacto es en metros sobre la esfera.
      var deg = (st.MaxDistanceMeters / 111_320.0 * 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
      var maxd = st.MaxDistanceMeters.ToString(System.Globalization.CultureInfo.InvariantCulture);
      string nearest;
      if (dep.References == "nodes")
      {
        var loc = $"o.{Q(_o.Nodes.Location)}::geometry";
        nearest = $"""
          SELECT r.id AS old_id,
                 (SELECT s.id FROM {StageNodes} s
                   WHERE ST_DWithin(s.loc, {loc}, {deg}) AND ST_DistanceSphere(s.loc, {loc}) <= {maxd}
                   ORDER BY s.loc <-> {loc} LIMIT 1) AS new_id
          FROM {retired} r JOIN {NodesQ} o ON o.{Q(_o.Nodes.Id)} = r.id
          WHERE EXISTS (SELECT 1 FROM {depQ} d WHERE {Cmp("r.id")}{filter})
          """;
      }
      else
      {
        // Punto medio del segmento retirado; se prefiere un segmento de la misma vía OSM.
        var mid = $"ST_LineInterpolatePoint(o.{Q(_o.Segments.Geometry)}::geometry, 0.5)";
        nearest = $"""
          SELECT r.id AS old_id,
                 (SELECT s.id FROM {StageSegs} s
                   WHERE ST_DWithin(s.geom, {mid}, {deg}) AND ST_DistanceSphere(ST_ClosestPoint(s.geom, {mid}), {mid}) <= {maxd}
                   ORDER BY (s.osm_id <> o.{Q(_o.Segments.OsmId)}), s.geom <-> {mid} LIMIT 1) AS new_id
          FROM {retired} r JOIN {SegsQ} o ON o.{Q(_o.Segments.Id)} = r.id
          WHERE EXISTS (SELECT 1 FROM {depQ} d WHERE {Cmp("r.id")}{filter})
          """;
      }

      outcome.Remapped = await ExecCountAsync(conn, $"""
        WITH m AS ({nearest})
        UPDATE {depQ} d SET {col} = {newValue}
        FROM m WHERE {Cmp("m.old_id")} AND m.new_id IS NOT NULL{filter};
        """, ct);
      pending -= outcome.Remapped;
      strategy = st.Fallback;
      if (pending > 0) Log.Info($"      {outcome.Remapped:N0} reasignadas; {pending:N0} sin candidato a ≤ {st.MaxDistanceMeters} m → {strategy}");
    }

    if (pending <= 0) return outcome;

    outcome.SampleUnresolved = await SampleAsync(conn,
      $"SELECT DISTINCT d.{col}::text FROM {depQ} d WHERE EXISTS (SELECT 1 FROM {retired} r WHERE {Cmp("r.id")}){filter} LIMIT 20", ct);

    switch (strategy)
    {
      case RetiredStrategy.Block:
        throw new DependentDataException($"{label}: {pending:N0} filas sin reasignar y el fallback es 'block'. Ids: {string.Join(", ", outcome.SampleUnresolved)}. No se modificó nada.");

      case RetiredStrategy.SetNull:
        if (!dep.IsNullable)
          throw new DependentDataException($"{label}: la columna no admite NULL; cambia la estrategia (report/block) en el perfil. No se modificó nada.");
        outcome.SetNull = await ExecCountAsync(conn,
          $"UPDATE {depQ} d SET {col} = NULL WHERE EXISTS (SELECT 1 FROM {retired} r WHERE {Cmp("r.id")}){filter};", ct);
        break;

      case RetiredStrategy.Report:
        if (dep.Source == "fk")
        {
          // FK real: no se puede dejar colgando → el elemento retirado se conserva.
          outcome.Retained = await ExecCountAsync(conn,
            $"DELETE FROM {retired} r WHERE EXISTS (SELECT 1 FROM {depQ} d WHERE {Cmp("r.id")}{filter});", ct);
          Log.Warn($"{label}: se conservan {outcome.Retained:N0} elementos retirados porque {pending:N0} filas los referencian (report).");
        }
        else
        {
          outcome.Dangling = pending;
          Log.Warn($"{label}: {pending:N0} filas quedan apuntando a Ids que ya no existen (report). Ej.: {string.Join(", ", outcome.SampleUnresolved.Take(5))}");
        }
        break;
    }

    return outcome;
  }

  // ==================================================================== aplicar

  private sealed record ApplyPlan(
    List<(string Col, string Expr, ColumnKind Kind)> NodeCols, Dictionary<string, string> NodeTypes,
    List<(string Col, string Expr, ColumnKind Kind)> SegCols, Dictionary<string, string> SegTypes);

  private ApplyPlan BuildApplyPlan(Dictionary<string, string> nodeTypes, Dictionary<string, string> segTypes)
  {
    var n = _o.Nodes;
    var s = _o.Segments;

    var nodeCols = new List<(string Col, string Expr, ColumnKind Kind)> { (n.Location, "s.loc", ColumnKind.Point) };
    if (n.Valence is { } v && nodeTypes.ContainsKey(v)) nodeCols.Add((v, "s.valence", ColumnKind.Plain));

    var segCols = new List<(string Col, string Expr, ColumnKind Kind)>
    {
      (s.OsmId, "s.osm_id", ColumnKind.Plain),
      (s.HighwayType, "s.highway", ColumnKind.Plain),
      (s.Geometry, "s.geom", ColumnKind.Line),
      (s.FromNodeId, "s.from_id", ColumnKind.Plain),
      (s.ToNodeId, "s.to_id", ColumnKind.Plain),
    };
    if (s.Name is { } name && segTypes.ContainsKey(name)) segCols.Add((name, "s.name", ColumnKind.Plain));
    if (s.IsOneWay is { } ow && segTypes.ContainsKey(ow)) segCols.Add((ow, "s.is_oneway", ColumnKind.Plain));
    if (s.Lanes is { } lanes && segTypes.ContainsKey(lanes)) segCols.Add((lanes, "s.lanes", ColumnKind.Plain));
    if (s.MaxSpeed is { } ms && segTypes.ContainsKey(ms)) segCols.Add((ms, "s.max_speed", ColumnKind.Plain));
    if (s.Surface is { } surf && segTypes.ContainsKey(surf)) segCols.Add((surf, "s.surface", ColumnKind.Plain));
    if (s.Smoothness is { } sm && segTypes.ContainsKey(sm)) segCols.Add((sm, "s.smoothness", ColumnKind.Plain));
    if (s.TrackType is { } tt && segTypes.ContainsKey(tt)) segCols.Add((tt, "s.tracktype", ColumnKind.Plain));
    if (s.Bridge is { } br && segTypes.ContainsKey(br)) segCols.Add((br, "s.bridge", ColumnKind.Plain));
    if (s.Tunnel is { } tu && segTypes.ContainsKey(tu)) segCols.Add((tu, "s.tunnel", ColumnKind.Plain));
    if (s.Layer is { } lay && segTypes.ContainsKey(lay)) segCols.Add((lay, "s.layer", ColumnKind.Plain));
    if (s.MaxWeight is { } mw && segTypes.ContainsKey(mw)) segCols.Add((mw, "s.max_weight", ColumnKind.Number));
    if (s.MaxHeight is { } mh && segTypes.ContainsKey(mh)) segCols.Add((mh, "s.max_height", ColumnKind.Number));
    if (s.MaxWidth is { } mwd && segTypes.ContainsKey(mwd)) segCols.Add((mwd, "s.max_width", ColumnKind.Number));
    if (s.LengthMeters is { } len && segTypes.ContainsKey(len)) segCols.Add((len, "s.length_m", ColumnKind.Number));
    if (s.TravelCost is { } cost && segTypes.ContainsKey(cost)) segCols.Add((cost, "s.travel_cost", ColumnKind.Number));

    return new ApplyPlan(nodeCols, nodeTypes, segCols, segTypes);
  }

  private async Task ApplyUpsertsAsync(NpgsqlConnection conn, ApplyPlan p, SyncStats stats, CancellationToken ct)
  {
    var n = _o.Nodes;
    var s = _o.Segments;
    await ExecCountAsync(conn, InsertSql(NodesQ, n.Id, n.CreatedAt, p.NodeCols, p.NodeTypes, StageNodes), ct);
    stats.NodesUpdated = await ExecCountAsync(conn, UpdateSql(NodesQ, n.Id, p.NodeCols, p.NodeTypes, StageNodes), ct);
    stats.SegmentsUpdated = await ExecCountAsync(conn, UpdateSql(SegsQ, s.Id, p.SegCols, p.SegTypes, StageSegs), ct);
    await ExecCountAsync(conn, InsertSql(SegsQ, s.Id, s.CreatedAt, p.SegCols, p.SegTypes, StageSegs), ct);
  }

  private async Task ApplyDeletesAsync(NpgsqlConnection conn, SyncStats stats, CancellationToken ct)
  {
    var n = _o.Nodes;
    var s = _o.Segments;

    await ExecCountAsync(conn, $"DELETE FROM {SegsQ} t USING {RetiredSegs} r WHERE t.{Q(s.Id)} = r.id;", ct);

    // No se borra ningún nodo que siga referenciado por un segmento (p. ej. segmento conservado por report)
    await ExecAsync(conn, $"""
      DELETE FROM {RetiredNodes} r
      WHERE EXISTS (SELECT 1 FROM {SegsQ} t WHERE t.{Q(s.FromNodeId)} = r.id)
         OR EXISTS (SELECT 1 FROM {SegsQ} t WHERE t.{Q(s.ToNodeId)} = r.id);
      """, ct);
    await ExecCountAsync(conn, $"DELETE FROM {NodesQ} t USING {RetiredNodes} r WHERE t.{Q(n.Id)} = r.id;", ct);

    stats.NodesRetained = await CountScalarAsync(conn, $"SELECT count(*) FROM {RetiredNodesAll} a WHERE NOT EXISTS (SELECT 1 FROM {RetiredNodes} r WHERE r.id = a.id)", ct);
    stats.SegmentsRetained = await CountScalarAsync(conn, $"SELECT count(*) FROM {RetiredSegsAll} a WHERE NOT EXISTS (SELECT 1 FROM {RetiredSegs} r WHERE r.id = a.id)", ct);

    if (stats.NodesRetained > 0 && n.Valence is { } val)
    {
      await ExecAsync(conn, $"""
        UPDATE {NodesQ} t SET {Q(val)} =
          (SELECT count(*) FROM {SegsQ} x WHERE x.{Q(s.FromNodeId)} = t.{Q(n.Id)}) +
          (SELECT count(*) FROM {SegsQ} x WHERE x.{Q(s.ToNodeId)} = t.{Q(n.Id)})
        WHERE t.{Q(n.Id)} IN (SELECT id FROM {RetiredNodesAll} EXCEPT SELECT id FROM {RetiredNodes});
        """, ct);
    }
  }

  /// <summary>Cuántas filas conservadas cambian, por columna (una sola pasada por tabla).</summary>
  private async Task<Dictionary<string, long>> ChangeSummaryAsync(NpgsqlConnection conn, ApplyPlan p, CancellationToken ct)
  {
    var result = new Dictionary<string, long>();
    var targets = new[]
    {
      (Table: NodesQ, IdCol: _o.Nodes.Id, Cols: p.NodeCols, Types: p.NodeTypes, Stage: StageNodes, Prefix: "nodos"),
      (Table: SegsQ, IdCol: _o.Segments.Id, Cols: p.SegCols, Types: p.SegTypes, Stage: StageSegs, Prefix: "segmentos"),
    };

    foreach (var x in targets)
    {
      var filters = x.Cols.Select(c => $"count(*) FILTER (WHERE {DiffPredicate(c, x.Types)})");
      var sql = $"SELECT {string.Join(", ", filters)} FROM {x.Table} t JOIN {x.Stage} s ON t.{Q(x.IdCol)} = s.id AND NOT s.is_new";
      await using var cmd = new NpgsqlCommand(sql, conn);
      await using var r = await cmd.ExecuteReaderAsync(ct);
      await r.ReadAsync(ct);
      for (var i = 0; i < x.Cols.Count; i++) result[$"{x.Prefix}.{x.Cols[i].Col}"] = r.GetInt64(i);
    }

    return result;
  }

  private static string DiffPredicate((string Col, string Expr, ColumnKind Kind) c, Dictionary<string, string> types)
  {
    var t = $"t.{Q(c.Col)}";
    var e = $"CAST({c.Expr} AS {types[c.Col]})";
    return c.Kind switch
    {
      ColumnKind.Point => $"ST_DistanceSphere({t}::geometry, {c.Expr}) > 0.01",
      ColumnKind.Line => $"(ST_DistanceSphere(ST_StartPoint({t}::geometry), ST_StartPoint({c.Expr})) > 0.01 OR ST_DistanceSphere(ST_EndPoint({t}::geometry), ST_EndPoint({c.Expr})) > 0.01)",
      ColumnKind.Number => $"(({t} IS NULL) <> ({e} IS NULL) OR abs({t} - {e}) > 0.01)",
      _ => $"{t} IS DISTINCT FROM {e}",
    };
  }

  private enum ColumnKind { Plain, Number, Point, Line }

  private static string InsertSql(string table, string idCol, string? createdAt, List<(string Col, string Expr, ColumnKind Kind)> cols,
    Dictionary<string, string> types, string stage)
  {
    var names = new List<string> { Q(idCol) };
    var values = new List<string> { $"CAST(s.id AS {types[idCol]})" };
    foreach (var (col, expr, _) in cols) { names.Add(Q(col)); values.Add($"CAST({expr} AS {types[col]})"); }
    if (createdAt is not null) { names.Add(Q(createdAt)); values.Add($"CAST(now() AS {types[createdAt]})"); }
    return $"INSERT INTO {table} ({string.Join(", ", names)}) SELECT {string.Join(", ", values)} FROM {stage} s WHERE s.is_new;";
  }

  /// <summary>UPDATE solo de filas con cambios reales (tolerancia: 1 cm en geometría y 0,01 en números: diferencias
  /// menores son ruido de redondeo, p. ej. longitudes medidas sobre coordenadas que pasaron por EPSG:3857).</summary>
  private static string UpdateSql(string table, string idCol, List<(string Col, string Expr, ColumnKind Kind)> cols,
    Dictionary<string, string> types, string stage)
  {
    var sets = cols.Select(c => $"{Q(c.Col)} = CAST({c.Expr} AS {types[c.Col]})");
    var diffs = cols.Select(c => DiffPredicate(c, types));
    return $"""
      UPDATE {table} t SET {string.Join(", ", sets)}
      FROM {stage} s
      WHERE t.{Q(idCol)} = s.id AND NOT s.is_new
        AND ({string.Join("\n OR ", diffs)});
      """;
  }

  // ==================================================================== utilidades

  private static async Task<Dictionary<string, string>> FormattedTypesAsync(NpgsqlConnection conn, string table, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand("""
      SELECT a.attname, format_type(a.atttypid, a.atttypmod)
      FROM pg_attribute a WHERE a.attrelid = $1::regclass AND a.attnum > 0 AND NOT a.attisdropped
      """, conn);
    cmd.Parameters.Add(new() { Value = table });
    var d = new Dictionary<string, string>(StringComparer.Ordinal);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    while (await r.ReadAsync(ct)) d[r.GetString(0)] = r.GetString(1);
    return d;
  }

  private static async Task<long> ExecCountAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand(sql, conn);
    return await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task<List<string>> SampleAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
  {
    var list = new List<string>();
    await using var cmd = new NpgsqlCommand(sql, conn);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    while (await r.ReadAsync(ct)) list.Add(r.IsDBNull(0) ? "null" : r.GetString(0));
    return list;
  }
}
