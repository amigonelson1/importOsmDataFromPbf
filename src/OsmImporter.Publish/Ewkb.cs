using System.Buffers.Binary;

namespace OsmImporter.Publish;

/// <summary>EWKB little-endian con SRID (lo aceptan geometry_recv de PostGIS y COPY binario).</summary>
public static class Ewkb
{
  private const uint SridFlag = 0x20000000;

  public static byte[] Point(double x, double y, int srid)
  {
    var b = new byte[1 + 4 + 4 + 16];
    b[0] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), 1 | SridFlag);
    BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), srid);
    BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(9), x);
    BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(17), y);
    return b;
  }

  /// <summary>WKB estándar (OGC, sin SRID) little-endian, para ST_GeomFromWKB de MySQL / SQL Server.</summary>
  public static byte[] PointWkb(double x, double y)
  {
    var b = new byte[1 + 4 + 16];
    b[0] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), 1);
    BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(5), x);
    BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(13), y);
    return b;
  }

  public static byte[] LineWkb(double x1, double y1, double x2, double y2)
  {
    var b = new byte[1 + 4 + 4 + 32];
    b[0] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5), 2);
    BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(9), x1);
    BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(17), y1);
    BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(25), x2);
    BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(33), y2);
    return b;
  }

  public static byte[] Line(double x1, double y1, double x2, double y2, int srid)
  {
    var b = new byte[1 + 4 + 4 + 4 + 32];
    b[0] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), 2 | SridFlag);
    BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), srid);
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(9), 2);
    BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(13), x1);
    BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(21), y1);
    BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(29), x2);
    BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(37), y2);
    return b;
  }
}
