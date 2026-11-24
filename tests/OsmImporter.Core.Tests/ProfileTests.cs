using System.Text.Json;
using OsmImporter.Core.Graph;
using OsmImporter.Core.Options;
using OsmImporter.Core.Osm;
using OsmImporter.Core.Output;
using OsmImporter.Publish;

namespace OsmImporter.Core.Tests;

public class ProfileTests : IDisposable
{
  private readonly List<string> _tempFiles = [];

  public void Dispose()
  {
    foreach (var file in _tempFiles)
    {
      try { if (File.Exists(file)) File.Delete(file); } catch { /* ignore */ }
    }
  }

  [Theory]
  [InlineData("profiles/sherpaslb.json")]
  [InlineData("profiles/sherpaslb-mysql.json")]
  [InlineData("profiles/generic.json")]
  public void Standard_profiles_contain_all_new_road_attributes(string relativePath)
  {
    var dir = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(dir, "profiles")))
    {
      dir = Directory.GetParent(dir)?.FullName;
    }
    Assert.NotNull(dir);
    var fullPath = Path.Combine(dir, relativePath);
    Assert.True(File.Exists(fullPath), $"Profile file not found at {fullPath}");

    var json = File.ReadAllText(fullPath);
    var docOptions = new JsonDocumentOptions
    {
      CommentHandling = JsonCommentHandling.Skip,
      AllowTrailingCommas = true
    };
    using var doc = JsonDocument.Parse(json, docOptions);
    var root = doc.RootElement;

    Assert.True(root.TryGetProperty("publish", out var publish), "Missing 'publish' element");
    Assert.True(publish.TryGetProperty("segments", out var segments), "Missing 'segments' element");

    var requiredAttrs = new[]
    {
      "surface", "smoothness", "trackType", "bridge", "tunnel", "layer",
      "maxWeight", "maxHeight", "maxWidth"
    };

    foreach (var attr in requiredAttrs)
    {
      var found = segments.TryGetProperty(attr, out _) ||
                  segments.TryGetProperty(attr.ToLowerInvariant(), out _);
      Assert.True(found, $"Attribute '{attr}' missing in profile {relativePath}");
    }
  }

  [Fact]
  public void Geopackage_roundtrip_preserves_all_nine_attributes()
  {
    var way = new SelectedWay
    {
      Way = new WayRecord
      {
        Id = 42,
        Highway = "track",
        Surface = "gravel",
        Smoothness = "bad",
        TrackType = "grade2",
        Bridge = true,
        Tunnel = false,
        Layer = 1,
        MaxWeight = 12.5,
        MaxHeight = 3.8,
        MaxWidth = 2.5,
        Refs = [100, 101]
      },
      Coords = [(-68.0, -38.9), (-68.0, -38.9005)]
    };

    var (graph, _) = GraphBuilder.Build([way], new BuildOptions());
    var gpkgPath = Path.Combine(Path.GetTempPath(), $"osmimport-attrs-{Guid.NewGuid():N}.gpkg");
    _tempFiles.Add(gpkgPath);

    GeoPackageWriter.Write(gpkgPath, graph, new Dictionary<string, string> { ["format"] = "osm-road-graph" });

    using var reader = new GeoPackageReader(gpkgPath);
    var segments = reader.Segments().ToList();
    Assert.NotEmpty(segments);
    Assert.All(segments, seg =>
    {
      Assert.Equal("gravel", seg.Surface);
      Assert.Equal("bad", seg.Smoothness);
      Assert.Equal("grade2", seg.TrackType);
      Assert.True(seg.Bridge);
      Assert.False(seg.Tunnel);
      Assert.Equal(1, seg.Layer);
      Assert.Equal(12.5, seg.MaxWeight);
      Assert.Equal(3.8, seg.MaxHeight);
      Assert.Equal(2.5, seg.MaxWidth);
    });
  }
}
