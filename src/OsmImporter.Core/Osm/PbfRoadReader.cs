using System.Diagnostics;
using OsmSharp;
using OsmSharp.Streams;

namespace OsmImporter.Core.Osm;

/// <summary>Vía OSM con tag highway (solo los tags que interesan).</summary>
public sealed class WayRecord
{
  public long Id { get; init; }
  public string Highway { get; init; } = default!;
  public string? Name { get; init; }
  public string? Oneway { get; init; }
  public string? Lanes { get; init; }
  public string? MaxSpeed { get; init; }
  public bool AreaYes { get; init; }
  public long[] Refs { get; init; } = [];

  public string? Surface { get; init; }
  public string? Smoothness { get; init; }
  public string? TrackType { get; init; }
  public bool Bridge { get; init; }
  public bool Tunnel { get; init; }
  public int Layer { get; init; }
  public double? MaxWeight { get; init; }
  public double? MaxHeight { get; init; }
  public double? MaxWidth { get; init; }
}

public sealed class RestrictionRecord
{
  public long Id { get; init; }
  public string Restriction { get; init; } = default!;
  public long FromWayId { get; init; }
  public long ViaNodeId { get; init; }
  public long ToWayId { get; init; }
}

public sealed class PbfReadResult
{
  public required List<WayRecord> Ways { get; init; }
  public List<RestrictionRecord> Restrictions { get; init; } = [];

  /// <summary>Ids de nodos requeridos, ordenados y únicos.</summary>
  public required long[] NodeIds { get; init; }

  /// <summary>Coordenadas en 1e-7 grados (resolución nativa del pbf). int.MinValue = nodo ausente.</summary>
  public required int[] LonE7 { get; init; }
  public required int[] LatE7 { get; init; }

  public long TotalWaysScanned { get; init; }
  public long TotalNodesScanned { get; init; }
  public long NodesFound { get; init; }

  public bool TryGetCoordinate(long nodeId, out double lon, out double lat)
  {
    var i = Array.BinarySearch(NodeIds, nodeId);
    if (i >= 0 && LonE7[i] != int.MinValue)
    {
      lon = LonE7[i] / 1e7;
      lat = LatE7[i] / 1e7;
      return true;
    }

    lon = lat = 0;
    return false;
  }
}

/// <summary>
/// Lee un .pbf en dos pasadas por streaming (memoria acotada):
/// 1) vías con tag highway y los ids de sus nodos; 2) coordenadas solo de esos nodos.
/// </summary>
public static class PbfRoadReader
{
  private const int ProgressEvery = 2_000_000;

