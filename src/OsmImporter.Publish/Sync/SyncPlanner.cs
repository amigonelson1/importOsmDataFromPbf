using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using OsmImporter.Core.Geo;
using OsmImporter.Core.Output;

namespace OsmImporter.Publish.Sync;

/// <summary>Qué columnas opcionales escribe el perfil (las no mapeadas no se comparan ni se escriben).</summary>
public sealed record MappedColumns(bool Valence, bool Name, bool IsOneWay, bool Lanes, bool MaxSpeed, bool Length, bool Cost)
{
  public static MappedColumns From(PublishOptions o) => new(
    o.Nodes.Valence is not null, o.Segments.Name is not null, o.Segments.IsOneWay is not null, o.Segments.Lanes is not null,
    o.Segments.MaxSpeed is not null, o.Segments.LengthMeters is not null, o.Segments.TravelCost is not null);
}

/// <param name="LocationChanged">En updates: false = solo cambia valence (no se reescribe la geometría ni su índice).</param>
public sealed record NodeRow(long Id, double Lon, double Lat, int Valence, bool LocationChanged = true);

/// <param name="GeometryChanged">En updates: false = la geometría no se reescribe (evita recalcular columnas generadas e
/// índices espaciales cuando solo cambian atributos).</param>
public sealed record SegmentRow(long Id, GpkgSegment Src, long FromId, long ToId, bool GeometryChanged = true);

/// <summary>Plan completo de un sync calculado en memoria: el motor solo ejecuta INSERT/UPDATE/DELETE.</summary>
public sealed class SyncPlan
{
  public List<NodeRow> NodeInserts { get; } = new();
  public List<NodeRow> NodeUpdates { get; } = new();
  public List<SegmentRow> SegmentInserts { get; } = new();
  public List<SegmentRow> SegmentUpdates { get; } = new();
  public Dictionary<string, long> ColumnChanges { get; } = new();
  public HashSet<long> RetiredNodeIds { get; init; } = new();
  public HashSet<long> RetiredSegmentIds { get; init; } = new();
  public long RetainedNodes { get; set; }
  public long RetainedSegments { get; set; }
}

/// <summary>
/// Planificador de sync independiente del motor (MySQL y SQL Server; Postgres hace lo equivalente en SQL).
/// Mismas tolerancias que el sync de Postgres: 1 cm en geometría, 0,01 en números.
/// </summary>
public static class SyncPlanner
{
  private const double GeometryToleranceMeters = 0.01;
  private const double NumberTolerance = 0.01;

