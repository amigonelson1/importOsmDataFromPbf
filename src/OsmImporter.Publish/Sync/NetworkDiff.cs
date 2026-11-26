using OsmImporter.Core.Geo;
using OsmImporter.Core.Output;

namespace OsmImporter.Publish.Sync;

/// <summary>
/// Diff entre la red nueva (GeoPackage) y la existente en el destino, por llave natural:
///   nodo     = coordenada snapeada a la grilla
///   segmento = OsmId + llave de nodo origen + llave de nodo destino
/// Primero empareja exacto; lo que queda se empareja con tolerancia de ±1 celda en cada extremo
/// (diferencias de redondeo, p. ej. data que pasó por EPSG:3857 en osm2pgsql).
/// No depende del motor de base de datos.
/// </summary>
public sealed class NetworkDiff
{
  public List<GpkgNode> Nodes { get; init; } = default!;
  public List<GpkgSegment> Segments { get; init; } = default!;

  /// <summary>Id final de cada nodo del gpkg (mismo índice que Nodes). Existente si se emparejó, 0 si es nuevo hasta AssignNewIds.</summary>
  public long[] NodeIds { get; init; } = Array.Empty<long>();
  public bool[] NodeIsNew { get; init; } = Array.Empty<bool>();

  public long[] SegmentIds { get; init; } = Array.Empty<long>();
  public bool[] SegmentIsNew { get; init; } = Array.Empty<bool>();

  public List<long> RetiredNodeIds { get; init; } = new();
  public List<long> RetiredSegmentIds { get; init; } = new();

  public long NodesKeptExact { get; init; }
  public long NodesKeptTolerant { get; init; }
  public long SegmentsKeptExact { get; init; }
  public long SegmentsKeptTolerant { get; init; }

  public int NewNodeCount => NodeIsNew.Count(x => x);
  public int NewSegmentCount => SegmentIsNew.Count(x => x);

  /// <summary>Id final del nodo con ese fid del gpkg (fids 1..n consecutivos).</summary>
  public long NodeIdForFid(long fid) => NodeIds[fid - 1];

  public void AssignNewIds(IReadOnlyList<long> newNodeIds, IReadOnlyList<long> newSegmentIds)
  {
    Assign(NodeIds, NodeIsNew, newNodeIds, "nodos");
    Assign(SegmentIds, SegmentIsNew, newSegmentIds, "segmentos");
  }

  private static void Assign(long[] ids, bool[] isNew, IReadOnlyList<long> fresh, string what)
  {
    var k = 0;
    for (var i = 0; i < ids.Length; i++)
      if (isNew[i]) ids[i] = k < fresh.Count ? fresh[k++] : throw new InvalidOperationException($"Faltan Ids nuevos para {what}");
    if (k != fresh.Count) throw new InvalidOperationException($"Sobran Ids nuevos para {what}");
  }

