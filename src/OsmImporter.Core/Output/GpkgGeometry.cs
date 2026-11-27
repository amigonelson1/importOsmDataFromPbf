using System.Buffers.Binary;

namespace OsmImporter.Core.Output;

/// <summary>
/// Codificación GeoPackageBinary (spec OGC 12-128r18 §2.1.3): cabecera "GP" + WKB little-endian.
/// </summary>
public static class GpkgGeometry
{
  public const int Srid = 4326;

  public static byte[] Point(double x, double y)
  {
    var buf = new byte[8 + 21];
    WriteHeader(buf, envelope: false);
    var p = 8;
    buf[p++] = 1;                                                   // little endian
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p), 1); p += 4; // wkbPoint
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), x); p += 8;
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), y);
    return buf;
  }

  public static byte[] Line(double x1, double y1, double x2, double y2)
  {
    var buf = new byte[8 + 32 + 9 + 32];
    WriteHeader(buf, envelope: true);
    var p = 8;
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), Math.Min(x1, x2)); p += 8;
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), Math.Max(x1, x2)); p += 8;
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), Math.Min(y1, y2)); p += 8;
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), Math.Max(y1, y2)); p += 8;
    buf[p++] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p), 2); p += 4; // wkbLineString
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p), 2); p += 4; // 2 puntos
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), x1); p += 8;
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), y1); p += 8;
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), x2); p += 8;
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), y2);
    return buf;
  }

  public static byte[] LineString(double[] coords)
  {
    if (coords == null || coords.Length < 4) throw new ArgumentException("Al menos 2 puntos requeridos");
    var count = coords.Length / 2;
    var minX = double.MaxValue; var maxX = double.MinValue;
    var minY = double.MaxValue; var maxY = double.MinValue;
    for (int i = 0; i < coords.Length; i += 2)
    {
      var x = coords[i]; var y = coords[i + 1];
      if (x < minX) minX = x; if (x > maxX) maxX = x;
      if (y < minY) minY = y; if (y > maxY) maxY = y;
    }
    var buf = new byte[8 + 32 + 9 + count * 16];
    WriteHeader(buf, envelope: true);
    var p = 8;
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), minX); p += 8;
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), maxX); p += 8;
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), minY); p += 8;
    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), maxY); p += 8;
    buf[p++] = 1; // little endian
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p), 2); p += 4; // wkbLineString
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p), (uint)count); p += 4;
    for (int i = 0; i < coords.Length; i += 2)
    {
      BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), coords[i]); p += 8;
      BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(p), coords[i + 1]); p += 8;
    }
    return buf;
  }

  public static byte[] ReverseLineString(byte[] blob)
  {
    var coords = ReadCoordinates(blob);
    var rev = new double[coords.Length];
    for (int i = 0, j = coords.Length - 2; i < coords.Length; i += 2, j -= 2)
    {
      rev[i] = coords[j];
      rev[i + 1] = coords[j + 1];
    }
    return LineString(rev);
  }

  /// <summary>Lee una geometría Point/LineString de 2 puntos escrita por esta clase (o por GDAL).</summary>
  public static double[] ReadCoordinates(byte[] blob)
  {
    if (blob.Length < 8 || blob[0] != 'G' || blob[1] != 'P') throw new FormatException("No es GeoPackageBinary");
    var flags = blob[3];
    var envelopeKind = (flags >> 1) & 0b111;
    var envelopeBytes = envelopeKind switch { 0 => 0, 1 => 32, 2 or 3 => 48, 4 => 64, _ => throw new FormatException("envelope inválido") };
    var p = 8 + envelopeBytes;

    var littleEndian = blob[p++] == 1;
    uint ReadU32() { var v = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(p)) : BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(p)); p += 4; return v; }
    double ReadD() { var v = littleEndian ? BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(p)) : BinaryPrimitives.ReadDoubleBigEndian(blob.AsSpan(p)); p += 8; return v; }

    var type = ReadU32() % 1000;
    var count = type switch { 1 => 1u, 2 => ReadU32(), _ => throw new FormatException($"Tipo WKB {type} no soportado") };
    var result = new double[count * 2];
    for (var i = 0; i < result.Length; i++) result[i] = ReadD();
    return result;
  }

  private static void WriteHeader(byte[] buf, bool envelope)
  {
    buf[0] = (byte)'G';
    buf[1] = (byte)'P';
    buf[2] = 0;                                       // versión
    buf[3] = (byte)(0b1 | (envelope ? 0b010 : 0));    // little endian + envelope [minx,maxx,miny,maxy]
    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), Srid);
  }
}
