using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OsmImporter.Core;
using OsmImporter.Core.Download;
using OsmImporter.Core.Options;
using Spectre.Console;

namespace OsmImporter.Cli.Server;

public record DownloadApiRequest(string? Region, bool Force = false);
public record BboxDto(double MinLon, double MinLat, double MaxLon, double MaxLat);
public record BuildApiRequest(
  string? Pbf,
  string? Profile,
  string? OutName,
  BboxDto? Bbox,
  double? BufferMeters,
  List<string>? Highways);

public sealed class JobState
{
  public string Id { get; set; } = Guid.NewGuid().ToString();
  public string Type { get; set; } = ""; // "download" o "build"
  public string Title { get; set; } = "";
  public string Status { get; set; } = "idle"; // "idle", "running", "completed", "error"
  public string CurrentStep { get; set; } = "";
  public string? ErrorMessage { get; set; }
  public DateTime StartedAt { get; set; } = DateTime.UtcNow;
  public string? ResultFile { get; set; }
}

public static class ServeCommand
{
  private static readonly ConcurrentQueue<string> LogBuffer = new();
  private static readonly JobState CurrentJob = new() { Status = "idle" };
  private static readonly object JobLock = new();

  static ServeCommand()
  {
    Log.OnLog += line =>
    {
      LogBuffer.Enqueue(line);
      while (LogBuffer.Count > 150) LogBuffer.TryDequeue(out _);
      lock (JobLock)
      {
        if (CurrentJob.Status == "running")
        {
          CurrentJob.CurrentStep = line;
        }
      }
    };
  }
  public static async Task<int> ExecuteAsync(ParsedArgs args, CancellationToken ct)
  {
    var port = args.GetInt("port") ?? 5000;
    var outputDir = Path.GetFullPath(args.Get("dir") ?? "output");
    var noBrowser = args.Flag("no-browser");
    var explicitGpkg = args.Get("gpkg");

    if (!Directory.Exists(outputDir))
    {
      Directory.CreateDirectory(outputDir);
    }

    var available = GpkgSpatialService.ListFiles(outputDir);
    string? selectedGpkg = null;

    if (!string.IsNullOrWhiteSpace(explicitGpkg))
    {
      selectedGpkg = Path.GetFullPath(explicitGpkg);
      if (!File.Exists(selectedGpkg))
      {
        Log.Error($"No se encontró el archivo GeoPackage especificado: {explicitGpkg}");
        return 1;
      }
    }
    else if (available.Count > 0)
    {
      selectedGpkg = available[0].FullPath;
    }

    // Configurar WebApplication
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders(); // Evitar ruido en la consola

    var app = builder.Build();
    app.Urls.Add($"http://127.0.0.1:{port}");

    // Servir archivos estáticos desde wwwroot (ubicación local en src o carpeta dist)
    var baseDir = AppContext.BaseDirectory;
    var possibleWwwRoots = new[]
    {
      Path.Combine(baseDir, "wwwroot"),
      Path.Combine(Directory.GetCurrentDirectory(), "src", "OsmImporter.Cli", "wwwroot"),
      Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")
    };

    string? wwwroot = possibleWwwRoots.FirstOrDefault(Directory.Exists);

    if (wwwroot != null)
    {
      app.UseDefaultFiles(new DefaultFilesOptions
      {
        FileProvider = new PhysicalFileProvider(wwwroot)
      });
      app.UseStaticFiles(new StaticFileOptions
      {
        FileProvider = new PhysicalFileProvider(wwwroot)
      });
    }

    string ResolveGpkgPath(string? fileNameOrPath)
    {
      if (string.IsNullOrWhiteSpace(fileNameOrPath))
      {
        return selectedGpkg ?? "";
      }
      if (File.Exists(fileNameOrPath)) return Path.GetFullPath(fileNameOrPath);

      var combined = Path.Combine(outputDir, fileNameOrPath);
      if (File.Exists(combined)) return combined;

      return fileNameOrPath;
    }

    // Endpoints API
    app.MapGet("/api/files", () =>
    {
      var files = GpkgSpatialService.ListFiles(outputDir);
      return Results.Ok(new
      {
        outputDir,
        current = selectedGpkg != null ? Path.GetFileName(selectedGpkg) : null,
        files
      });
    });

    app.MapGet("/api/info", (string? file) =>
    {
      var path = ResolveGpkgPath(file);
      if (string.IsNullOrEmpty(path) || !File.Exists(path))
        return Results.NotFound(new { error = "Archivo no encontrado" });

      var meta = GpkgSpatialService.GetMetadata(path);
      return meta != null ? Results.Ok(meta) : Results.NotFound();
    });

    app.MapGet("/api/segments", (
      string? file,
      double? minLon,
      double? minLat,
      double? maxLon,
      double? maxLat,
      double? zoom,
      string? highways,
      int? limit) =>
    {
      var path = ResolveGpkgPath(file);
      if (string.IsNullOrEmpty(path) || !File.Exists(path))
        return Results.NotFound(new { error = "Archivo no encontrado" });

      if (minLon == null || minLat == null || maxLon == null || maxLat == null)
        return Results.BadRequest(new { error = "Se requieren minLon, minLat, maxLon, maxLat" });

      var geoJson = GpkgSpatialService.GetSegmentsGeoJson(
        path,
        minLon.Value,
        minLat.Value,
        maxLon.Value,
        maxLat.Value,
        zoom ?? 12,
        highways,
        limit ?? 10000);

      return Results.Ok(geoJson);
    });

    app.MapGet("/api/nodes", (
      string? file,
      double? minLon,
      double? minLat,
      double? maxLon,
      double? maxLat,
      int? limit) =>
    {
      var path = ResolveGpkgPath(file);
      if (string.IsNullOrEmpty(path) || !File.Exists(path))
        return Results.NotFound(new { error = "Archivo no encontrado" });

      if (minLon == null || minLat == null || maxLon == null || maxLat == null)
        return Results.BadRequest(new { error = "Se requieren minLon, minLat, maxLon, maxLat" });

      var geoJson = GpkgSpatialService.GetNodesGeoJson(
        path,
        minLon.Value,
        minLat.Value,
        maxLon.Value,
        maxLat.Value,
        limit ?? 3000);

      return Results.Ok(geoJson);
    });

    // Geofabrik: Buscar regiones
    app.MapGet("/api/regions", async (string? search, CancellationToken cancellationToken) =>
    {
      try
      {
        var regions = await Geofabrik.ListRegionsAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(search))
        {
          regions = regions.Where(r =>
            r.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            (r.Parent != null && r.Parent.Contains(search, StringComparison.OrdinalIgnoreCase))
          ).ToList();
        }
        return Results.Ok(regions.Take(50));
      }
      catch (Exception ex)
      {
        return Results.Problem(ex.Message);
      }
    });

