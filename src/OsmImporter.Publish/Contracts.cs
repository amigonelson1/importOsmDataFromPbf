using OsmImporter.Core.Output;

namespace OsmImporter.Publish;

public sealed class PublishFlags
{
  public bool DryRun { get; init; }
  public bool Backup { get; init; } = true;

  /// <summary>Sobrescribe publish.maxRetiredPercent del perfil.</summary>
  public double? MaxRetiredPercent { get; init; }
}

public sealed class DependencyStatus
{
  public string Source { get; init; } = default!;     // "fk" | "declarada"
  public string Table { get; init; } = default!;
  public string Column { get; init; } = default!;
  public string References { get; init; } = default!; // "nodes" | "segments"
  public long RowsReferencing { get; init; }
  public string? Note { get; init; }
}

/// <summary>Resultado del diff + aplicación de sync.</summary>
public sealed class SyncStats
{
  public long NodesExisting { get; set; }
  public long NodesKeptExact { get; set; }
  public long NodesKeptTolerant { get; set; }
  public long NodesNew { get; set; }
  public long NodesRetired { get; set; }
  public long NodesRetained { get; set; }
  public long NodesUpdated { get; set; }

  public long SegmentsExisting { get; set; }
  public long SegmentsKeptExact { get; set; }
  public long SegmentsKeptTolerant { get; set; }
  public long SegmentsNew { get; set; }
  public long SegmentsRetired { get; set; }
  public long SegmentsRetained { get; set; }
  public long SegmentsUpdated { get; set; }

  public double RetiredSegmentsPercent => SegmentsExisting == 0 ? 0 : Math.Round(100.0 * SegmentsRetired / SegmentsExisting, 3);
}

/// <summary>Qué pasó con una tabla dependiente durante sync.</summary>
public sealed class DependencyOutcome
{
  public string Source { get; init; } = default!;
  public string Table { get; init; } = default!;
  public string Column { get; init; } = default!;
  public string References { get; init; } = default!;
  public RetiredStrategy Strategy { get; init; }
  public RetiredStrategy Fallback { get; init; }
  public double MaxDistanceMeters { get; init; }

  /// <summary>Filas que apuntaban a elementos retirados.</summary>
  public long Affected { get; set; }
  public long Remapped { get; set; }
  public long SetNull { get; set; }

  /// <summary>Quedan apuntando a un elemento que se conserva (FK real + report).</summary>
  public long Retained { get; set; }

  /// <summary>Quedan apuntando a un Id que ya no existe (referencia lógica + report).</summary>
  public long Dangling { get; set; }

  public List<string> SampleUnresolved { get; set; } = new();
}

public sealed class PublishResult
{
  public string Target { get; init; } = default!;
  public PublishMode Mode { get; init; }
  public bool DryRun { get; init; }
  public long NodesWritten { get; set; }
  public long SegmentsWritten { get; set; }
  public long PreviousNodes { get; set; }
  public long PreviousSegments { get; set; }
  public List<string> Backups { get; } = new();
  public List<DependencyStatus> Dependencies { get; } = new();
  public SyncStats? Sync { get; set; }
  public Dictionary<string, long> ColumnChanges { get; set; } = new();
  public List<DependencyOutcome> DependencyOutcomes { get; } = new();
  public List<string> Steps { get; } = new();
  public TimeSpan Elapsed { get; set; }
}

/// <summary>Fila de segmento tal como está hoy en el destino (para comparar / sync).</summary>
/// <remarks>Los atributos opcionales solo los llenan los motores que hacen el diff de atributos en memoria (MySQL).</remarks>
public readonly record struct ExistingSegment(
  long Id, long OsmId, string Highway, double X1, double Y1, double X2, double Y2, double LengthMeters,
  string? Name = null, bool IsOneWay = false, int? Lanes = null, int? MaxSpeed = null, double TravelCost = 0,
  long? FromNodeId = null, long? ToNodeId = null);

public readonly record struct ExistingNode(long Id, double Lon, double Lat, int Valence = 0);

public sealed class ExistingNetwork
{
  public List<ExistingNode> Nodes { get; init; } = default!;
  public List<ExistingSegment> Segments { get; init; } = default!;
}

/// <summary>Un adaptador por motor de base de datos.</summary>
public interface IPublisher : IAsyncDisposable
{
  /// <summary>Descripción segura del destino (servidor/base/tablas, sin credenciales).</summary>
  string Target { get; }

  Task<PublishResult> PublishAsync(GeoPackageReader gpkg, PublishMode mode, PublishFlags flags, CancellationToken ct = default);

  Task<ExistingNetwork> ReadExistingAsync(CancellationToken ct = default);

  /// <summary>Info del motor útil para diagnosticar paridad (versión, método de segmentize, etc.).</summary>
  Task<Dictionary<string, string>> DiagnosticsAsync(CancellationToken ct = default);
}

public sealed class DependentDataException : Exception
{
  public DependentDataException(string message) : base(message) { }
}

public static class PublisherFactory
{
  public static IPublisher Create(string connectionString, PublishOptions options) =>
    options.Provider.ToLowerInvariant() switch
    {
      "postgres" or "postgresql" or "postgis" => new Postgres.PostgresPublisher(connectionString, options),
      "mysql" or "mariadb" => new MySql.MySqlPublisher(connectionString, options),
      "sqlserver" or "mssql" => new SqlServer.SqlServerPublisher(connectionString, options),
      var p => throw new ArgumentException($"Proveedor desconocido: '{p}' (postgres | mysql | sqlserver)"),
    };
}