  /// <summary>Requiere que diff.AssignNewIds ya se haya llamado (Ids finales).</summary>
  public static SyncPlan Plan(NetworkDiff diff, ExistingNetwork existing, MappedColumns m)
  {
    var plan = new SyncPlan
    {
      RetiredNodeIds = new HashSet<long>(diff.RetiredNodeIds),
      RetiredSegmentIds = new HashSet<long>(diff.RetiredSegmentIds),
    };

    var nodesById = existing.Nodes.ToDictionary(n => n.Id);
    var segsById = existing.Segments.ToDictionary(s => s.Id);
    long cLoc = 0, cVal = 0;

    for (var i = 0; i < diff.Nodes.Count; i++)
    {
      var n = diff.Nodes[i];
      var row = new NodeRow(diff.NodeIds[i], n.Lon, n.Lat, n.Valence);
      if (diff.NodeIsNew[i]) { plan.NodeInserts.Add(row); continue; }

      var e = nodesById[row.Id];
      var locChanged = Geodesy.SphereDistance(e.Lon, e.Lat, n.Lon, n.Lat) > GeometryToleranceMeters;
      var valChanged = m.Valence && e.Valence != n.Valence;
      if (locChanged) cLoc++;
      if (valChanged) cVal++;
      if (locChanged || valChanged) plan.NodeUpdates.Add(row with { LocationChanged = locChanged });
    }

    plan.ColumnChanges["nodos.location"] = cLoc;
    if (m.Valence) plan.ColumnChanges["nodos.valence"] = cVal;

    var counters = new long[11];
    string[] names = new[] { "osm_id", "highway", "geometry", "from_node", "to_node", "name", "is_oneway", "lanes", "max_speed", "length", "travel_cost" };

    for (var i = 0; i < diff.Segments.Count; i++)
    {
      var s = diff.Segments[i];
      var row = new SegmentRow(diff.SegmentIds[i], s, diff.NodeIdForFid(s.FromNodeFid), diff.NodeIdForFid(s.ToNodeFid));
      if (diff.SegmentIsNew[i]) { plan.SegmentInserts.Add(row); continue; }

      var e = segsById[row.Id];
      bool[] changed = new[]
      {
        e.OsmId != s.OsmId,
        !string.Equals(e.Highway, s.Highway, StringComparison.Ordinal),
        Geodesy.SphereDistance(e.X1, e.Y1, s.X1, s.Y1) > GeometryToleranceMeters || Geodesy.SphereDistance(e.X2, e.Y2, s.X2, s.Y2) > GeometryToleranceMeters,
        e.FromNodeId != row.FromId,
        e.ToNodeId != row.ToId,
        m.Name && !string.Equals(e.Name, s.Name, StringComparison.Ordinal),
        m.IsOneWay && e.IsOneWay != s.IsOneWay,
        m.Lanes && e.Lanes != s.Lanes,
        m.MaxSpeed && e.MaxSpeed != s.MaxSpeed,
        m.Length && Math.Abs(e.LengthMeters - s.LengthMeters) > NumberTolerance,
        m.Cost && Math.Abs(e.TravelCost - s.TravelCost) > NumberTolerance,
      };

      var any = false;
      for (var k = 0; k < changed.Length; k++)
        if (changed[k]) { counters[k]++; any = true; }
      if (any) plan.SegmentUpdates.Add(row with { GeometryChanged = changed[2] });
    }

    for (var k = 0; k < names.Length; k++) plan.ColumnChanges[$"segmentos.{names[k]}"] = counters[k];
    return plan;
  }

  /// <summary>
  /// Quita de los retirados lo que las dependencias obligan a conservar (FK + report) y los nodos que esos
  /// segmentos conservados siguen usando; ajusta valence para que cuente también los segmentos conservados.
  /// </summary>
  public static void ApplyRetention(SyncPlan plan, ExistingNetwork existing, IEnumerable<long> retainNodes, IEnumerable<long> retainSegments, bool valenceMapped)
  {
    var segsById = existing.Segments.ToDictionary(s => s.Id);
    var nodesById = existing.Nodes.ToDictionary(n => n.Id);

    var keptSegs = retainSegments.Where(plan.RetiredSegmentIds.Contains).ToList();
    foreach (var id in keptSegs) plan.RetiredSegmentIds.Remove(id);
    plan.RetainedSegments = keptSegs.Count;

    var keptNodes = new HashSet<long>(retainNodes.Where(plan.RetiredNodeIds.Contains));
    var extraValence = new Dictionary<long, int>();
    foreach (var id in keptSegs)
    {
      var s = segsById[id];
      foreach (var n in new[] { s.FromNodeId, s.ToNodeId })
      {
        if (n is not { } nid) continue;
        extraValence[nid] = extraValence.GetValueOrDefault(nid) + 1;
        if (plan.RetiredNodeIds.Contains(nid)) keptNodes.Add(nid);
      }
    }

    foreach (var id in keptNodes) plan.RetiredNodeIds.Remove(id);
    plan.RetainedNodes = keptNodes.Count;
    if (!valenceMapped) return;

    // valence final = la del gpkg (segmentos de la red nueva) + segmentos conservados que lo usan
    var byId = plan.NodeInserts.Concat(plan.NodeUpdates).ToDictionary(r => r.Id);
    foreach (var id in keptNodes)
      if (!extraValence.ContainsKey(id)) extraValence[id] = 0;

    foreach (var (id, extra) in extraValence)
    {
      if (byId.TryGetValue(id, out var row))
      {
        var updated = row with { Valence = (keptNodes.Contains(id) ? 0 : row.Valence) + extra };
        Replace(plan.NodeInserts, row, updated);
        Replace(plan.NodeUpdates, row, updated);
      }
      else if (nodesById.TryGetValue(id, out var e))
      {
        // nodo conservado (retirado retenido o sin cambios): solo cambia valence
        plan.NodeUpdates.Add(new NodeRow(id, e.Lon, e.Lat, extra, LocationChanged: false));
      }
    }
  }

