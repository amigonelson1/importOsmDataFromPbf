using Microsoft.Data.Sqlite;
using OsmImporter.Core.Geo;
using OsmImporter.Core.Graph;
using OsmImporter.Core.Options;
using OsmImporter.Core.Osm;
using OsmImporter.Core.Output;

namespace OsmImporter.Core.Tests;

public class GraphTests
{
  private static SelectedWay Way(long id, string highway, string? oneway, params (double, double)[] coords) => new()
  {
    Way = new WayRecord { Id = id, Highway = highway, Oneway = oneway, Lanes = "2", MaxSpeed = "60", Refs = new long[coords.Length] },
    Coords = coords,
  };

  [Fact]
  public void Crossing_ways_share_the_junction_node()
  {
    // Dos vías en "T" que comparten el vértice (-68.0, -38.9)
    var a = Way(1, "primary", null, (-68.001, -38.9), (-68.0, -38.9), (-67.999, -38.9));
    var b = Way(2, "residential", null, (-68.0, -38.9), (-68.0, -38.899));

    var (graph, _) = GraphBuilder.Build(new[] { a, b }, new BuildOptions());
    var junction = graph.Nodes.Single(n => n.Key == GridKey.KeyFor(-68.0, -38.9, 0.00001));

    Assert.Equal(3, junction.Valence);
    Assert.Equal(1, GraphAnalysis.Analyze(graph).ConnectedComponents);
  }

  [Fact]
  public void Segments_are_at_most_50m_and_costs_use_factors()
  {
    var w = Way(10, "motorway", "yes", (-68.0, -38.9), (-68.0, -38.903)); // ~333 m
    var (graph, _) = GraphBuilder.Build(new[] { w }, new BuildOptions());

    Assert.Equal(8, graph.Segments.Count); // bisect: 333 m → 2^3 partes
    Assert.All(graph.Segments, s =>
    {
      Assert.True(s.LengthMeters <= 50.2);
      Assert.Equal(s.LengthMeters * 0.7, s.TravelCost, 1e-9);
      Assert.True(s.Way.IsOneWay);
      Assert.Equal(2, s.Way.Lanes);
      Assert.Equal(60, s.Way.MaxSpeed);
    });
    Assert.Equal(graph.Segments.Count + 1, graph.Nodes.Count);
  }

  [Fact]
  public void Oneway_minus_one_is_reversed()
  {
    var w = Way(5, "secondary", "-1", (-68.0, -38.9), (-68.0, -38.9003));
    var (graph, stats) = GraphBuilder.Build(new[] { w }, new BuildOptions());

    var s = Assert.Single(graph.Segments);
    Assert.True(s.Way.IsOneWay);
    Assert.Equal(-38.9003, s.Y1, 1e-9);
    Assert.Equal(1, stats.WaysReversed);

    var (legacy, _) = GraphBuilder.Build(new[] { w }, new BuildOptions { ReverseOnewayMinusOne = false });
    Assert.False(legacy.Segments[0].Way.IsOneWay);
    Assert.Equal(-38.9, legacy.Segments[0].Y1, 1e-9);
  }

  [Fact]
  public void Short_segments_and_loops_are_dropped_without_orphans()
  {
    // 0,5 m: se descarta por corto. Ningún nodo debe quedar huérfano.
    var w = Way(7, "track", null, (-68.0, -38.9), (-68.0, -38.9000045), (-68.0, -38.9005));
    var (graph, stats) = GraphBuilder.Build(new[] { w }, new BuildOptions());

    Assert.Equal(1, stats.DroppedShort);
    Assert.All(graph.Nodes, n => Assert.True(n.Valence > 0));
    Assert.All(graph.Segments, s => Assert.NotSame(s.FromNode, s.ToNode));
  }

  [Fact]
  public void Segment_keys_are_unique_and_deterministic()
  {
    var w = Way(9, "service", null, (-68.0, -38.9), (-68.0, -38.9004), (-68.0, -38.9)); // ida y vuelta
    var (g1, s1) = GraphBuilder.Build(new[] { w }, new BuildOptions());
    var (g2, _) = GraphBuilder.Build(new[] { w }, new BuildOptions());

    Assert.Equal(g1.Segments.Select(s => s.Key), g2.Segments.Select(s => s.Key));
    Assert.Equal(g1.Segments.Count, g1.Segments.Select(s => s.Key).Distinct().Count());
    Assert.Equal(0, s1.DuplicateSegmentKeys); // ida y vuelta tienen From/To invertidos → llaves distintas
  }