    // Estado del trabajo activo (Descarga o Build)
    app.MapGet("/api/job/status", () =>
    {
      lock (JobLock)
      {
        return Results.Ok(new
        {
          active = CurrentJob.Status == "running",
          job = CurrentJob
        });
      }
    });

    // Abrir carpeta en el Explorador de Windows
    app.MapPost("/api/open-folder", (string? folder) =>
    {
      var target = folder == "output" ? outputDir : Path.GetFullPath("downloads");
      if (!Directory.Exists(target)) Directory.CreateDirectory(target);
      try
      {
        Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        return Results.Ok(new { path = target });
      }
      catch (Exception ex)
      {
        return Results.Problem(ex.Message);
      }
    });

    // Archivos descargados localmente (.osm.pbf)
    app.MapGet("/api/downloads", () =>
    {
      var downloadsDir = Path.GetFullPath("downloads");
      if (!Directory.Exists(downloadsDir)) Directory.CreateDirectory(downloadsDir);

      var allFiles = new List<object>();
      var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      void AddFrom(string dir, string folderLabel)
      {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.pbf"))
        {
          var fi = new FileInfo(f);
          if (seenNames.Add(fi.Name))
          {
            allFiles.Add(new
            {
              fileName = fi.Name,
              fullPath = fi.FullName,
              sizeMb = Math.Round((double)fi.Length / (1024 * 1024), 1),
              lastModified = fi.LastWriteTimeUtc,
              folderLabel
            });
          }
        }
      }

      AddFrom(downloadsDir, "downloads (proyecto)");
      var userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
      if (Directory.Exists(userDownloads) && !string.Equals(downloadsDir, userDownloads, StringComparison.OrdinalIgnoreCase))
      {
        AddFrom(userDownloads, "Descargas (Windows)");
      }

      return Results.Ok(new
      {
        downloadsDir,
        userDownloadsDir = userDownloads,
        files = allFiles
      });
    });

