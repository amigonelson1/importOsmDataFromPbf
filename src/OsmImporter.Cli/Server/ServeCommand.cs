using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
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
public record UpdateDirectionApiRequest(string? File, List<long>? Fids, bool IsOneWay, bool ReverseDirection);
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
    var outputDir = ResolveDirectory("output", args.Get("dir"), "*.gpkg");
    var downloadsDir = ResolveDirectory("downloads", null, "*.pbf");
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

    // Verificar y liberar puerto si está ocupado por una instancia previa de osmimport
    var portOk = await EnsurePortAvailableAsync(port, ct);
    if (!portOk)
    {
      return 1;
    }

    // Configurar WebApplication
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders(); // Evitar ruido en la consola

    var app = builder.Build();
    app.Urls.Add($"http://127.0.0.1:{port}");

    // Servir archivos estáticos desde wwwroot (resolviendo la versión más reciente y evitando caché)
    string? wwwroot = ResolveWwwRoot();

    if (wwwroot != null)
    {
      app.UseDefaultFiles(new DefaultFilesOptions
      {
        FileProvider = new PhysicalFileProvider(wwwroot)
      });
      app.UseStaticFiles(new StaticFileOptions
      {
        FileProvider = new PhysicalFileProvider(wwwroot),
        OnPrepareResponse = ctx =>
        {
          ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
          ctx.Context.Response.Headers["Pragma"] = "no-cache";
          ctx.Context.Response.Headers["Expires"] = "0";
        }
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

    // Obtener tramo completo hasta esquinas para edición
    app.MapGet("/api/segments/block", (string? file, long? fid) =>
    {
      var path = ResolveGpkgPath(file);
      if (string.IsNullOrEmpty(path) || !File.Exists(path))
        return Results.NotFound(new { ok = false, error = "Archivo no encontrado" });

      if (fid == null || fid <= 0)
        return Results.BadRequest(new { ok = false, error = "Se requiere fid del segmento" });

      var block = GpkgSpatialService.GetStreetBlock(path, fid.Value);
      return Results.Ok(block);
    });

    // Actualizar sentido de tramo en GeoPackage
    app.MapPost("/api/segments/update-direction", (UpdateDirectionApiRequest req) =>
    {
      var path = ResolveGpkgPath(req.File);
      if (string.IsNullOrEmpty(path) || !File.Exists(path))
        return Results.NotFound(new { ok = false, error = "Archivo no encontrado" });

      if (req.Fids == null || req.Fids.Count == 0)
        return Results.BadRequest(new { ok = false, error = "Se requiere al menos un fid" });

      var result = GpkgSpatialService.UpdateSegmentDirection(path, req.Fids, req.IsOneWay, req.ReverseDirection);
      return Results.Ok(result);
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
      var userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
      var target = folder == "output" ? outputDir : (folder == "user-downloads" ? userDownloads : downloadsDir);
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
          var path = await Geofabrik.DownloadAsync(req.Region, downloadsDir, req.Force, CancellationToken.None);
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
      var candidateRoots = new List<string> { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
      for (int i = 1; i <= 3; i++)
      {
        var rel = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("..", i));
        candidateRoots.Add(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), rel)));
        candidateRoots.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, rel)));
      }

      var dirs = candidateRoots
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(r => Path.Combine(r, "profiles"))
        .Where(Directory.Exists)
        .ToList();

      var found = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
      foreach (var d in dirs)
      {
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

      var pbfCandidate = req.Pbf;
      var possiblePbfPaths = new[]
      {
        pbfCandidate,
        Path.Combine(downloadsDir, pbfCandidate),
        Path.Combine("downloads", pbfCandidate),
        Path.Combine(AppContext.BaseDirectory, "downloads", pbfCandidate),
        Path.Combine(AppContext.BaseDirectory, "..", "downloads", pbfCandidate),
        Path.Combine(Directory.GetCurrentDirectory(), "downloads", pbfCandidate),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", pbfCandidate)
      };
      var pbfPath = possiblePbfPaths.FirstOrDefault(File.Exists);
      if (string.IsNullOrEmpty(pbfPath))
      {
        return Results.BadRequest(new { error = $"No se encontró el archivo: {req.Pbf}" });
      }

      var (profile, _) = Profile.LoadOrDefault(req.Profile);
      var options = profile.Build;
      if (req.Bbox != null)
      {
        options.BBox = new BoundingBox(req.Bbox.MinLon, req.Bbox.MinLat, req.Bbox.MaxLon, req.Bbox.MaxLat);
      }
      if (req.BufferMeters.HasValue) options.BufferMeters = req.BufferMeters.Value;
      if (req.Highways != null && req.Highways.Count > 0) options.Highways = req.Highways;

      if (options.BBox == null)
      {
        return Results.BadRequest(new { error = "Se requiere delimitar un área geográfica (Bounding Box). Usa '✏️ Dibujar área' o '🎯 Capturar vista' en el mapa antes de iniciar la construcción." });
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
      context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
      context.Response.Headers["Pragma"] = "no-cache";
      context.Response.Headers["Expires"] = "0";

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
      if (ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
          ex.InnerException?.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) == true)
      {
        Log.Warn($"El puerto {port} fue ocupado mientras iniciaba. Reintentando verificar proceso...");
        if (await EnsurePortAvailableAsync(port, ct))
        {
          try
          {
            await app.StartAsync(ct);
            goto ServerStarted;
          }
          catch (Exception retryEx)
          {
            Log.Error($"No se pudo iniciar el servidor web en el puerto {port}: {retryEx.Message}");
            return 1;
          }
        }
      }

      Log.Error($"No se pudo iniciar el servidor web en el puerto {port}: {ex.Message}");
      return 1;
    }