  private static void Replace(List<NodeRow> list, NodeRow old, NodeRow updated)
  {
    var i = list.IndexOf(old);
    if (i >= 0) list[i] = updated;
  }
}

/// <summary>Entrada de una dependencia para el planificador: filas que referencian cada Id.</summary>
public sealed class DependencyInput
{
  public string Source { get; init; } = default!;      // "fk" | "declarada"
  public string Table { get; init; } = default!;
  public string Column { get; init; } = default!;
  public string References { get; init; } = default!;  // "nodes" | "segments"
  public DependencyStrategy Strategy { get; init; } = default!;
  public bool IsNullable { get; init; }

  /// <summary>Id referenciado → cantidad de filas que lo referencian (solo valores numéricos).</summary>
  public Dictionary<long, long> RowsByReferencedId { get; init; } = default!;
}

public sealed class DependencyDecision
{
  public DependencyOutcome Outcome { get; init; } = default!;

  /// <summary>Id retirado → Id nuevo (o null = poner NULL). Lo que no está en el mapa no se toca.</summary>
  public Dictionary<long, long?> Map { get; } = new();
  public HashSet<long> Retain { get; } = new();
}

/// <summary>Elige reemplazos para referencias a elementos retirados (más cercano en la red final), en memoria.</summary>
public sealed class DependencyPlanner
{
  private readonly STRtree<(long Id, double Lon, double Lat)> _nodes = new();
  private readonly STRtree<(long Id, long OsmId, double X1, double Y1, double X2, double Y2)> _segments = new();
  private readonly Dictionary<long, ExistingNode> _existingNodes;
  private readonly Dictionary<long, ExistingSegment> _existingSegments;
  private readonly SyncPlan _plan;

  public DependencyPlanner(NetworkDiff diff, ExistingNetwork existing, SyncPlan plan)
  {
    _plan = plan;
    _existingNodes = existing.Nodes.ToDictionary(n => n.Id);
    _existingSegments = existing.Segments.ToDictionary(s => s.Id);

    for (var i = 0; i < diff.Nodes.Count; i++)
    {
      var n = diff.Nodes[i];
      _nodes.Insert(new Envelope(n.Lon, n.Lon, n.Lat, n.Lat), (diff.NodeIds[i], n.Lon, n.Lat));
    }
    for (var i = 0; i < diff.Segments.Count; i++)
    {
      var s = diff.Segments[i];
      _segments.Insert(new Envelope(s.X1, s.X2, s.Y1, s.Y2), (diff.SegmentIds[i], s.OsmId, s.X1, s.Y1, s.X2, s.Y2));
    }
    _nodes.Build();
    _segments.Build();
  }

