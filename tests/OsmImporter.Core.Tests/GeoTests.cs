using OsmImporter.Core.Geo;
using OsmImporter.Core.Options;

namespace OsmImporter.Core.Tests;

public class GeoTests
{
  [Fact]
  public void SpheroidDistance_matches_known_geodesic()
  {
    // Neuquén (plaza) → Cipolletti, referencia GeographicLib ≈ 15 km; validamos contra Vincenty de referencia
    // con un caso clásico: 1° de latitud en el ecuador sobre WGS84 = 110 574,389 m.
    var d = Geodesy.SpheroidDistance(0, 0, 0, 1);
    Assert.Equal(110_574.389, d, 0.01);

    // 1° de longitud en el ecuador = 111 319,491 m
    Assert.Equal(111_319.491, Geodesy.SpheroidDistance(0, 0, 1, 0), 0.01);
  }

  [Fact]
  public void Build_requires_bbox()
  {
    var ex = Assert.Throws<ArgumentException>(() => new BuildOptions().Validate());
    Assert.Contains("bbox", ex.Message);
  }

  [Fact]
  public void BuildOptions_rejects_snap_degrees_below_1e6()
  {
    var bbox = new BoundingBox(-69.0, -39.0, -67.0, -33.0);
    var options = new BuildOptions { BBox = bbox, SnapDegrees = 1e-7 };
    var ex = Assert.Throws<ArgumentException>(() => options.Validate());
    Assert.Contains("desbordamiento numérico", ex.Message);
  }

  [Fact]
  public void GridKey_roundtrips_at_minimum_safe_snap_degrees()
  {
    const double snap = 1e-6; // 0.1 metros de resolución
    var key = GridKey.KeyFor(180.0, 90.0, snap);
    var (lon, lat) = GridKey.Location(key, snap);

    Assert.Equal(180.0, lon, 1e-6);
    Assert.Equal(90.0, lat, 1e-6);
  }

  [Fact]
  public void SphereDistance_uses_mean_radius()
  {
    var expected = Geodesy.SphereRadius * Math.PI / 180;
    Assert.Equal(expected, Geodesy.SphereDistance(0, 0, 0, 1), 1e-6);
  }

  [Fact]
  public void Interpolate_midpoint_is_equidistant()
  {
    var (lon, lat) = Geodesy.Interpolate(-68.0, -38.9, -68.01, -38.95, 0.5);
    var a = Geodesy.SphereDistance(-68.0, -38.9, lon, lat);
    var b = Geodesy.SphereDistance(lon, lat, -68.01, -38.95);
    Assert.Equal(a, b, 1e-6);
  }

  [Theory]
  [InlineData(49.9, SegmentizeMethod.Uniform, 1)]
  [InlineData(50.0, SegmentizeMethod.Uniform, 1)]
  [InlineData(50.1, SegmentizeMethod.Uniform, 2)]
  [InlineData(222.0, SegmentizeMethod.Uniform, 5)]
  [InlineData(222.0, SegmentizeMethod.Bisect, 8)]
  [InlineData(100.0, SegmentizeMethod.Bisect, 2)]
  [InlineData(100.1, SegmentizeMethod.Bisect, 4)]
  public void PartsFor_follows_method(double distance, SegmentizeMethod method, int expected) =>
    Assert.Equal(expected, Segmentizer.PartsFor(distance, 50, method));

  [Fact]
  public void Segmentize_keeps_original_vertices_and_limits_length()
  {
    var input = new List<(double, double)> { (-68.0, -38.9), (-68.0, -38.902), (-68.003, -38.902) };
    var output = new List<(double Lon, double Lat)>();
    Segmentizer.Segmentize(input, 50, SegmentizeMethod.Uniform, output);

    Assert.Equal(input[0], output[0]);
    Assert.Equal(input[^1], output[^1]);
    Assert.Contains(input[1], output);
    for (var i = 1; i < output.Count; i++)
      Assert.True(Geodesy.SphereDistance(output[i - 1].Lon, output[i - 1].Lat, output[i].Lon, output[i].Lat) <= 50.0001);
  }

  [Fact]
  public void GridKey_roundtrips_and_is_stable()
  {
    const double snap = 0.00001;
    var key = GridKey.KeyFor(-68.0598712, -38.9516104, snap);
    var (lon, lat) = GridKey.Location(key, snap);

    Assert.Equal(-68.05987, lon, 1e-9);
    Assert.Equal(-38.95161, lat, 1e-9);
    Assert.Equal(key, GridKey.KeyFor(lon, lat, snap));
    Assert.Equal(key, GridKey.KeyFor(-68.059869, -38.951614, snap)); // misma celda

    var (ix, iy) = GridKey.Unpack(key);
    Assert.Equal(-6805987, ix);
    Assert.Equal(-3895161, iy);
  }

