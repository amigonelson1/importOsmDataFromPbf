namespace OsmImporter.Core.Graph;

/// <summary>Metadatos de la vía OSM, compartidos por todos sus micro-segmentos.</summary>
public sealed class RoadWayInfo
{
  public long OsmId { get; init; }
  public string? Name { get; init; }
  public string Highway { get; init; } = default!;
  public bool IsOneWay { get; init; }
  public int? Lanes { get; init; }
  public int? MaxSpeed { get; init; }

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

public sealed class RoadNode
{
  /// <summary>Id local del artefacto (1..n). No es estable entre cargas: usar Key.</summary>
  public long Fid { get; set; }

  /// <summary>Llave natural estable (coordenada snapeada empaquetada, ver GridKey).</summary>
  public long Key { get; init; }

  public double Lon { get; init; }
  public double Lat { get; init; }
  public int Valence { get; set; }
}

public sealed class RoadSegment
{
  public long Fid { get; set; }
  public RoadWayInfo Way { get; init; } = default!;

  /// <summary>Llave natural estable: "OsmId:FromKey:ToKey" (+ "#n" si se repite).</summary>
  public string Key => Ordinal <= 1 ? $"{Way.OsmId}:{FromKey}:{ToKey}" : $"{Way.OsmId}:{FromKey}:{ToKey}#{Ordinal}";

  public int Ordinal { get; init; } = 1;

  public double X1 { get; init; }
  public double Y1 { get; init; }
  public double X2 { get; init; }
  public double Y2 { get; init; }

  public long FromKey { get; init; }
  public long ToKey { get; init; }
  public RoadNode FromNode { get; init; } = default!;
  public RoadNode ToNode { get; init; } = default!;

  public double LengthMeters { get; init; }
  public double TravelCost { get; init; }
}

public sealed class TurnRestriction
{
  public long Fid { get; set; }
  public long RelationId { get; init; }
  public string Restriction { get; init; } = default!;
  public long FromOsmId { get; init; }
  public long ViaNodeKey { get; init; }
  public long ToOsmId { get; init; }
}

public sealed class RoadGraph
{
  public List<RoadNode> Nodes { get; } = new();
  public List<RoadSegment> Segments { get; } = new();
  public List<TurnRestriction> Restrictions { get; } = new();
}