  [Theory]
  [InlineData("60", 60)]
  [InlineData("60 mph", 97)]
  [InlineData("80;60", 80)]
  [InlineData("AR:urban", null)]
  [InlineData("none", null)]
  public void MaxSpeed_parsing(string raw, int? expected) => Assert.Equal(expected, TagParsing.ParseMaxSpeed(raw));

  [Fact]
  public void GeoPackage_roundtrip()
  {
    var w = Way(11, "primary", null, (-68.0, -38.9), (-68.0, -38.9003));
    var (graph, _) = GraphBuilder.Build(new[] { w }, new BuildOptions());
    var path = Path.Combine(Path.GetTempPath(), $"osmimport-test-{Guid.NewGuid():N}.gpkg");

    try
    {
      GeoPackageWriter.Write(path, graph, new Dictionary<string, string> { ["format"] = "test" });

      using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
      conn.Open();
      using var cmd = conn.CreateCommand();

      cmd.CommandText = "PRAGMA application_id";
      Assert.Equal(1196444487L, (long)cmd.ExecuteScalar()!);

      cmd.CommandText = "SELECT count(*) FROM road_segments";
      Assert.Equal((long)graph.Segments.Count, (long)cmd.ExecuteScalar()!);

      cmd.CommandText = "SELECT count(*) FROM rtree_road_segments_geom";
      Assert.Equal((long)graph.Segments.Count, (long)cmd.ExecuteScalar()!);

      cmd.CommandText = "SELECT geom FROM road_segments WHERE fid = 1";
      var coords = GpkgGeometry.ReadCoordinates((byte[])cmd.ExecuteScalar()!);
      Assert.Equal(graph.Segments[0].X1, coords[0]);
      Assert.Equal(graph.Segments[0].Y2, coords[3]);

      cmd.CommandText = "SELECT segment_key FROM road_segments WHERE fid = 1";
      Assert.Equal(graph.Segments[0].Key, (string)cmd.ExecuteScalar()!);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public void GeoPackage_cleans_up_tmp_file_on_cancellation()
  {
    var w = Way(12, "primary", null, (-68.0, -38.9), (-68.0, -38.9003));
    var (graph, _) = GraphBuilder.Build(new[] { w }, new BuildOptions());
    var path = Path.Combine(Path.GetTempPath(), $"osmimport-test-cancel-{Guid.NewGuid():N}.gpkg");
    var temp = path + ".tmp";

    using var cts = new CancellationTokenSource();
    cts.Cancel(); // cancel immediately

    Assert.Throws<OperationCanceledException>(() =>
      GeoPackageWriter.Write(path, graph, new Dictionary<string, string> { ["format"] = "test" }, cts.Token));

    Assert.False(File.Exists(path), "El archivo final .gpkg no debió crearse.");
    Assert.False(File.Exists(temp), "El archivo temporal .tmp debió eliminarse al cancelar.");
  }

  [Fact]
  public void WaySelector_is_parallel_and_strictly_deterministic()
  {
    var bbox = new BoundingBox(-69.0, -39.0, -67.0, -33.0);
    var options = new BuildOptions { BBox = bbox };
    var area = new OperationalArea(options);

    var nodeIds = new List<long>();
    var lonE7 = new List<int>();
    var latE7 = new List<int>();
    var ways = new List<WayRecord>();

    long nodeIdCounter = 1;
    for (var w = 1; w <= 300; w++)
    {
      var n1 = nodeIdCounter++;
      var n2 = nodeIdCounter++;
      var n3 = nodeIdCounter++;

      nodeIds.AddRange(new[] { n1, n2, n3 });
      var baseLon = -68.5 + (w % 50) * 0.01;
      var baseLat = -38.5 + (w % 50) * 0.01;

      lonE7.AddRange(new[] { (int)(baseLon * 1e7), (int)((baseLon + 0.005) * 1e7), (int)((baseLon + 0.01) * 1e7) });
      latE7.AddRange(new[] { (int)(baseLat * 1e7), (int)((baseLat + 0.005) * 1e7), (int)((baseLat + 0.01) * 1e7) });

      ways.Add(new WayRecord
      {
        Id = (w * 17) % 300 + 1, // IDs desordenados a propósito
        Highway = (w % 2 == 0) ? "primary" : "secondary",
        Refs = new[] { n1, n2, n3 },
      });
    }

    var sortedNodeIds = nodeIds.ToArray();
    Array.Sort(sortedNodeIds);

    var read = new PbfReadResult
    {
      Ways = ways,
      NodeIds = sortedNodeIds,
      LonE7 = lonE7.ToArray(),
      LatE7 = latE7.ToArray(),
      TotalWaysScanned = ways.Count,
      TotalNodesScanned = nodeIds.Count,
      NodesFound = nodeIds.Count,
    };

    var res1 = WaySelector.Select(read, options, area);
    var res2 = WaySelector.Select(read, options, area);

    Assert.NotEmpty(res1.Ways);
    Assert.Equal(res1.Ways.Count, res2.Ways.Count);
    for (var i = 0; i < res1.Ways.Count; i++)
    {
      Assert.Equal(res1.Ways[i].Way.Id, res2.Ways[i].Way.Id);
      Assert.Equal(res1.Ways[i].PieceOrdinal, res2.Ways[i].PieceOrdinal);
      Assert.Equal(res1.Ways[i].Coords.Length, res2.Ways[i].Coords.Length);
    }

    Assert.Equal(res1.Coverage.Count, res2.Coverage.Count);
    for (var i = 0; i < res1.Coverage.Count; i++)
    {
      Assert.Equal(res1.Coverage[i].Highway, res2.Coverage[i].Highway);
      Assert.Equal(res1.Coverage[i].Ways, res2.Coverage[i].Ways);
      Assert.Equal(res1.Coverage[i].Km, res2.Coverage[i].Km, 1e-6);
    }
  }

  [Fact]
  public void GeoPackage_roundtrips_enriched_attributes_and_restrictions()
  {
    var w1 = new SelectedWay
    {
      Way = new WayRecord
      {
        Id = 42,
        Highway = "secondary",
        Surface = "asphalt",
        Smoothness = "good",
        TrackType = "grade1",
        Bridge = true,
        Tunnel = false,
        Layer = 1,
        MaxWeight = 15.5,
        MaxHeight = 4.2,
        MaxWidth = 2.8,
        Refs = new long[] { 1, 2 },
      },
      Coords = new[] { (-68.0, -38.9), (-68.0003, -38.9) },
    };

    var w2 = new SelectedWay
    {
      Way = new WayRecord
      {
        Id = 43,
        Highway = "residential",
        Refs = new long[] { 2, 3 },
      },
      Coords = new[] { (-68.0003, -38.9), (-68.0003, -38.899) },
    };

    var restrictions = new List<RestrictionRecord>
    {
      new() { Id = 1001, FromWayId = 42, ViaNodeId = 2, ToWayId = 43, Restriction = "no_left_turn" }
    };

    var (graph, _) = GraphBuilder.Build(new[] { w1, w2 }, new BuildOptions(), restrictions);
    Assert.Single(graph.Restrictions);
    Assert.Equal(1001, graph.Restrictions[0].RelationId);
    Assert.Equal(42, graph.Restrictions[0].FromOsmId);
    Assert.Equal(43, graph.Restrictions[0].ToOsmId);
    Assert.Equal("no_left_turn", graph.Restrictions[0].Restriction);

    var temp = Path.Combine(Path.GetTempPath(), $"test_gpkg_{Guid.NewGuid():N}.gpkg");
    try
    {
      var info = new Dictionary<string, string>
      {
        ["format"] = "osm-road-graph",
        ["snap_degrees"] = "0.00001",
      };
      GeoPackageWriter.Write(temp, graph, info);

      using var reader = new GeoPackageReader(temp);
      Assert.True(reader.HasRestrictions);

      var segs = reader.Segments().ToList();
      var seg42 = segs.Single(s => s.OsmId == 42);
      Assert.Equal("asphalt", seg42.Surface);
      Assert.Equal("good", seg42.Smoothness);
      Assert.Equal("grade1", seg42.TrackType);
      Assert.True(seg42.Bridge);
      Assert.False(seg42.Tunnel);
      Assert.Equal(1, seg42.Layer);
      Assert.Equal(15.5, seg42.MaxWeight);
      Assert.Equal(4.2, seg42.MaxHeight);
      Assert.Equal(2.8, seg42.MaxWidth);

      var res = Assert.Single(reader.Restrictions());
      Assert.Equal(1001, res.RelationId);
      Assert.Equal(42, res.FromOsmId);
      Assert.Equal(43, res.ToOsmId);
      Assert.Equal("no_left_turn", res.Restriction);
    }
    finally
    {
      if (File.Exists(temp)) File.Delete(temp);
    }
  }
}
