using OsmImporter.Core.Options;

namespace OsmImporter.Core.Geo;

/// <summary>Densificación geodésica equivalente a ST_Segmentize(geography, maxMeters).</summary>
public static class Segmentizer
{
  public static void Segmentize(
    IReadOnlyList<(double Lon, double Lat)> points,
    double maxMeters,
    SegmentizeMethod method,
    List<(double Lon, double Lat)> output)
  {
    output.Clear();
    if (points.Count == 0) return;

    output.Add(points[0]);

    for (var i = 1; i < points.Count; i++)
    {
      var (lon1, lat1) = points[i - 1];
      var (lon2, lat2) = points[i];
      var d = Geodesy.SphereDistance(lon1, lat1, lon2, lat2);

      if (d > maxMeters)
      {
        var parts = PartsFor(d, maxMeters, method);
        for (var k = 1; k < parts; k++)
          output.Add(Geodesy.Interpolate(lon1, lat1, lon2, lat2, (double)k / parts));
      }

      output.Add(points[i]);
    }
  }

  public static int PartsFor(double distance, double maxMeters, SegmentizeMethod method)
  {
    if (distance <= maxMeters) return 1;

    if (method == SegmentizeMethod.Uniform)
      return (int)Math.Ceiling(distance / maxMeters);

    // Bisección recursiva: todas las sub-aristas miden lo mismo, así que equivale a 2^k partes
    // con el k mínimo que deja cada parte ≤ max.
    var n = 1;
    while (distance / n > maxMeters) n *= 2;
    return n;
  }
}