    // Descargar región desde Geofabrik
    app.MapPost("/api/download", (DownloadApiRequest req) =>
    {
      if (string.IsNullOrWhiteSpace(req.Region))
        return Results.BadRequest(new { error = "Se requiere el ID de la región (ej. argentina)" });

      lock (JobLock)
      {
        if (CurrentJob.Status == "running")
          return Results.Conflict(new { error = $"Ya hay una operación en curso: {CurrentJob.Title}" });

        CurrentJob.Id = Guid.NewGuid().ToString();
        CurrentJob.Type = "download";
        CurrentJob.Title = $"Descargando '{req.Region}' desde Geofabrik";
        CurrentJob.Status = "running";
        CurrentJob.CurrentStep = "Iniciando conexión con Geofabrik...";
        CurrentJob.ErrorMessage = null;
        CurrentJob.ResultFile = null;
        CurrentJob.StartedAt = DateTime.UtcNow;
      }

      _ = Task.Run(async () =>
      {
        try
        {
          Log.Step($"Descarga iniciada desde la WebUI para '{req.Region}'");
          var path = await Geofabrik.DownloadAsync(req.Region, "downloads", req.Force, CancellationToken.None);
          Log.Step($"Descarga completada con éxito: {path}");
          lock (JobLock)
          {
            CurrentJob.Status = "completed";
            CurrentJob.CurrentStep = $"Descarga lista: {Path.GetFileName(path)}";
            CurrentJob.ResultFile = Path.GetFileName(path);
          }
        }
        catch (Exception ex)
        {
          Log.Error($"Error en la descarga de '{req.Region}': {ex.Message}");
          lock (JobLock)
          {
            CurrentJob.Status = "error";
            CurrentJob.ErrorMessage = ex.Message;
          }
        }
      });

      return Results.Ok(new { status = "started", region = req.Region });
    });

    // Listar perfiles disponibles
    app.MapGet("/api/profiles", () =>
    {
      var dirs = new[]
      {
        Path.Combine(AppContext.BaseDirectory, "profiles"),
        Path.Combine(Directory.GetCurrentDirectory(), "profiles")
      };
      var found = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
      foreach (var d in dirs)
      {
        if (!Directory.Exists(d)) continue;
        foreach (var f in Directory.GetFiles(d, "*.json"))
        {
          var name = Path.GetFileNameWithoutExtension(f);
          if (!found.ContainsKey(name))
          {
            try
            {
              var (p, _) = Profile.Load(f);
              found[name] = new
              {
                name = p.Name,
                description = p.Description,
                bbox = p.Build.BBox != null ? new
                {
                  minLon = p.Build.BBox.MinLon,
                  minLat = p.Build.BBox.MinLat,
                  maxLon = p.Build.BBox.MaxLon,
                  maxLat = p.Build.BBox.MaxLat
                } : null,
                highways = p.Build.Highways,
                bufferMeters = p.Build.BufferMeters
              };
            }
            catch { }
          }
        }
      }
      return Results.Ok(found.Values);
    });

