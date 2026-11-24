using OsmImporter.Core;
using OsmImporter.Core.Download;

namespace OsmImporter.Cli;

public static class DownloadCommands
{
  /// <summary>osmimport regions [--search texto]</summary>
  public static async Task<int> RegionsAsync(ParsedArgs args, CancellationToken ct = default)
  {
    var search = args.Get("search");
    var regions = await Geofabrik.ListRegionsAsync(ct);
    var filtered = regions
      .Where(r => search is null
                  || r.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                  || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                  || (r.Parent?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
      .ToList();

    Console.WriteLine($"    {"id",-34} {"nombre",-36} {"dentro de",-20}");
    foreach (var r in filtered)
      Console.WriteLine($"    {r.Id,-34} {r.Name,-36} {r.Parent ?? "-",-20}");
    Log.Info($"{filtered.Count} de {regions.Count} regiones. Uso: osmimport download --region <id>  |  osmimport build --download <id> ...");
    return 0;
  }

  /// <summary>osmimport download --region id|ruta [--out carpeta] [--force]</summary>
  public static async Task<int> DownloadAsync(ParsedArgs args, CancellationToken ct = default)
  {
    var region = args.Get("region") ?? throw new UsageException("Falta --region (ej. argentina, south-america/chile). Ver 'osmimport regions'.");
    Log.Step($"Descargando '{region}' desde Geofabrik");
    var path = await Geofabrik.DownloadAsync(region, args.Get("out") ?? "downloads", args.Flag("force"), ct);
    Log.Step($"Listo: {Path.GetFullPath(path)}");
    return 0;
  }
}
