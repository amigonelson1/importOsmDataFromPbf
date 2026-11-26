using System.Globalization;
using OsmImporter.Core;
using OsmImporter.Core.Options;

namespace OsmImporter.Cli;

public static class Commands
{
  public static int Help()
  {
    Console.WriteLine($"""
      osmimport {BuildPipeline.ToolVersion} — pbf de OpenStreetMap → red vial ruteable

      USO
        osmimport build --pbf <archivo.osm.pbf> | --download <región> [opciones]
        osmimport publish --gpkg <archivo.gpkg> --mode create|replace|sync [opciones]
        osmimport compare --gpkg <archivo.gpkg>
        osmimport regions [--search texto]
        osmimport download --region <región> [--out carpeta] [--force]
        osmimport serve [--gpkg <archivo.gpkg>] [--port 5000] [--no-browser]

      BUILD: lee el pbf y genera un GeoPackage (.gpkg) con road_segments, road_nodes y build_info.
        --pbf <ruta>                 Archivo .osm.pbf (si falta, se pregunta)
        --download <región>          En vez de --pbf: baja (o reutiliza) el extracto de Geofabrik
                                     (id como 'argentina' o ruta como 'south-america/chile')
        --downloads-dir <carpeta>    Carpeta de descargas (default: downloads)
        --out <ruta.gpkg>            Salida (default: output/<pbf>_<fecha>.gpkg)
        --profile <nombre|ruta>      Perfil JSON (opcional; ej. sherpaslb). Sin perfil: valores genéricos + --bbox
        --bbox minLon,minLat,maxLon,maxLat
        --buffer-m <metros>          Buffer alrededor del bbox (default 20000)
        --legacy-buffer              Aplicar el buffer en EPSG:3857 como el SQL original
        --highways a,b,c             Tipos de vía a incluir
        --split-ways-at <unidades>   Partir vías largas cada N unidades EPSG:3857 y conservar solo los tramos
                                     que tocan el área, como osm2pgsql (default 100000; 0 = vía completa)
        --segmentize-m <metros>      Largo máximo de micro-segmento (default 50)
        --segmentize-method <m>      bisect (= ST_Segmentize de PostGIS 3.4, default) | uniform
        --min-segment-m <metros>     Descartar segmentos ≤ este largo (default 1)
        --snap-deg <grados>          Grilla para unificar nodos (default 0.00001)
        --no-reverse-oneway          No invertir vías oneway=-1
        --include-areas              Incluir vías cerradas con area=yes
        --coverage-only              Solo reportar cobertura de highway (no construye el grafo)
        --overwrite                  Sobrescribir la salida si existe
        --verbose                    Progreso detallado

      PUBLISH: escribe el GeoPackage en la base destino (según la sección "publish" del perfil).
        osmimport publish --gpkg <archivo.gpkg> --mode create|replace|sync [opciones]
        --mode create                Crea las tablas (no deben existir) y carga
        --mode replace               Respalda, vacía y recarga. Se NIEGA si hay filas que dependen de la red
                                     (FKs detectadas + dependencias declaradas en el perfil)
        --mode sync                  Diff por llave natural: conserva Ids de lo que no cambió, inserta lo nuevo,
                                     resuelve dependencias de lo retirado (perfil: remapNearest|setNull|report|block)
                                     y lo borra. Todo en una transacción.
        --max-retired-percent <n>    sync aborta si retiraría más de n% de los segmentos (default 20)
        --connection <cadena>        Si falta: variable OSMIMPORT_CONNECTION o se pide sin mostrarla
        --dry-run                    Muestra el plan y las dependencias, no modifica nada
        --no-backup                  replace sin tablas *_bak_<fecha>
        --yes                        No pedir confirmación
        --schema / --nodes-table / --segments-table / --provider   Sobrescriben el perfil

      REGIONS / DOWNLOAD: extractos de download.geofabrik.de
        osmimport regions --search argentina
        osmimport download --region argentina     → downloads/argentina-AAMMDD.osm.pbf (MD5 verificado;
                                                     si ya existe con el MD5 correcto, no se vuelve a bajar)
        --force                      Volver a descargar aunque exista

      COMPARE: cruza el GeoPackage con la red que ya está en la base (paridad, sin modificar nada).
        osmimport compare --gpkg <archivo.gpkg> [--connection ...] [--out reporte.json]

      SERVE: levanta un visor web interactivo en localhost para inspeccionar los GeoPackage (.gpkg).
        osmimport serve [--gpkg <archivo.gpkg>] [--port 5000] [--dir output] [--no-browser]
        --gpkg <archivo>             Archivo GeoPackage a abrir (opcional; si falta, lista los de output/)
        --port <puerto>              Puerto HTTP local (default 5000)
        --dir <carpeta>              Carpeta de búsqueda de .gpkg (default output)
        --no-browser                 No abrir el navegador automáticamente

      EJEMPLOS
        osmimport build --download argentina --profile sherpaslb
        osmimport build --pbf argentina-260528.osm.pbf --profile sherpaslb
        osmimport build --pbf argentina.osm.pbf --bbox -69.4,-39,-67.7,-33 --coverage-only
        osmimport build --pbf chile.osm.pbf --bbox -71.5,-34,-70,-32.5 --highways primary,secondary,tertiary
      """);
    return 0;
  }

