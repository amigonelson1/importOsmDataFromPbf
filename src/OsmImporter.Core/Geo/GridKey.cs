namespace OsmImporter.Core.Geo;

/// <summary>
/// Llave natural de un nodo: su coordenada snapeada a la grilla (equivalente a ST_SnapToGrid).
/// Se empaqueta en un long estable: mismos datos y mismo snap ⇒ misma llave en cualquier carga.
/// </summary>
public static class GridKey
{
  private const long Offset = 1L << 30;
  private const long Mask31 = (1L << 31) - 1;

  /// <summary>Índice de grilla. Math.Round con ToEven replica rint() de C que usa PostGIS.</summary>
  public static long ToGrid(double value, double size) => (long)Math.Round(value / size, MidpointRounding.ToEven);

  public static double FromGrid(long index, double size) => index * size;

  public static long Pack(long ix, long iy) => ((ix + Offset) << 31) | (iy + Offset);

  public static (long Ix, long Iy) Unpack(long key) => ((key >> 31) - Offset, (key & Mask31) - Offset);

  public static long KeyFor(double lon, double lat, double size) => Pack(ToGrid(lon, size), ToGrid(lat, size));

  public static (double Lon, double Lat) Location(long key, double size)
  {
    var (ix, iy) = Unpack(key);
    return (FromGrid(ix, size), FromGrid(iy, size));
  }
}