ServerStarted:
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

  private static async Task<bool> EnsurePortAvailableAsync(int port, CancellationToken ct)
  {
    var (inUse, pid, processName) = CheckPortUsage(port);
    if (!inUse) return true;

    if (!pid.HasValue)
    {
      Log.Warn($"El puerto {port} parece estar en uso, pero no se pudo determinar el PID del proceso.");
      return true;
    }

    try
    {
      using var proc = Process.GetProcessById(pid.Value);
      if (IsOurApplication(proc))
      {
        Log.Warn($"El puerto {port} está ocupado por una instancia previa de osmimport (PID {pid}: '{proc.ProcessName}').");
        Log.Info("Liberando el puerto automáticamente para iniciar el visor web...");
        try
        {
          proc.Kill(entireProcessTree: true);
          using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
          await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch
        {
          // El proceso ya terminó
        }

        // Esperar brevemente a que el sistema operativo libere el socket TCP
        for (int i = 0; i < 20; i++)
        {
          await Task.Delay(100, ct);
          var (stillInUse, _, _) = CheckPortUsage(port);
          if (!stillInUse)
          {
            Log.Info($"Instancia anterior finalizada. Puerto {port} disponible.");
            return true;
          }
        }
        return true;
      }
      else
      {
        Log.Error($"No se puede iniciar el servidor web: el puerto {port} está ocupado por otro proceso del sistema: '{proc.ProcessName}' (PID {pid}).");
        Log.Error($"Por favor cierra esa aplicación o ejecuta osmimport serve con '--port <otro_puerto>'.");
        return false;
      }
    }
    catch (ArgumentException)
    {
      // El proceso ya no existe
      return true;
    }
    catch (Exception ex)
    {
      Log.Warn($"No se pudo verificar el proceso en el puerto {port}: {ex.Message}");
      return true;
    }
  }

  private static (bool InUse, int? Pid, string? ProcessName) CheckPortUsage(int port)
  {
    try
    {
      var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
      bool anyListener = listeners.Any(ep => ep.Port == port);
      if (!anyListener)
      {
        return (false, null, null);
      }

      int? pid = FindProcessIdUsingPort(port);
      if (pid.HasValue)
      {
        try
        {
          var p = Process.GetProcessById(pid.Value);
          return (true, pid, p.ProcessName);
        }
        catch
        {
          return (true, pid, null);
        }
      }

      return (true, null, null);
    }
    catch
    {
      return (false, null, null);
    }
  }

  private static int? FindProcessIdUsingPort(int port)
  {
    if (OperatingSystem.IsWindows())
    {
      return FindProcessIdUsingPortWindows(port);
    }
    else
    {
      return FindProcessIdUsingPortUnix(port);
    }
  }

  private static int? FindProcessIdUsingPortWindows(int port)
  {
    try
    {
      var psi = new ProcessStartInfo
      {
        FileName = "netstat.exe",
        Arguments = "-ano -p tcp",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };
      using var proc = Process.Start(psi);
      if (proc == null) return null;
      string output = proc.StandardOutput.ReadToEnd();
      proc.WaitForExit(3000);

      var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
      foreach (var line in lines)
      {
        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4 && parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase))
        {
          var localAddress = parts[1];
          var state = parts[parts.Length - 2];
          var pidStr = parts[parts.Length - 1];

          if (state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase) &&
              (localAddress.EndsWith($":{port}", StringComparison.OrdinalIgnoreCase) ||
               localAddress.EndsWith($"]:{port}", StringComparison.OrdinalIgnoreCase)))
          {
            if (int.TryParse(pidStr, out var pid) && pid > 0)
            {
              return pid;
            }
          }
        }
      }
    }
    catch
    {
    }
    return null;
  }

  private static int? FindProcessIdUsingPortUnix(int port)
  {
    try
    {
      var psi = new ProcessStartInfo
      {
        FileName = "lsof",
        Arguments = $"-t -i:{port}",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };
      using var proc = Process.Start(psi);
      if (proc == null) return null;
      string output = proc.StandardOutput.ReadToEnd().Trim();
      proc.WaitForExit(3000);

      var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
      if (lines.Length > 0 && int.TryParse(lines[0], out var pid) && pid > 0)
      {
        return pid;
      }
    }
    catch
    {
    }
    return null;
  }

  private static bool IsOurApplication(Process proc)
  {
    try
    {
      var currentProc = Process.GetCurrentProcess();
      if (proc.Id == currentProc.Id) return false;

      string name = proc.ProcessName.ToLowerInvariant();
      if (name.Contains("osmimport")) return true;

      if (name.Contains("dotnet"))
      {
        if (OperatingSystem.IsWindows())
        {
          var cmdLine = GetProcessCommandLineWindows(proc.Id);
          if (!string.IsNullOrEmpty(cmdLine) && cmdLine.Contains("osmimport", StringComparison.OrdinalIgnoreCase))
          {
            return true;
          }
        }
      }

      try
      {
        var mainModule = proc.MainModule?.FileName ?? "";
        if (mainModule.Contains("osmimport", StringComparison.OrdinalIgnoreCase))
          return true;
      }
      catch
      {
      }
    }
    catch
    {
    }
    return false;
  }

  private static string? GetProcessCommandLineWindows(int pid)
  {
    try
    {
      var psi = new ProcessStartInfo
      {
        FileName = "powershell.exe",
        Arguments = $"-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId = {pid}').CommandLine\"",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };
      using var p = Process.Start(psi);
      if (p == null) return null;
      string output = p.StandardOutput.ReadToEnd().Trim();
      p.WaitForExit(2000);
      return output;
    }
    catch
    {
      return null;
    }
  }

  private static string ResolveDirectory(string folderName, string? userSpecified = null, string? fileFilter = null)
  {
    if (!string.IsNullOrWhiteSpace(userSpecified))
    {
      var explicitPath = Path.GetFullPath(userSpecified);
      if (!Directory.Exists(explicitPath))
      {
        Directory.CreateDirectory(explicitPath);
      }
      return explicitPath;
    }

    var baseDir = AppContext.BaseDirectory;
    var currentDir = Directory.GetCurrentDirectory();

    var candidateRoots = new List<string> { currentDir, baseDir };
    for (int i = 1; i <= 3; i++)
    {
      var rel = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("..", i));
      candidateRoots.Add(Path.GetFullPath(Path.Combine(currentDir, rel)));
      candidateRoots.Add(Path.GetFullPath(Path.Combine(baseDir, rel)));
    }

    var distinctRoots = candidateRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    var candidateDirs = distinctRoots
      .Select(root => Path.Combine(root, folderName))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();

    // 1. Priorizar aquel directorio que exista y contenga archivos según el filtro (ej. *.gpkg, *.pbf)
    if (!string.IsNullOrEmpty(fileFilter))
    {
      foreach (var cand in candidateDirs)
      {
        if (Directory.Exists(cand))
        {
          try
          {
            if (Directory.EnumerateFiles(cand, fileFilter).Any())
            {
              return cand;
            }
          }
          catch { }
        }
      }
    }

    // 2. Si ninguno tiene archivos que coincidan, usar el primer directorio que exista físicamente
    foreach (var cand in candidateDirs)
    {
      if (Directory.Exists(cand))
      {
        return cand;
      }
    }

    // 3. Fallback: usar en el directorio de trabajo actual y crearlo si no existe
    var defaultDir = Path.GetFullPath(Path.Combine(currentDir, folderName));
    if (!Directory.Exists(defaultDir))
    {
      Directory.CreateDirectory(defaultDir);
    }
    return defaultDir;
  }

  private static string? ResolveWwwRoot()
  {
    var baseDir = AppContext.BaseDirectory;
    var currentDir = Directory.GetCurrentDirectory();

    var candidateRoots = new List<string> { currentDir, baseDir };
    for (int i = 1; i <= 3; i++)
    {
      var rel = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("..", i));
      candidateRoots.Add(Path.GetFullPath(Path.Combine(currentDir, rel)));
      candidateRoots.Add(Path.GetFullPath(Path.Combine(baseDir, rel)));
    }

    var candidates = new List<string>();
    foreach (var root in candidateRoots.Distinct(StringComparer.OrdinalIgnoreCase))
    {
      candidates.Add(Path.Combine(root, "src", "OsmImporter.Cli", "wwwroot"));
      candidates.Add(Path.Combine(root, "wwwroot"));
    }

    var valid = candidates
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Where(d => Directory.Exists(d) && File.Exists(Path.Combine(d, "index.html")))
      .ToList();

    if (valid.Count == 0) return null;

    // Si encontramos una versión más nueva de index.html (ej. editada en src/), sincronizar con baseDir/wwwroot si existe
    var best = valid
      .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "index.html")))
      .First();

    try
    {
      var baseWwwRoot = Path.Combine(baseDir, "wwwroot");
      if (Directory.Exists(baseWwwRoot) && !string.Equals(best, baseWwwRoot, StringComparison.OrdinalIgnoreCase))
      {
        var srcIndex = Path.Combine(best, "index.html");
        var destIndex = Path.Combine(baseWwwRoot, "index.html");
        if (File.Exists(srcIndex) && File.GetLastWriteTimeUtc(srcIndex) > File.GetLastWriteTimeUtc(destIndex))
        {
          File.Copy(srcIndex, destIndex, overwrite: true);
        }
      }
    }
    catch { }

    return best;
  }
}