  public static PbfReadResult Read(string pbfPath, CancellationToken ct = default)
  {
    var internTable = new Dictionary<string, string>(StringComparer.Ordinal);
    string Intern(string s)
    {
      if (internTable.TryGetValue(s, out var v)) return v;
      internTable[s] = s;
      return s;
    }

    // ---------- Pasada 1: vías y restricciones de giro ----------
    var ways = new List<WayRecord>();
    var restrictions = new List<RestrictionRecord>();
    var refs = new List<long>(1 << 22);
    long waysScanned = 0, elements = 0;
    var sw = Stopwatch.StartNew();

    using (var stream = File.OpenRead(pbfPath))
    {
      var source = new PBFOsmStreamSource(stream);
      foreach (var geo in source)
      {
        if (++elements % ProgressEvery == 0)
        {
          ct.ThrowIfCancellationRequested();
          Log.Info($"  pasada 1: {elements:N0} elementos, {ways.Count:N0} vías highway");
        }

        if (geo.Type == OsmGeoType.Relation)
        {
          var rel = (Relation)geo;
          if (rel.Tags is not null &&
              rel.Tags.TryGetValue("type", out var rtype) &&
              rtype.StartsWith("restriction", StringComparison.OrdinalIgnoreCase) &&
              rel.Tags.TryGetValue("restriction", out var restr) &&
              rel.Members is not null)
          {
            long fromWay = 0, viaNode = 0, toWay = 0;
            foreach (var m in rel.Members)
            {
              if (m.Role == "from" && m.Type == OsmGeoType.Way) fromWay = m.Id;
              else if (m.Role == "via" && m.Type == OsmGeoType.Node) viaNode = m.Id;
              else if (m.Role == "to" && m.Type == OsmGeoType.Way) toWay = m.Id;
            }
            if (fromWay > 0 && toWay > 0)
            {
              restrictions.Add(new RestrictionRecord
              {
                Id = rel.Id ?? 0,
                Restriction = Intern(restr.Trim().ToLowerInvariant()),
                FromWayId = fromWay,
                ViaNodeId = viaNode,
                ToWayId = toWay
              });
              if (viaNode > 0) refs.Add(viaNode);
            }
          }
          continue;
        }

        if (geo.Type != OsmGeoType.Way) continue;

        waysScanned++;
        var way = (Way)geo;
        if (way.Tags is null || way.Nodes is null || way.Nodes.Length < 2) continue;
        if (!way.Tags.TryGetValue("highway", out var highway) || string.IsNullOrWhiteSpace(highway)) continue;

        way.Tags.TryGetValue("name", out var name);
        way.Tags.TryGetValue("oneway", out var oneway);
        way.Tags.TryGetValue("lanes", out var lanes);
        way.Tags.TryGetValue("maxspeed", out var maxSpeed);
        way.Tags.TryGetValue("area", out var area);
        way.Tags.TryGetValue("surface", out var surface);
        way.Tags.TryGetValue("smoothness", out var smoothness);
        way.Tags.TryGetValue("tracktype", out var trackType);
        way.Tags.TryGetValue("bridge", out var bridge);
        way.Tags.TryGetValue("tunnel", out var tunnel);
        way.Tags.TryGetValue("layer", out var layer);
        way.Tags.TryGetValue("maxweight", out var maxWeight);
        way.Tags.TryGetValue("maxheight", out var maxHeight);
        way.Tags.TryGetValue("maxwidth", out var maxWidth);

        ways.Add(new WayRecord
        {
          Id = way.Id ?? 0,
          Highway = Intern(highway.Trim().ToLowerInvariant()),
          Name = name,
          Oneway = oneway is null ? null : Intern(oneway.Trim().ToLowerInvariant()),
          Lanes = lanes,
          MaxSpeed = maxSpeed,
          AreaYes = string.Equals(area, "yes", StringComparison.OrdinalIgnoreCase),
          Refs = way.Nodes,
          Surface = surface is null ? null : Intern(surface.Trim().ToLowerInvariant()),
          Smoothness = smoothness is null ? null : Intern(smoothness.Trim().ToLowerInvariant()),
          TrackType = trackType is null ? null : Intern(trackType.Trim().ToLowerInvariant()),
          Bridge = bridge is not null && !string.Equals(bridge, "no", StringComparison.OrdinalIgnoreCase),
          Tunnel = tunnel is not null && !string.Equals(tunnel, "no", StringComparison.OrdinalIgnoreCase),
          Layer = TagParsing.ParseLayer(layer),
          MaxWeight = TagParsing.ParseWeight(maxWeight),
          MaxHeight = TagParsing.ParseDimension(maxHeight),
          MaxWidth = TagParsing.ParseDimension(maxWidth),
        });
        refs.AddRange(way.Nodes);
      }
    }

    Log.Info($"  pasada 1 (vías/relaciones): {waysScanned:N0} vías leídas, {ways.Count:N0} con highway, {restrictions.Count:N0} restricciones ({sw.Elapsed:mm\\:ss})");

    var nodeIds = refs.ToArray();
    refs = null!;
    Array.Sort(nodeIds);
    var unique = 0;
    for (var i = 0; i < nodeIds.Length; i++)
      if (i == 0 || nodeIds[i] != nodeIds[unique - 1])
        nodeIds[unique++] = nodeIds[i];
    Array.Resize(ref nodeIds, unique);

    // ---------- Pasada 2: coordenadas de nodos ----------
    var lonE7 = new int[nodeIds.Length];
    var latE7 = new int[nodeIds.Length];
    Array.Fill(lonE7, int.MinValue);
    Array.Fill(latE7, int.MinValue);
    long nodesScanned = 0, found = 0;
    sw.Restart();

    using (var stream = File.OpenRead(pbfPath))
    {
      var source = new PBFOsmStreamSource(stream);
      foreach (var geo in source)
      {
        if (geo.Type != OsmGeoType.Node) break; // los nodos van primero en un pbf ordenado

        if (++nodesScanned % ProgressEvery == 0)
        {
          ct.ThrowIfCancellationRequested();
          Log.Info($"  pasada 2: {nodesScanned:N0} nodos, {found:N0} requeridos encontrados");
        }

        var node = (Node)geo;
        if (node.Id is not { } id || node.Longitude is not { } lon || node.Latitude is not { } lat) continue;

        var i = Array.BinarySearch(nodeIds, id);
        if (i < 0) continue;

        lonE7[i] = (int)Math.Round(lon * 1e7);
        latE7[i] = (int)Math.Round(lat * 1e7);
        found++;
      }
    }

    Log.Info($"  pasada 2 (nodos): {nodesScanned:N0} nodos leídos, {found:N0}/{nodeIds.Length:N0} requeridos encontrados ({sw.Elapsed:mm\\:ss})");

    if (nodesScanned == 0)
      throw new InvalidDataException("El pbf no tiene nodos al inicio. ¿Es un archivo ordenado (Sort.Type_then_ID)? Ordénalo con: osmium sort entrada.pbf -o salida.pbf");

    return new PbfReadResult
    {
      Ways = ways,
      Restrictions = restrictions,
      NodeIds = nodeIds,
      LonE7 = lonE7,
      LatE7 = latE7,
      TotalWaysScanned = waysScanned,
      TotalNodesScanned = nodesScanned,
      NodesFound = found,
    };
  }
}
