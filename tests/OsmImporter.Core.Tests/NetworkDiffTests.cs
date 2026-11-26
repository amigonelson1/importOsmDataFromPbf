using OsmImporter.Core.Graph;
using OsmImporter.Core.Options;
using OsmImporter.Core.Osm;
using OsmImporter.Core.Output;
using OsmImporter.Publish;
using OsmImporter.Publish.Sync;

namespace OsmImporter.Core.Tests;

public class NetworkDiffTests : IDisposable
{
  private readonly List<string> _files = [];

  private static SelectedWay Way(long id, params (double, double)[] coords) => new()
  {
    Way = new WayRecord { Id = id, Highway = "residential", Refs = new long[coords.Length] },
    Coords = coords,
  };

  private GeoPackageReader Gpkg(params SelectedWay[] ways)
  {
    var (graph, _) = GraphBuilder.Build(ways, new BuildOptions());
    var path = Path.Combine(Path.GetTempPath(), $"osmimport-diff-{Guid.NewGuid():N}.gpkg");
    _files.Add(path);
    GeoPackageWriter.Write(path, graph, new Dictionary<string, string>
    {
      ["format"] = "osm-road-graph",
      ["snap_degrees"] = "0.00001",
    });
    return new GeoPackageReader(path);
  }

  /// <summary>Red "existente" = misma red del gpkg con otros Ids (como si viniera de la BD).</summary>
  private static ExistingNetwork AsExisting(GeoPackageReader g, long idOffset, double shiftLonDeg = 0)
  {
    var nodes = g.Nodes().Select(n => new ExistingNode(n.Fid + idOffset, n.Lon + shiftLonDeg, n.Lat)).ToList();
    var segs = g.Segments().Select(s => new ExistingSegment(s.Fid + idOffset, s.OsmId, s.Highway,
      s.X1 + shiftLonDeg, s.Y1, s.X2 + shiftLonDeg, s.Y2, s.LengthMeters)).ToList();
    return new ExistingNetwork { Nodes = nodes, Segments = segs };
  }

  private static readonly SelectedWay A = Way(1, (-68.0, -38.9), (-68.0, -38.9004));
  private static readonly SelectedWay B = Way(2, (-68.0, -38.9004), (-68.0005, -38.9004));
  private static readonly SelectedWay C = Way(3, (-68.01, -38.9), (-68.01, -38.9003));

  [Fact]
  public void Identical_network_keeps_every_id()
  {
    using var g = Gpkg(A, B);
    var diff = NetworkDiff.Compute(g, AsExisting(g, idOffset: 1000));

    Assert.Equal(0, diff.NewNodeCount);
    Assert.Equal(0, diff.NewSegmentCount);
    Assert.Empty(diff.RetiredNodeIds);
    Assert.Empty(diff.RetiredSegmentIds);
    Assert.All(diff.SegmentIds.Zip(diff.Segments), p => Assert.Equal(p.Second.Fid + 1000, p.First));
    Assert.All(diff.NodeIds.Zip(diff.Nodes), p => Assert.Equal(p.Second.Fid + 1000, p.First));
  }

  [Fact]
  public void Added_and_removed_ways_become_new_and_retired()
  {
    using var before = Gpkg(A, B);
    using var after = Gpkg(A, C);
    var existing = AsExisting(before, idOffset: 1000);
    var diff = NetworkDiff.Compute(after, existing);

    var bSegments = before.Segments().Where(s => s.OsmId == 2).Select(s => s.Fid + 1000).Order().ToList();
    var cSegments = after.Segments().Count(s => s.OsmId == 3);

    Assert.Equal(bSegments, diff.RetiredSegmentIds);
    Assert.Equal(cSegments, diff.NewSegmentCount);
    Assert.All(diff.Segments.Select((s, i) => (s, i)).Where(x => x.s.OsmId == 1), x => Assert.False(diff.SegmentIsNew[x.i]));

    diff.AssignNewIds(Enumerable.Range(5000, diff.NewNodeCount).Select(i => (long)i).ToList(),
                      Enumerable.Range(9000, diff.NewSegmentCount).Select(i => (long)i).ToList());
    Assert.All(diff.SegmentIds, id => Assert.NotEqual(0, id));
    Assert.Equal(diff.SegmentIds.Length, diff.SegmentIds.Distinct().Count());
  }

  [Fact]
  public void One_cell_rounding_difference_is_tolerated()
  {
    using var g = Gpkg(A);
    var existing = AsExisting(g, idOffset: 1000, shiftLonDeg: 0.000009); // ~0,8 m: cae en la celda vecina
    var diff = NetworkDiff.Compute(g, existing);

    Assert.Equal(0, diff.NewSegmentCount);
    Assert.Empty(diff.RetiredSegmentIds);
    Assert.Equal(diff.Segments.Count, diff.SegmentsKeptTolerant);
    Assert.Equal(diff.Nodes.Count, diff.NodesKeptTolerant);
  }

  [Fact]
  public void Shift_beyond_tolerance_is_not_matched()
  {
    using var g = Gpkg(A);
    var existing = AsExisting(g, idOffset: 1000, shiftLonDeg: 0.00005); // ~4 m
    var diff = NetworkDiff.Compute(g, existing);

    Assert.Equal(diff.Segments.Count, diff.NewSegmentCount);
    Assert.Equal(existing.Segments.Count, diff.RetiredSegmentIds.Count);
  }

  public void Dispose()
  {
    foreach (var f in _files) try { File.Delete(f); } catch { /* ignorar */ }
  }
}