    // Construir red vial (Build)
    app.MapPost("/api/build", (BuildApiRequest req) =>
    {
      if (string.IsNullOrWhiteSpace(req.Pbf))
        return Results.BadRequest(new { error = "Se requiere la ruta o nombre del archivo .pbf" });

      var pbfPath = req.Pbf;
      if (!File.Exists(pbfPath))
      {
        var inDownloads = Path.Combine("downloads", pbfPath);
        if (File.Exists(inDownloads)) pbfPath = inDownloads;
        else
        {
          var userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", pbfPath);
          if (File.Exists(userDownloads)) pbfPath = userDownloads;
          else return Results.BadRequest(new { error = $"No se encontró el archivo: {req.Pbf}" });
        }
      }

      lock (JobLock)
      {
        if (CurrentJob.Status == "running")
          return Results.Conflict(new { error = $"Ya hay una operación en curso: {CurrentJob.Title}" });

        CurrentJob.Id = Guid.NewGuid().ToString();
        CurrentJob.Type = "build";
        CurrentJob.Title = $"Construyendo red vial desde '{Path.GetFileName(pbfPath)}'";
        CurrentJob.Status = "running";
        CurrentJob.CurrentStep = "Inicializando pipeline de construcción...";
        CurrentJob.ErrorMessage = null;
        CurrentJob.ResultFile = null;
        CurrentJob.StartedAt = DateTime.UtcNow;
      }

      var (profile, _) = Profile.LoadOrDefault(req.Profile);
      var options = profile.Build;
      if (req.Bbox != null)
      {
        options.BBox = new BoundingBox(req.Bbox.MinLon, req.Bbox.MinLat, req.Bbox.MaxLon, req.Bbox.MaxLat);
      }
      if (req.BufferMeters.HasValue) options.BufferMeters = req.BufferMeters.Value;
      if (req.Highways != null && req.Highways.Count > 0) options.Highways = req.Highways;

      var outName = req.OutName;
      if (string.IsNullOrWhiteSpace(outName))
      {
        var baseName = Path.GetFileNameWithoutExtension(pbfPath).Replace(".osm", "");
        outName = $"{baseName}_{DateTime.Now:yyyyMMdd-HHmm}.gpkg";
      }
      if (!outName.EndsWith(".gpkg", StringComparison.OrdinalIgnoreCase)) outName += ".gpkg";
      var outPath = Path.Combine(outputDir, outName);

      _ = Task.Run(() =>
      {
        try
        {
          Log.Step($"Iniciando build desde WebUI: {pbfPath} → {outPath}");
          options.Validate();
          var report = BuildPipeline.Run(new BuildRequest
          {
            PbfPath = pbfPath,
            OutputPath = outPath,
            Options = options,
            ProfileName = profile.Name,
            CoverageOnly = false
          }, CancellationToken.None);
          Log.Step($"Build finalizado con éxito: {outPath} en {report.Timings["total"]}");
          lock (JobLock)
          {
            CurrentJob.Status = "completed";
            CurrentJob.CurrentStep = $"Listo en {report.Timings["total"]}";
            CurrentJob.ResultFile = Path.GetFileName(outPath);
          }
        }
        catch (Exception ex)
        {
          Log.Error($"Error durante el build: {ex.Message}");
          lock (JobLock)
          {
            CurrentJob.Status = "error";
            CurrentJob.ErrorMessage = ex.Message;
          }
        }
      });

      return Results.Ok(new { status = "started", output = outName });
    });

    // Logs en vivo
    app.MapGet("/api/logs", () => Results.Ok(LogBuffer.ToArray()));

    app.MapFallback(async (HttpContext context) =>
    {
      if (wwwroot != null)
      {
        var indexPath = Path.Combine(wwwroot, "index.html");
        if (File.Exists(indexPath))
        {
          context.Response.ContentType = "text/html; charset=utf-8";
          await context.Response.SendFileAsync(indexPath);
          return;
        }
      }

      context.Response.ContentType = "text/html; charset=utf-8";
      await context.Response.WriteAsync("<h1>osmimport WebUI</h1><p>No se encontró index.html en wwwroot.</p>");
    });

    var url = $"http://127.0.0.1:{port}";

    try
    {
      await app.StartAsync(ct);
    }
    catch (Exception ex)
    {
      Log.Error($"No se pudo iniciar el servidor web en el puerto {port}: {ex.Message}");
      return 1;
    }

    var rule = new Rule("[bold green]osmimport — Visor Web Activo[/]").LeftJustified();
    AnsiConsole.Write(rule);
    AnsiConsole.MarkupLine($"[bold]URL:[/]        [link={url}]{url}[/]");
    AnsiConsole.MarkupLine($"[bold]Directorio:[/] [cyan]{outputDir}[/]");
    if (selectedGpkg != null)
      AnsiConsole.MarkupLine($"[bold]Archivo:[/]    [yellow]{Path.GetFileName(selectedGpkg)}[/]");
    else
      AnsiConsole.MarkupLine($"[bold]Archivo:[/]    [dim](No se encontraron archivos .gpkg en {outputDir})[/]");

    AnsiConsole.MarkupLine($"[dim]💡 Tip: Los archivos en '{outputDir}' son derivados del pipeline y pueden borrarse para liberar espacio.[/]");
    AnsiConsole.MarkupLine("\n[dim]Presiona Ctrl+C para detener el servidor.[/]\n");

    if (!noBrowser)
    {
      try
      {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
      }
      catch
      {
        // Ignorar si el sistema no puede abrir el navegador automáticamente
      }
    }

    try
    {
      await Task.Delay(Timeout.Infinite, ct);
    }
    catch (OperationCanceledException)
    {
      // Finalización normal al recibir Ctrl+C
    }

    AnsiConsole.MarkupLine("[yellow]Deteniendo servidor web...[/]");
    await app.StopAsync(CancellationToken.None);
    return 0;
  }
}
