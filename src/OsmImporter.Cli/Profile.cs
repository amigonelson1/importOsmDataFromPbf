using System.Text.Json;
using System.Text.Json.Serialization;
using OsmImporter.Core.Options;
using OsmImporter.Publish;

namespace OsmImporter.Cli;

/// <summary>
/// Perfil JSON reutilizable: "build" (cómo construir la red) y "publish" (dónde y con qué nombres
/// escribirla, y qué tablas dependen de ella). Nunca contiene cadenas de conexión.
/// </summary>
public sealed class Profile
{
  public string? Name { get; set; }
  public string? Description { get; set; }
  public BuildOptions Build { get; set; } = new();
  public PublishOptions Publish { get; set; } = new();

  public static readonly JsonSerializerOptions Json = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
  };

  /// <summary>Sin nombre → perfil vacío con valores genéricos (bbox debe venir por --bbox).</summary>
  public static (Profile Profile, string? Path) LoadOrDefault(string? nameOrPath)
  {
    if (string.IsNullOrWhiteSpace(nameOrPath)) return (new Profile { Name = "(sin perfil)" }, null);
    var (p, path) = Load(nameOrPath);
    return (p, path);
  }

  /// <summary>Busca el perfil por ruta, o por nombre en la carpeta profiles/ junto al ejecutable o en el directorio actual.</summary>
  public static (Profile Profile, string Path) Load(string nameOrPath)
  {
    var candidates = new List<string> { nameOrPath };
    if (!nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    {
      candidates.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "profiles", nameOrPath + ".json"));
      candidates.Add(System.IO.Path.Combine(Environment.CurrentDirectory, "profiles", nameOrPath + ".json"));
    }

    var path = candidates.FirstOrDefault(File.Exists)
               ?? throw new UsageException($"No se encontró el perfil '{nameOrPath}'. Buscado en:\n  " + string.Join("\n  ", candidates));

    var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Json)
                  ?? throw new UsageException($"Perfil vacío: {path}");
    profile.Name ??= System.IO.Path.GetFileNameWithoutExtension(path);
    profile.Build ??= new BuildOptions();
    profile.Publish ??= new PublishOptions();
    return (profile, System.IO.Path.GetFullPath(path));
  }
}
