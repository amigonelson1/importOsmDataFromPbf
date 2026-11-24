using OsmImporter.Core.Geo;
using OsmImporter.Core.Options;
using OsmImporter.Core.Osm;

namespace OsmImporter.Core.Graph;

public sealed class GraphBuildStats
{
  public long WaysProcessed { get; set; }
  public long WaysReversed { get; set; }
  public long DensifiedPoints { get; set; }
  public long CandidateSegments { get; set; }
  public long DroppedShort { get; set; }
  public long DroppedLoops { get; set; }
  public long DuplicateSegmentKeys { get; set; }
}

/// <summary>
/// Construye el grafo replicando updateDataOSMreal.txt:
/// segmentize → micro-segmentos → filtro de longitud → nodos snapeados → lazos fuera → valence → costos.
/// </summary>
public static class GraphBuilder
{
  public static (RoadGraph Graph, GraphBuildStats Stats) Build(
    IEnumerable<SelectedWay> ways,
    BuildOptions options,
    IReadOnlyList<RestrictionRecord>? restrictions = null,
    PbfReadResult? read = null)
  {
    var graph = new RoadGraph();
    var stats = new GraphBuildStats();
    var nodes = new Dictionary<long, RoadNode>();
    var seenKeys = new Dictionary<(long, long, long), int>();
    var source = new List<(double Lon, double Lat)>(256);
    var dense = new List<(double Lon, double Lat)>(1024);
    var snap = options.SnapDegrees;

    RoadNode NodeFor(long key)
    {
      if (nodes.TryGetValue(key, out var n)) return n;
      var (lon, lat) = GridKey.Location(key, snap);
      n = new RoadNode { Fid = graph.Nodes.Count + 1, Key = key, Lon = lon, Lat = lat };
      nodes[key] = n;
      graph.Nodes.Add(n);
      return n;
    }

    var waysList = ways as IList<SelectedWay> ?? ways.ToList();
    foreach (var sel in waysList)
    {
      var w = sel.Way;
      stats.WaysProcessed++;

      source.Clear();
      source.AddRange(sel.Coords);

      var isOneWay = TagParsing.IsOneWayForward(w.Oneway);
      if (options.ReverseOnewayMinusOne && TagParsing.IsOneWayReverse(w.Oneway))
      {
        source.Reverse();
        isOneWay = true;
        stats.WaysReversed++;
      }

      var info = new RoadWayInfo
      {
        OsmId = w.Id,
        Name = w.Name,
        Highway = w.Highway,
        IsOneWay = isOneWay,
        Lanes = TagParsing.ParseLanes(w.Lanes),
        MaxSpeed = TagParsing.ParseMaxSpeed(w.MaxSpeed),
        Surface = w.Surface,
        Smoothness = w.Smoothness,
        TrackType = w.TrackType,
        Bridge = w.Bridge,
        Tunnel = w.Tunnel,
        Layer = w.Layer,
        MaxWeight = w.MaxWeight,
        MaxHeight = w.MaxHeight,
        MaxWidth = w.MaxWidth,
      };
      var costFactor = options.CostFactorFor(w.Highway);

      Segmentizer.Segmentize(source, options.SegmentizeMeters, options.SegmentizeMethod, dense);
      stats.DensifiedPoints += dense.Count;

      for (var i = 1; i < dense.Count; i++)
      {
        stats.CandidateSegments++;
        var (lon1, lat1) = dense[i - 1];
        var (lon2, lat2) = dense[i];

        var length = Geodesy.SpheroidDistance(lon1, lat1, lon2, lat2);
        if (length <= options.MinSegmentMeters) { stats.DroppedShort++; continue; }

        var fromKey = GridKey.KeyFor(lon1, lat1, snap);
        var toKey = GridKey.KeyFor(lon2, lat2, snap);
        if (fromKey == toKey) { stats.DroppedLoops++; continue; }

        var id = (w.Id, fromKey, toKey);
        var ordinal = seenKeys.TryGetValue(id, out var count) ? count + 1 : 1;
        seenKeys[id] = ordinal;
        if (ordinal > 1) stats.DuplicateSegmentKeys++;

        var from = NodeFor(fromKey);
        var to = NodeFor(toKey);
        from.Valence++;
        to.Valence++;

        graph.Segments.Add(new RoadSegment
        {
          Fid = graph.Segments.Count + 1,
          Way = info,
          Ordinal = ordinal,
          X1 = lon1, Y1 = lat1, X2 = lon2, Y2 = lat2,
          FromKey = fromKey,
          ToKey = toKey,
          FromNode = from,
          ToNode = to,
          LengthMeters = length,
          TravelCost = length * costFactor,
        });
      }
    }

    if (restrictions is { Count: > 0 })
    {
      var selectedWayIds = waysList.Select(w => w.Way.Id).ToHashSet();
      long rFid = 1;
      foreach (var r in restrictions)
      {
        if (!selectedWayIds.Contains(r.FromWayId) || !selectedWayIds.Contains(r.ToWayId)) continue;
        long viaKey = 0;
        if (r.ViaNodeId > 0 && read != null && read.TryGetCoordinate(r.ViaNodeId, out var vLon, out var vLat))
          viaKey = GridKey.KeyFor(vLon, vLat, snap);

        graph.Restrictions.Add(new TurnRestriction
        {
          Fid = rFid++,
          RelationId = r.Id,
          Restriction = r.Restriction,
          FromOsmId = r.FromWayId,
          ViaNodeKey = viaKey,
          ToOsmId = r.ToWayId,
        });
      }
    }

    return (graph, stats);
  }
}
