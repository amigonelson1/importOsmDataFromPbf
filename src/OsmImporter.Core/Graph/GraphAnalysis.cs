namespace OsmImporter.Core.Graph;

public sealed class GraphQuality
{
  public long Segments { get; init; }
  public long Nodes { get; init; }
  public double TotalKm { get; init; }

  /// <summary>Equivalentes a las queries de validación de updateDataOSMreal.txt (deben dar 0).</summary>
  public long SegmentsWithoutNodes { get; init; }
  public long SelfLoops { get; init; }
  public long OrphanNodes { get; init; }

  public Dictionary<string, long> ValenceHistogram { get; init; } = [];
  public long ConnectedComponents { get; init; }
  public double LargestComponentNodePercent { get; init; }
  public Dictionary<string, double> KmByHighway { get; init; } = [];
  public Dictionary<string, long> SegmentsByHighway { get; init; } = [];
}

public static class GraphAnalysis
{
  public static GraphQuality Analyze(RoadGraph graph)
  {
    var index = new Dictionary<RoadNode, int>(graph.Nodes.Count, ReferenceEqualityComparer.Instance);
    for (var i = 0; i < graph.Nodes.Count; i++) index[graph.Nodes[i]] = i;

    var parent = new int[graph.Nodes.Count];
    for (var i = 0; i < parent.Length; i++) parent[i] = i;

    int Find(int x)
    {
      while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
      return x;
    }

    long loops = 0;
    var kmBy = new Dictionary<string, double>();
    var segBy = new Dictionary<string, long>();
    double totalKm = 0;

    foreach (var s in graph.Segments)
    {
      if (s.FromNode == s.ToNode) loops++;
      var a = Find(index[s.FromNode]);
      var b = Find(index[s.ToNode]);
      if (a != b) parent[a] = b;

      var km = s.LengthMeters / 1000.0;
      totalKm += km;
      kmBy[s.Way.Highway] = kmBy.GetValueOrDefault(s.Way.Highway) + km;
      segBy[s.Way.Highway] = segBy.GetValueOrDefault(s.Way.Highway) + 1;
    }

    var componentSizes = new Dictionary<int, long>();
    for (var i = 0; i < parent.Length; i++)
    {
      var r = Find(i);
      componentSizes[r] = componentSizes.GetValueOrDefault(r) + 1;
    }

    var valence = graph.Nodes
      .GroupBy(n => n.Valence >= 5 ? "5+" : n.Valence.ToString())
      .OrderBy(g => g.Key)
      .ToDictionary(g => g.Key, g => (long)g.Count());

    return new GraphQuality
    {
      Segments = graph.Segments.Count,
      Nodes = graph.Nodes.Count,
      TotalKm = Math.Round(totalKm, 3),
      SegmentsWithoutNodes = 0, // por construcción todo segmento tiene From/To
      SelfLoops = loops,
      OrphanNodes = graph.Nodes.LongCount(n => n.Valence == 0),
      ValenceHistogram = valence,
      ConnectedComponents = componentSizes.Count,
      LargestComponentNodePercent = graph.Nodes.Count == 0 ? 0 : Math.Round(100.0 * componentSizes.Values.Max() / graph.Nodes.Count, 2),
      KmByHighway = kmBy.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => Math.Round(k.Value, 3)),
      SegmentsByHighway = segBy.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value),
    };
  }
}