  public DependencyDecision Decide(DependencyInput dep)
  {
    var st = dep.Strategy;
    var outcome = new DependencyOutcome
    {
      Source = dep.Source, Table = dep.Table, Column = dep.Column, References = dep.References,
      Strategy = st.OnRetired, Fallback = st.Fallback, MaxDistanceMeters = st.MaxDistanceMeters,
    };
    var decision = new DependencyDecision { Outcome = outcome };
    var retired = dep.References == "nodes" ? _plan.RetiredNodeIds : _plan.RetiredSegmentIds;
    var label = $"{dep.Table}.{dep.Column} → {dep.References}";

    var affected = dep.RowsByReferencedId.Where(kv => retired.Contains(kv.Key)).ToList();
    outcome.Affected = affected.Sum(kv => kv.Value);
    if (outcome.Affected == 0) return decision;

    if (st.OnRetired == RetiredStrategy.Block)
      throw new DependentDataException($"{label}: {outcome.Affected:N0} filas apuntan a elementos retirados y la estrategia es 'block'. No se modificó nada.");

    var unresolved = new List<KeyValuePair<long, long>>();
    foreach (var kv in affected)
    {
      if (st.OnRetired == RetiredStrategy.RemapNearest && Nearest(dep.References, kv.Key, st.MaxDistanceMeters) is { } replacement)
      {
        decision.Map[kv.Key] = replacement;
        outcome.Remapped += kv.Value;
      }
      else unresolved.Add(kv);
    }

    if (unresolved.Count == 0) return decision;

    var strategy = st.OnRetired == RetiredStrategy.RemapNearest ? st.Fallback : st.OnRetired;
    outcome.SampleUnresolved = unresolved.Take(20).Select(kv => kv.Key.ToString()).ToList();
    var pending = unresolved.Sum(kv => kv.Value);

    switch (strategy)
    {
      case RetiredStrategy.Block:
        throw new DependentDataException($"{label}: {pending:N0} filas sin reasignar y el fallback es 'block'. Ids: {string.Join(", ", outcome.SampleUnresolved)}. No se modificó nada.");

      case RetiredStrategy.SetNull:
        if (!dep.IsNullable)
          throw new DependentDataException($"{label}: la columna no admite NULL; cambia la estrategia (report/block) en el perfil. No se modificó nada.");
        foreach (var kv in unresolved) decision.Map[kv.Key] = null;
        outcome.SetNull = pending;
        break;

      case RetiredStrategy.Report:
        if (dep.Source == "fk")
        {
          foreach (var kv in unresolved) decision.Retain.Add(kv.Key);
          outcome.Retained = pending;
        }
        else outcome.Dangling = pending;
        break;
    }

    return decision;
  }

  private long? Nearest(string references, long retiredId, double maxMeters)
  {
    if (references == "nodes")
    {
      if (!_existingNodes.TryGetValue(retiredId, out var old)) return null;
      var env = Around(old.Lon, old.Lat, maxMeters);
      long? best = null;
      var bestD = double.MaxValue;
      foreach (var c in _nodes.Query(env))
      {
        var d = Geodesy.SphereDistance(old.Lon, old.Lat, c.Lon, c.Lat);
        if (d <= maxMeters && d < bestD) { bestD = d; best = c.Id; }
      }
      return best;
    }
    else
    {
      if (!_existingSegments.TryGetValue(retiredId, out var old)) return null;
      var mx = (old.X1 + old.X2) / 2;
      var my = (old.Y1 + old.Y2) / 2;
      var env = Around(mx, my, maxMeters);
      long? best = null;
      (bool OtherWay, double D) bestKey = (true, double.MaxValue);
      foreach (var c in _segments.Query(env))
      {
        var d = PointToSegmentMeters(mx, my, c.X1, c.Y1, c.X2, c.Y2);
        if (d > maxMeters) continue;
        var key = (c.OsmId != old.OsmId, d); // se prefiere la misma vía OSM
        if (key.CompareTo(bestKey) < 0) { bestKey = key; best = c.Id; }
      }
      return best;
    }
  }

  private static Envelope Around(double lon, double lat, double meters)
  {
    var dLat = meters / 111_320.0;
    var dLon = meters / (111_320.0 * Math.Max(0.01, Math.Cos(lat * Math.PI / 180)));
    return new Envelope(lon - dLon, lon + dLon, lat - dLat, lat + dLat);
  }

  /// <summary>Distancia punto–segmento en metros (proyección equirectangular local; exacta a esta escala).</summary>
  private static double PointToSegmentMeters(double px, double py, double x1, double y1, double x2, double y2)
  {
    var k = Math.Cos(py * Math.PI / 180) * 111_320.0;
    const double kLat = 110_574.0;
    double ax = (x1 - px) * k, ay = (y1 - py) * kLat, bx = (x2 - px) * k, by = (y2 - py) * kLat;
    double dx = bx - ax, dy = by - ay;
    var len2 = dx * dx + dy * dy;
    var t = len2 == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / len2, 0, 1);
    double cx = ax + t * dx, cy = ay + t * dy;
    return Math.Sqrt(cx * cx + cy * cy);
  }
}
