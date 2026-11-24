namespace OsmImporter.Core.Geo;

/// <summary>
/// Cálculos geodésicos equivalentes a los de PostGIS geography:
/// - ST_Segmentize(geography) trabaja sobre la esfera de radio medio (2a+b)/3.
/// - ST_Length(geography) mide sobre el elipsoide WGS84 (aquí con Vincenty, diferencias &lt; 0,1 mm).
/// </summary>
public static class Geodesy
{
  public const double WgsA = 6378137.0;
  public const double WgsF = 1 / 298.257223563;
  public const double WgsB = WgsA * (1 - WgsF);

  /// <summary>Radio de la esfera que usa PostGIS para geography (radio medio WGS84).</summary>
  public const double SphereRadius = (2 * WgsA + WgsB) / 3;

  private const double DegToRad = Math.PI / 180.0;
  private const double RadToDeg = 180.0 / Math.PI;

  /// <summary>Distancia de gran círculo en metros (esfera de radio medio).</summary>
  public static double SphereDistance(double lon1, double lat1, double lon2, double lat2)
  {
    var p1 = lat1 * DegToRad;
    var p2 = lat2 * DegToRad;
    var dp = p2 - p1;
    var dl = (lon2 - lon1) * DegToRad;
    var h = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
    return 2 * SphereRadius * Math.Asin(Math.Min(1, Math.Sqrt(h)));
  }

  /// <summary>Punto a la fracción t (0..1) del arco de gran círculo entre dos puntos (slerp).</summary>
  public static (double Lon, double Lat) Interpolate(double lon1, double lat1, double lon2, double lat2, double t)
  {
    var (x1, y1, z1) = ToUnitVector(lon1, lat1);
    var (x2, y2, z2) = ToUnitVector(lon2, lat2);

    var cx = y1 * z2 - z1 * y2;
    var cy = z1 * x2 - x1 * z2;
    var cz = x1 * y2 - y1 * x2;
    var omega = Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), x1 * x2 + y1 * y2 + z1 * z2);

    if (omega < 1e-15)
      return (lon1 + (lon2 - lon1) * t, lat1 + (lat2 - lat1) * t);

    var s = Math.Sin(omega);
    var a = Math.Sin((1 - t) * omega) / s;
    var b = Math.Sin(t * omega) / s;

    var x = a * x1 + b * x2;
    var y = a * y1 + b * y2;
    var z = a * z1 + b * z2;

    return (Math.Atan2(y, x) * RadToDeg, Math.Atan2(z, Math.Sqrt(x * x + y * y)) * RadToDeg);
  }

  private static (double X, double Y, double Z) ToUnitVector(double lon, double lat)
  {
    var l = lon * DegToRad;
    var p = lat * DegToRad;
    var cp = Math.Cos(p);
    return (cp * Math.Cos(l), cp * Math.Sin(l), Math.Sin(p));
  }

  /// <summary>Distancia sobre el elipsoide WGS84 (Vincenty inverso). Cae a la esfera si no converge.</summary>
  public static double SpheroidDistance(double lon1, double lat1, double lon2, double lat2)
  {
    if (lon1 == lon2 && lat1 == lat2) return 0;

    var L = (lon2 - lon1) * DegToRad;
    var u1 = Math.Atan((1 - WgsF) * Math.Tan(lat1 * DegToRad));
    var u2 = Math.Atan((1 - WgsF) * Math.Tan(lat2 * DegToRad));
    double sinU1 = Math.Sin(u1), cosU1 = Math.Cos(u1);
    double sinU2 = Math.Sin(u2), cosU2 = Math.Cos(u2);

    var lambda = L;
    double sinSigma, cosSigma, sigma, cosSqAlpha, cos2SigmaM;
    var iterations = 0;

    while (true)
    {
      var sinLambda = Math.Sin(lambda);
      var cosLambda = Math.Cos(lambda);
      var t1 = cosU2 * sinLambda;
      var t2 = cosU1 * sinU2 - sinU1 * cosU2 * cosLambda;
      sinSigma = Math.Sqrt(t1 * t1 + t2 * t2);
      if (sinSigma == 0) return 0;

      cosSigma = sinU1 * sinU2 + cosU1 * cosU2 * cosLambda;
      sigma = Math.Atan2(sinSigma, cosSigma);
      var sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
      cosSqAlpha = 1 - sinAlpha * sinAlpha;
      cos2SigmaM = cosSqAlpha != 0 ? cosSigma - 2 * sinU1 * sinU2 / cosSqAlpha : 0;

      var c = WgsF / 16 * cosSqAlpha * (4 + WgsF * (4 - 3 * cosSqAlpha));
      var prev = lambda;
      lambda = L + (1 - c) * WgsF * sinAlpha *
               (sigma + c * sinSigma * (cos2SigmaM + c * cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM)));

      if (Math.Abs(lambda - prev) < 1e-12) break;
      if (++iterations > 200) return SphereDistance(lon1, lat1, lon2, lat2);
    }

    var uSq = cosSqAlpha * (WgsA * WgsA - WgsB * WgsB) / (WgsB * WgsB);
    var A = 1 + uSq / 16384 * (4096 + uSq * (-768 + uSq * (320 - 175 * uSq)));
    var B = uSq / 1024 * (256 + uSq * (-128 + uSq * (74 - 47 * uSq)));
    var deltaSigma = B * sinSigma * (cos2SigmaM + B / 4 *
      (cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM) -
       B / 6 * cos2SigmaM * (-3 + 4 * sinSigma * sinSigma) * (-3 + 4 * cos2SigmaM * cos2SigmaM)));

    return WgsB * A * (sigma - deltaSigma);
  }

  /// <summary>WGS84 lon/lat → EPSG:3857 (Web Mercator), igual a ST_Transform(..., 3857).</summary>
  public static (double X, double Y) ToWebMercator(double lon, double lat)
  {
    var x = WgsA * lon * DegToRad;
    var y = WgsA * Math.Log(Math.Tan(Math.PI / 4 + lat * DegToRad / 2));
    return (x, y);
  }
}
