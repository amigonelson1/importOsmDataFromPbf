namespace OsmImporter.Publish;

public enum PublishMode
{
  /// <summary>Crea las tablas (no deben existir) y carga.</summary>
  Create,

  /// <summary>Vacía y recarga tablas existentes. Se niega si hay datos dependientes.</summary>
  Replace,

  /// <summary>
  /// Diff por llave natural: lo que no cambió conserva su Id, lo nuevo se inserta, lo retirado se borra
  /// después de resolver las referencias que apuntan a ello (según las estrategias del perfil).
  /// </summary>
  Sync,
}

public enum RetiredStrategy
{
  /// <summary>Reasignar al elemento más cercano de la red nueva (dentro de MaxDistanceMeters).</summary>
  RemapNearest,

  /// <summary>Poner la referencia en NULL.</summary>
  SetNull,

  /// <summary>No tocar la referencia y reportarla. Si es una FK real, el elemento retirado se conserva.</summary>
  Report,

  /// <summary>Abortar toda la sincronización si hay referencias afectadas.</summary>
  Block,
}

/// <summary>Qué hacer con las filas que apuntan a nodos/segmentos que desaparecen de la red.</summary>
public class DependencyStrategy
{
  public RetiredStrategy OnRetired { get; set; } = RetiredStrategy.RemapNearest;
  public double MaxDistanceMeters { get; set; } = 30;

  /// <summary>Para las filas que RemapNearest no logra reasignar.</summary>
  public RetiredStrategy Fallback { get; set; } = RetiredStrategy.Report;
}

/// <summary>
/// Sección "publish" del perfil: dónde y con qué nombres se escribe la red en el destino.
/// Nombres por defecto genéricos (snake_case); cada proyecto los ajusta en su perfil.
/// Una columna opcional en null = no se escribe.
/// Nunca contiene la cadena de conexión.
/// </summary>
public sealed class PublishOptions
{
  public string Provider { get; set; } = "postgres";
  public string? Schema { get; set; } = "public";

  /// <summary>Solo SQL Server, al crear tablas: "geography" (default, como EF Core + NetTopologySuite) o "geometry".
  /// En tablas existentes se detecta el tipo de la columna.</summary>
  public string SqlServerSpatialType { get; set; } = "geography";
  public NodesMapping Nodes { get; set; } = new();
  public SegmentsMapping Segments { get; set; } = new();

  /// <summary>
  /// Referencias hacia la red desde otras tablas. Las FKs reales se detectan solas; aquí se declaran
  /// las lógicas (sin FK), ej. una columna de texto que guarda el Id de un segmento. Cada una trae su
  /// estrategia para elementos retirados (sync); replace se niega si alguna tiene filas.
  /// </summary>
  public List<DependencyRule> Dependencies { get; set; } = new();

  /// <summary>Estrategia para las FKs reales detectadas automáticamente (las declaradas traen la suya).</summary>
  public DependencyStrategy ForeignKeyDefaults { get; set; } = new();

  /// <summary>
  /// sync aborta si se retiraría más de este % de los segmentos existentes. Protege contra publicar un
  /// gpkg de otra zona o con otros parámetros. Se sobrescribe con --max-retired-percent.
  /// </summary>
  public double MaxRetiredPercent { get; set; } = 20;

  public void Validate()
  {
    var errors = new List<string>();
    void Required(string? value, string name) { if (string.IsNullOrWhiteSpace(value)) errors.Add($"publish.{name} es obligatorio"); }

    Required(Provider, "provider");
    Required(Nodes?.Table, "nodes.table");
    Required(Nodes?.Id, "nodes.id");
    Required(Nodes?.Location, "nodes.location");
    Required(Segments?.Table, "segments.table");
    Required(Segments?.Id, "segments.id");
    Required(Segments?.OsmId, "segments.osmId");
    Required(Segments?.HighwayType, "segments.highwayType");
    Required(Segments?.Geometry, "segments.geometry");
    Required(Segments?.FromNodeId, "segments.fromNodeId");
    Required(Segments?.ToNodeId, "segments.toNodeId");

    void CheckStrategy(DependencyStrategy st, string name)
    {
      if (st.MaxDistanceMeters <= 0) errors.Add($"{name}: maxDistanceMeters debe ser > 0");
      if (st.Fallback == RetiredStrategy.RemapNearest) errors.Add($"{name}: fallback no puede ser remapNearest");
    }

    if (MaxRetiredPercent is < 0 or > 100) errors.Add("publish.maxRetiredPercent debe estar entre 0 y 100");
    CheckStrategy(ForeignKeyDefaults ??= new(), "publish.foreignKeyDefaults");

    foreach (var d in Dependencies ?? new List<DependencyRule>())
    {
      CheckStrategy(d, $"publish.dependencies ({d.Table}.{d.Column})");
      if (string.IsNullOrWhiteSpace(d.Table) || string.IsNullOrWhiteSpace(d.Column))
        errors.Add("publish.dependencies: table y column son obligatorios");
      if (d.References is not ("nodes" or "segments"))
        errors.Add($"publish.dependencies ({d.Table}.{d.Column}): references debe ser 'nodes' o 'segments'");
    }

    if (errors.Count > 0)
      throw new ArgumentException("Perfil de publicación inválido:\n  - " + string.Join("\n  - ", errors));
  }
}

public sealed class NodesMapping
{
  public string Table { get; set; } = "road_nodes";
  public string Id { get; set; } = "id";
  public string Location { get; set; } = "location";
  public string? Valence { get; set; } = "valence";
  public string? CreatedAt { get; set; } = "created_at";
}

public sealed class SegmentsMapping
{
  public string Table { get; set; } = "road_segments";
  public string Id { get; set; } = "id";
  public string OsmId { get; set; } = "osm_id";
  public string? Name { get; set; } = "name";
  public string HighwayType { get; set; } = "highway_type";
  public string? IsOneWay { get; set; } = "is_one_way";
  public string? Lanes { get; set; } = "lanes";
  public string? MaxSpeed { get; set; } = "max_speed";
  public string? Surface { get; set; }
  public string? Smoothness { get; set; }
  public string? TrackType { get; set; }
  public string? Bridge { get; set; }
  public string? Tunnel { get; set; }
  public string? Layer { get; set; }
  public string? MaxWeight { get; set; }
  public string? MaxHeight { get; set; }
  public string? MaxWidth { get; set; }
  public string Geometry { get; set; } = "geometry";
  public string? LengthMeters { get; set; } = "length_meters";
  public string? TravelCost { get; set; } = "travel_cost";
  public string FromNodeId { get; set; } = "from_node_id";
  public string ToNodeId { get; set; } = "to_node_id";
  public string? CreatedAt { get; set; } = "created_at";

  /// <summary>
  /// Solo MySQL: columnas GENERADAS (STORED) con ST_StartPoint/ST_EndPoint(geometry) e índice espacial; equivalen
  /// al índice funcional GIST sobre los extremos en Postgres. Nunca se escriben. null = no crearlas (create).
  /// </summary>
  public string? StartPoint { get; set; } = "start_point";
  public string? EndPoint { get; set; } = "end_point";
}

public sealed class DependencyRule : DependencyStrategy
{
  public string? Schema { get; set; }
  public string Table { get; set; } = default!;
  public string Column { get; set; } = default!;

  /// <summary>"nodes" o "segments".</summary>
  public string References { get; set; } = "nodes";

  /// <summary>Condición SQL adicional (confiable: viene del perfil), ej. "\"NetworkType\" = 2".</summary>
  public string? Filter { get; set; }

  public string? Description { get; set; }
}