  public static int Version()
  {
    Console.WriteLine(BuildPipeline.ToolVersion);
    return 0;
  }

  public static int Unknown(string command)
  {
    Log.Error($"Comando desconocido: '{command}'");
    Help();
    return 2;
  }

  public static int Build(ParsedArgs args, CancellationToken ct = default)
  {
    if (args.Flag("help")) return Help();

    var (profile, profilePath) = Profile.LoadOrDefault(args.Get("profile"));
    var options = profile.Build;
    ApplyOverrides(options, args);

    string pbf;
    if (args.Get("download") is { } region)
    {
      if (args.Get("pbf") is not null) throw new UsageException("Usa --pbf o --download, no ambos.");
      Log.Step($"Descargando '{region}' desde Geofabrik");
      pbf = OsmImporter.Core.Download.Geofabrik
        .DownloadAsync(region, args.Get("downloads-dir") ?? "downloads", args.Flag("force"), ct)
        .GetAwaiter().GetResult();
    }
    else
    {
      pbf = args.Get("pbf") ?? Prompt("Ruta del archivo .osm.pbf: ")
            ?? throw new UsageException("Falta --pbf (o --download <región>)");
      pbf = pbf.Trim('"');
    }

    var output = args.Get("out") ?? Path.Combine(
      "output",
      $"{Path.GetFileName(pbf).Replace(".osm.pbf", "", StringComparison.OrdinalIgnoreCase).Replace(".pbf", "")}_{DateTime.Now:yyyyMMdd-HHmm}.gpkg");

    if (!args.Flag("coverage-only") && File.Exists(output) && !args.Flag("overwrite"))
      throw new UsageException($"La salida ya existe: {output}. Usa --overwrite o --out.");

    options.Validate();
    Log.Step(profilePath is null ? "Sin perfil: valores genéricos" : $"Perfil: {profile.Name} ({profilePath})");
    Log.Info($"  bbox={options.BBox} buffer={options.BufferMeters} m{(options.LegacyBuffer ? " (legacy 3857)" : "")}");
    Log.Info($"  highways={string.Join(",", options.Highways)}");
    Log.Info($"  split={options.SplitWaysAt3857} segmentize={options.SegmentizeMeters} m ({options.SegmentizeMethod}) min={options.MinSegmentMeters} m snap={options.SnapDegrees.ToString(CultureInfo.InvariantCulture)}°");

    var report = BuildPipeline.Run(new BuildRequest
    {
      PbfPath = pbf,
      OutputPath = output,
      Options = options,
      ProfileName = profile.Name,
      CoverageOnly = args.Flag("coverage-only"),
    }, ct);

    Log.Step(args.Flag("coverage-only")
      ? $"Cobertura lista en {report.Timings["total"]}"
      : $"Listo en {report.Timings["total"]}: {report.Output}");
    if (!args.Flag("coverage-only"))
    {
      Log.Info($"  Archivos generados en: {Path.GetFullPath(Path.GetDirectoryName(report.Output)!)}");
      Log.Info("  (Nota: Puedes borrar los archivos de 'output' cuando desees; son artefactos generados)");
    }
    return 0;
  }

  private static void ApplyOverrides(BuildOptions o, ParsedArgs a)
  {
    if (a.Get("bbox") is { } bbox) o.BBox = BoundingBox.Parse(bbox);
    if (a.GetDouble("buffer-m") is { } buffer) o.BufferMeters = buffer;
    if (a.Flag("legacy-buffer")) o.LegacyBuffer = true;
    if (a.Get("highways") is { } hw) o.Highways = hw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    if (a.GetDouble("split-ways-at") is { } split) o.SplitWaysAt3857 = split;
    if (a.GetDouble("segmentize-m") is { } seg) o.SegmentizeMeters = seg;
    if (a.Get("segmentize-method") is { } method)
      o.SegmentizeMethod = Enum.TryParse<SegmentizeMethod>(method, true, out var m) ? m : throw new UsageException("--segmentize-method: uniform | bisect");
    if (a.GetDouble("min-segment-m") is { } min) o.MinSegmentMeters = min;
    if (a.GetDouble("snap-deg") is { } snap) o.SnapDegrees = snap;
    if (a.Flag("no-reverse-oneway")) o.ReverseOnewayMinusOne = false;
    if (a.Flag("include-areas")) o.ExcludeAreas = false;
  }

  private static string? Prompt(string label)
  {
    if (Console.IsInputRedirected) return null;
    Console.Write(label);
    var value = Console.ReadLine();
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
  }
}