  public static NetworkDiff Compute(GeoPackageReader gpkg, ExistingNetwork existing)
  {
    var snap = gpkg.SnapDegrees;

    // ------------------------------ nodos
    var nodes = gpkg.Nodes().ToList();
    for (var i = 0; i < nodes.Count; i++)
      if (nodes[i].Fid != i + 1) throw new InvalidDataException("El gpkg debe tener fids de nodo consecutivos desde 1.");

    var existingNodesByKey = new Dictionary<long, Queue<long>>(existing.Nodes.Count);
    foreach (var n in existing.Nodes.OrderBy(n => n.Id))
    {
      var key = GridKey.KeyFor(n.Lon, n.Lat, snap);
      if (!existingNodesByKey.TryGetValue(key, out var q)) existingNodesByKey[key] = q = new Queue<long>(1);
      q.Enqueue(n.Id);
    }

    var nodeIds = new long[nodes.Count];
    var nodeIsNew = new bool[nodes.Count];
    long nodesExact = 0, nodesTolerant = 0;

    for (var i = 0; i < nodes.Count; i++)
      if (existingNodesByKey.TryGetValue(nodes[i].Key, out var q) && q.Count > 0) { nodeIds[i] = q.Dequeue(); nodesExact++; }

    for (var i = 0; i < nodes.Count; i++)
    {
      if (nodeIds[i] != 0) continue;
      var (ix, iy) = GridKey.Unpack(nodes[i].Key);
      foreach (var nk in Neighbors(ix, iy))
      {
        if (existingNodesByKey.TryGetValue(nk, out var q) && q.Count > 0) { nodeIds[i] = q.Dequeue(); nodesTolerant++; break; }
      }
      if (nodeIds[i] == 0) nodeIsNew[i] = true;
    }

    var retiredNodes = existingNodesByKey.Values.SelectMany(q => q).Order().ToList();

    // ------------------------------ segmentos
    var segments = gpkg.Segments().ToList();
    var existingSegsByKey = new Dictionary<(long, long, long), Queue<long>>(existing.Segments.Count);
    foreach (var s in existing.Segments.OrderBy(s => s.Id))
    {
      var key = (s.OsmId, GridKey.KeyFor(s.X1, s.Y1, snap), GridKey.KeyFor(s.X2, s.Y2, snap));
      if (!existingSegsByKey.TryGetValue(key, out var q)) existingSegsByKey[key] = q = new Queue<long>(1);
      q.Enqueue(s.Id);
    }

    var segIds = new long[segments.Count];
    var segIsNew = new bool[segments.Count];
    long segsExact = 0, segsTolerant = 0;

    for (var i = 0; i < segments.Count; i++)
    {
      var s = segments[i];
      if (existingSegsByKey.TryGetValue((s.OsmId, s.FromNodeKey, s.ToNodeKey), out var q) && q.Count > 0) { segIds[i] = q.Dequeue(); segsExact++; }
    }

    // Tolerancia: candidatos restantes agrupados por OsmId
    var remainingByOsm = new Dictionary<long, List<(long From, long To, long Id)>>();
    foreach (var ((osm, from, to), q) in existingSegsByKey)
    {
      if (q.Count == 0) continue;
      if (!remainingByOsm.TryGetValue(osm, out var list)) remainingByOsm[osm] = list = new();
      foreach (var id in q) list.Add((from, to, id));
    }

    for (var i = 0; i < segments.Count; i++)
    {
      if (segIds[i] != 0) continue;
      var s = segments[i];
      if (remainingByOsm.TryGetValue(s.OsmId, out var list))
      {
        var idx = list.FindIndex(c => Near(c.From, s.FromNodeKey) && Near(c.To, s.ToNodeKey));
        if (idx >= 0)
        {
          segIds[i] = list[idx].Id;
          list.RemoveAt(idx);
          segsTolerant++;
          continue;
        }
      }
      segIsNew[i] = true;
    }

    var retiredSegs = remainingByOsm.Values.SelectMany(l => l.Select(c => c.Id)).Order().ToList();

    return new NetworkDiff
    {
      Nodes = nodes,
      Segments = segments,
      NodeIds = nodeIds,
      NodeIsNew = nodeIsNew,
      SegmentIds = segIds,
      SegmentIsNew = segIsNew,
      RetiredNodeIds = retiredNodes,
      RetiredSegmentIds = retiredSegs,
      NodesKeptExact = nodesExact,
      NodesKeptTolerant = nodesTolerant,
      SegmentsKeptExact = segsExact,
      SegmentsKeptTolerant = segsTolerant,
    };
  }

  private static IEnumerable<long> Neighbors(long ix, long iy)
  {
    for (var dx = -1; dx <= 1; dx++)
      for (var dy = -1; dy <= 1; dy++)
        if (dx != 0 || dy != 0) yield return GridKey.Pack(ix + dx, iy + dy);
  }

  private static bool Near(long a, long b)
  {
    var (ax, ay) = GridKey.Unpack(a);
    var (bx, by) = GridKey.Unpack(b);
    return Math.Abs(ax - bx) <= 1 && Math.Abs(ay - by) <= 1;
  }
}
