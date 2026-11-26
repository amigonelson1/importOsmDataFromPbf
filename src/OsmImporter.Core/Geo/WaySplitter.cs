namespace OsmImporter.Core.Geo;

/// <summary>
/// Parte vías largas en tramos, igual que osm2pgsql (split_linestring, salida pgsql): recorre la línea en
/// EPSG:3857 y corta cada <c>splitAt</c> unidades interpolando el punto de corte. Con el área operativa,
/// solo se conservan los tramos que la tocan — así se comportaba el SQL original (planet_osm_line).
/// </summary>
public static class WaySplitter
{
  private const double R = Geodesy.WgsA;

  public static List<(double Lon, double Lat)[]> Split((double Lon, double Lat)[] lonLat, double splitAt)
  {
    if (splitAt <= 0 || lonLat.Length < 2) return [lonLat];

    var pieces = new List<(double Lon, double Lat)[]>();
    var current = new List<(double Lon, double Lat)> { lonLat[0] };
    var (px, py) = Geodesy.ToWebMercator(lonLat[0].Lon, lonLat[0].Lat);
    double dist = 0;

    for (var i = 1; i < lonLat.Length; i++)
    {
      var (tx, ty) = Geodesy.ToWebMercator(lonLat[i].Lon, lonLat[i].Lat);
      var delta = Math.Sqrt((tx - px) * (tx - px) + (ty - py) * (ty - py));

      if (dist + delta > splitAt)
      {
        var splits = (int)Math.Floor((dist + delta) / splitAt);
        double ix = 0, iy = 0;
        for (var j = 0; j < splits; j++)
        {
          var frac = ((j + 1) * splitAt - dist) / delta;
          ix = px + frac * (tx - px);
          iy = py + frac * (ty - py);
          var ipoint = FromWebMercator(ix, iy);
          if (frac != 0) current.Add(ipoint);
          Flush(pieces, current);
          current = [ipoint];
        }

        if (tx == ix && ty == iy)
        {
          dist = 0;
          px = tx; py = ty;
          continue;
        }

        dist = Math.Sqrt((tx - ix) * (tx - ix) + (ty - iy) * (ty - iy));
      }
      else
      {
        dist += delta;
      }

      current.Add(lonLat[i]);
      px = tx; py = ty;
    }

    Flush(pieces, current);
    return pieces;
  }

  private static void Flush(List<(double, double)[]> pieces, List<(double, double)> current)
  {
    if (current.Count > 1) pieces.Add(current.ToArray());
  }

  public static (double Lon, double Lat) FromWebMercator(double x, double y) =>
    (x / R * 180 / Math.PI, (2 * Math.Atan(Math.Exp(y / R)) - Math.PI / 2) * 180 / Math.PI);
}
