using OsmImporter.Core.Graph;
using OsmImporter.Core.Options;
using OsmImporter.Core.Osm;
using OsmImporter.Core.Output;
using OsmImporter.Publish;
using OsmImporter.Publish.Sync;

namespace OsmImporter.Core.Tests;

public class SyncPlannerTests : IDisposable
{
  private readonly List<string> _files = [];
  private static readonly MappedColumns All = new(true, true, true, true, true, true, true);

  private static SelectedWay Way(long id, string? lanes, params (double, double)[] coords) => new()
  {
    Way = new WayRecord { Id = id, Highway = "residential", Lanes = lanes, Refs = new long[coords.Length] },
    Coords = coords,
  };

  private GeoPackageReader Gpkg(params SelectedWay[] ways)
  {
    var (graph, _) = GraphBuilder.Build(ways, new BuildOptions());
    var path = Path.Combine(Path.GetTempPath(), $"osmimport-plan-{Guid.NewGuid():N}.gpkg");
    _files.Add(path);
    GeoPackageWriter.Write(path, graph, new Dictionary<string, string> { ["format"] = "osm-road-graph", ["snap_degrees"] = "0.00001" });
    return new GeoPackageReader(path);
  }

  /// <summary>Red existente con todos los atributos, como la lee el adaptador MySQL.</summary>
  private static ExistingNetwork Existing(GeoPackageReader g, long offset, Func<GpkgSegment, int?>? lanes = null) => new()
  {
    Nodes = g.Nodes().Select(n => new ExistingNode(n.Fid + offset, n.Lon, n.Lat, n.Valence)).ToList(),
    Segments = g.Segments().Select(s => new ExistingSegment(s.Fid + offset, s.OsmId, s.Highway, s.X1, s.Y1, s.X2, s.Y2,
      s.LengthMeters, s.Name, s.IsOneWay, lanes is null ? s.Lanes : lanes(s), s.MaxSpeed, s.TravelCost,
      s.FromNodeFid + offset, s.ToNodeFid + offset)).ToList(),
  };

  private static (NetworkDiff Diff, SyncPlan Plan) PlanFor(GeoPackageReader g, ExistingNetwork existing)
  {
    var diff = NetworkDiff.Compute(g, existing);
    diff.AssignNewIds(Enumerable.Range(50_000, diff.NewNodeCount).Select(i => (long)i).ToList(),
                      Enumerable.Range(90_000, diff.NewSegmentCount).Select(i => (long)i).ToList());
    return (diff, SyncPlanner.Plan(diff, existing, All));
  }

  private static readonly (double, double)[] A = [(-68.0, -38.9), (-68.0, -38.9004)];
  private static readonly (double, double)[] B = [(-68.0, -38.9004), (-68.0004, -38.9004)];

  [Fact]
  public void Identical_network_produces_no_writes()
  {
    using var g = Gpkg(Way(1, "2", A), Way(2, null, B));
    var (_, plan) = PlanFor(g, Existing(g, 100));

    Assert.Empty(plan.NodeInserts);
    Assert.Empty(plan.NodeUpdates);
    Assert.Empty(plan.SegmentInserts);
    Assert.Empty(plan.SegmentUpdates);
    Assert.Empty(plan.RetiredSegmentIds);
  }

  [Fact]
  public void Attribute_change_is_detected_per_column()
  {
    using var g = Gpkg(Way(1, "2", A));
    var (_, plan) = PlanFor(g, Existing(g, 100, lanes: _ => null)); // en la BD lanes estaba vacío

    Assert.Equal(g.SegmentCount, plan.SegmentUpdates.Count);
    Assert.Equal(g.SegmentCount, plan.ColumnChanges["segmentos.lanes"]);
    Assert.Equal(0, plan.ColumnChanges["segmentos.geometry"]);
  }

  [Fact]
  public void Retired_node_reference_is_remapped_to_nearest_within_distance()
  {
    using var before = Gpkg(Way(1, null, A), Way(2, null, B));
    using var after = Gpkg(Way(1, null, A)); // desaparece la vía 2 → su nodo final se retira
    var existing = Existing(before, 100);
    var (diff, plan) = PlanFor(after, existing);
    var retiredNode = Assert.Single(plan.RetiredNodeIds); // extremo (-68.0004, -38.9004), a ~35 m del más cercano
    var planner = new DependencyPlanner(diff, existing, plan);

    DependencyDecision Decide(double maxMeters, RetiredStrategy fallback, string source = "fk", bool nullable = true) => planner.Decide(new DependencyInput
    {
      Source = source, Table = "t", Column = "c", References = "nodes", IsNullable = nullable,
      Strategy = new DependencyStrategy { OnRetired = RetiredStrategy.RemapNearest, MaxDistanceMeters = maxMeters, Fallback = fallback },
      RowsByReferencedId = new() { [retiredNode] = 3 },
    });

    var near = Decide(50, RetiredStrategy.Report);
    Assert.Equal(3, near.Outcome.Remapped);
    Assert.NotNull(near.Map[retiredNode]);

    var far = Decide(10, RetiredStrategy.Report);           // FK + report → se conserva
    Assert.Equal(3, far.Outcome.Retained);
    Assert.Contains(retiredNode, far.Retain);

    var dangling = Decide(10, RetiredStrategy.Report, source: "declarada");
    Assert.Equal(3, dangling.Outcome.Dangling);

    var nulled = Decide(10, RetiredStrategy.SetNull);
    Assert.Null(nulled.Map[retiredNode]);

    Assert.Throws<DependentDataException>(() => Decide(10, RetiredStrategy.SetNull, nullable: false));
    Assert.Throws<DependentDataException>(() => Decide(10, RetiredStrategy.Block));
  }

  [Fact]
  public void Retention_keeps_segment_and_its_nodes()
  {
    using var before = Gpkg(Way(1, null, A), Way(2, null, B));
    using var after = Gpkg(Way(1, null, A));
    var existing = Existing(before, 100);
    var (_, plan) = PlanFor(after, existing);
    var retiredSeg = plan.RetiredSegmentIds.First();

    SyncPlanner.ApplyRetention(plan, existing, [], [retiredSeg], valenceMapped: true);

    Assert.DoesNotContain(retiredSeg, plan.RetiredSegmentIds);
    Assert.Equal(1, plan.RetainedSegments);
    var seg = existing.Segments.Single(s => s.Id == retiredSeg);
    Assert.DoesNotContain(seg.FromNodeId!.Value, plan.RetiredNodeIds);
    Assert.DoesNotContain(seg.ToNodeId!.Value, plan.RetiredNodeIds);
  }

  public void Dispose()
  {
    foreach (var f in _files) try { File.Delete(f); } catch { /* ignorar */ }
  }
}