  [Theory]
  [InlineData("3.5", 3.5)]
  [InlineData("3.5 m", 3.5)]
  [InlineData("3.5m", 3.5)]
  [InlineData("12' 6\"", 3.81)]
  [InlineData("12'6\"", 3.81)]
  [InlineData("default", null)]
  [InlineData("none", null)]
  [InlineData("", null)]
  public void TagParsing_ParseDimension(string input, double? expected)
  {
    var res = OsmImporter.Core.Osm.TagParsing.ParseDimension(input);
    if (expected is null) Assert.Null(res);
    else { Assert.NotNull(res); Assert.Equal(expected.Value, res.Value, 0.01); }
  }

  [Theory]
  [InlineData("12", 12.0)]
  [InlineData("12 t", 12.0)]
  [InlineData("3500 kg", 3.5)]
  [InlineData("20000 lbs", 9.07)]
  [InlineData("none", null)]
  public void TagParsing_ParseWeight(string input, double? expected)
  {
    var res = OsmImporter.Core.Osm.TagParsing.ParseWeight(input);
    if (expected is null) Assert.Null(res);
    else { Assert.NotNull(res); Assert.Equal(expected.Value, res.Value, 0.01); }
  }

  [Theory]
  [InlineData("1", 1)]
  [InlineData("-2", -2)]
  [InlineData("+3", 3)]
  [InlineData("0", 0)]
  [InlineData("invalid", 0)]
  [InlineData(null, 0)]
  public void TagParsing_ParseLayer(string? input, int expected) =>
    Assert.Equal(expected, OsmImporter.Core.Osm.TagParsing.ParseLayer(input));

  [Fact]
  public void GridKey_uses_bankers_rounding_like_rint()
  {
    Assert.Equal(2, GridKey.ToGrid(2.5, 1));
    Assert.Equal(4, GridKey.ToGrid(3.5, 1));
    Assert.Equal(-2, GridKey.ToGrid(-2.5, 1));
  }

  [Fact]
  public void OperationalArea_real_buffer_is_wider_than_legacy()
  {
    var bbox = new BoundingBox(-69.419662, -38.988540, -67.716560, -33.070484);
    var real = new OperationalArea(new BuildOptions { BBox = bbox });
    var legacy = new OperationalArea(new BuildOptions { BBox = bbox, LegacyBuffer = true });
    Assert.Equal(20000, legacy.Buffer3857);
    Assert.True(real.Buffer3857 > 25000);

    // Línea 18 km al oeste del bbox (a −36°): entra con buffer real, no con el legacy (≈ 16 km reales)
    var lonOffset = 18_000 / (111_320 * Math.Cos(36 * Math.PI / 180));
    var line = new List<(double, double)> { (-69.419662 - lonOffset, -36.0), (-69.419662 - lonOffset - 0.01, -36.0) };
    Assert.True(real.Intersects(line));
    Assert.False(legacy.Intersects(line));
  }

  [Fact]
  public void WaySplitter_splits_every_100km_in_3857_and_keeps_vertices()
  {
    // Línea recta de 1° de longitud a −38°: ≈ 111,3 km en 3857 → 2 tramos
    var line = new[] { (-69.0, -38.0), (-68.5, -38.0), (-68.0, -38.0) };
    var pieces = WaySplitter.Split(line, 100_000);

    Assert.Equal(2, pieces.Count);
    Assert.Equal(line[0], pieces[0][0]);
    Assert.Equal(line[^1], pieces[1][^1]);
    Assert.Equal(pieces[0][^1], pieces[1][0]);              // el punto de corte es compartido
    Assert.Contains(line[1], pieces[0]);                     // los vértices originales se conservan tal cual

    var (cutX, _) = Geodesy.ToWebMercator(pieces[0][^1].Lon, pieces[0][^1].Lat);
    var (startX, _) = Geodesy.ToWebMercator(line[0].Item1, line[0].Item2);
    Assert.Equal(100_000, cutX - startX, 1e-6);

    Assert.Single(WaySplitter.Split(line, 0));               // 0 = sin partir
  }

  [Fact]
  public void GpkgGeometry_LineString_and_ReverseLineString_work_correctly()
  {
    double[] coords = [-73.1234, 7.1234, -73.1235, 7.1236, -73.1240, 7.1245];
    var blob = OsmImporter.Core.Output.GpkgGeometry.LineString(coords);
    var read = OsmImporter.Core.Output.GpkgGeometry.ReadCoordinates(blob);

    Assert.Equal(coords.Length, read.Length);
    for (int i = 0; i < coords.Length; i++)
      Assert.Equal(coords[i], read[i], 1e-6);

    var revBlob = OsmImporter.Core.Output.GpkgGeometry.ReverseLineString(blob);
    var revRead = OsmImporter.Core.Output.GpkgGeometry.ReadCoordinates(revBlob);

    Assert.Equal(coords.Length, revRead.Length);
    Assert.Equal(coords[4], revRead[0], 1e-6);
    Assert.Equal(coords[5], revRead[1], 1e-6);
    Assert.Equal(coords[0], revRead[4], 1e-6);
    Assert.Equal(coords[1], revRead[5], 1e-6);
  }
}
