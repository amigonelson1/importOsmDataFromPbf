using System.Text;
using System.Text.Json;
using OsmImporter.Core;
using OsmImporter.Core.Output;
using OsmImporter.Publish;
using OsmImporter.Publish.Compare;
using Spectre.Console;

namespace OsmImporter.Cli;

public static class PublishCommands
{
  public const string ConnectionEnvVar = "OSMIMPORT_CONNECTION";

  public static async Task<int> PublishAsync(ParsedArgs args, CancellationToken ct = default)
  {
    if (args.Flag("help")) return Commands.Help();

    var (profile, _) = Profile.LoadOrDefault(args.Get("profile"));
    var options = profile.Publish;
    ApplyOverrides(options, args);
    options.Validate();

    var modeText = args.Get("mode") ?? throw new UsageException("Falta --mode (create | replace | sync)");
    if (!Enum.TryParse<PublishMode>(modeText, true, out var mode))
      throw new UsageException($"--mode '{modeText}' inválido. Opciones: create | replace | sync");

    using var gpkg = OpenGpkg(args);
    var connection = ResolveConnection(args);
    var flags = new PublishFlags
    {
      DryRun = args.Flag("dry-run"),
      Backup = !args.Flag("no-backup"),
      MaxRetiredPercent = args.GetDouble("max-retired-percent"),
    };

    await using var publisher = PublisherFactory.Create(connection, options);
    Log.Step($"Publicar {Path.GetFileName(gpkg.Path)} → {publisher.Target}");
    Log.Info($"  modo={mode}{(flags.DryRun ? " (dry-run: no se modifica nada)" : "")} respaldo={(flags.Backup ? "sí" : "no")}");
    Log.Info($"  origen: pbf={gpkg.Info.GetValueOrDefault("pbf_file")} perfil={gpkg.Info.GetValueOrDefault("profile")} creado={gpkg.Info.GetValueOrDefault("created_at_utc")}");

    // Sin --yes: primero una simulación (dry-run) para mostrar el plan/diff y pedir confirmación.
    // En sync la simulación ejecuta todo dentro de una transacción que se revierte, así que lo que se
    // muestra es exactamente lo que va a pasar. Con --yes en sync se salta (evita hacer el trabajo dos veces).
    if (!flags.DryRun && !(mode == PublishMode.Sync && args.Flag("yes")))
    {
      Log.Step("Simulación previa (no modifica nada)");
      var plan = await publisher.PublishAsync(gpkg, mode, new PublishFlags { DryRun = true, Backup = flags.Backup, MaxRetiredPercent = flags.MaxRetiredPercent }, ct);
      PrintResult(plan);
      if (plan.Steps.Any(s => s.StartsWith("BLOQUEADO"))) return 3;
      var verb = mode switch { PublishMode.Create => "crear y cargar", PublishMode.Replace => "REEMPLAZAR", _ => "SINCRONIZAR" };
      if (!Confirm(args, $"Se va a {verb} la red en {publisher.Target}."))
      {
        Log.Warn("Cancelado por el usuario. No se modificó nada.");
        return 4;
      }
    }

    var result = await publisher.PublishAsync(gpkg, mode, flags, ct);
    PrintResult(result);
    Log.Step(flags.DryRun ? "Dry-run terminado (sin cambios)" : $"Publicación completa en {result.Elapsed:hh\\:mm\\:ss}");
    return result.Steps.Any(s => s.StartsWith("BLOQUEADO")) ? 3 : 0;
  }

  public static async Task<int> CompareAsync(ParsedArgs args, CancellationToken ct = default)
  {
    if (args.Flag("help")) return Commands.Help();

    var (profile, _) = Profile.LoadOrDefault(args.Get("profile"));
    var options = profile.Publish;
    ApplyOverrides(options, args);
    options.Validate();

    using var gpkg = OpenGpkg(args);
    var connection = ResolveConnection(args);
    await using var publisher = PublisherFactory.Create(connection, options);

    Log.Step($"Comparar {Path.GetFileName(gpkg.Path)} ↔ {publisher.Target}");
    var diagnostics = await publisher.DiagnosticsAsync(ct);
    Log.Info($"  destino: {string.Join(", ", diagnostics.Select(kv => $"{kv.Key}={kv.Value}"))}");

    Log.Info("  leyendo red existente...");
    var existing = await publisher.ReadExistingAsync(ct);
    Log.Info($"  {existing.Segments.Count:N0} segmentos y {existing.Nodes.Count:N0} nodos en el destino");

    Log.Info("  cruzando por llave natural...");
    var report = ParityComparer.Compare(gpkg, existing, publisher.Target, diagnostics);
    PrintParity(report);

    var output = args.Get("out") ?? gpkg.Path + ".parity.json";
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, BuildReport.Json), ct);
    Log.Info($"Reporte: {Path.GetFullPath(output)}");
    return 0;
  }

  // ------------------------------------------------------------------ helpers

  private static GeoPackageReader OpenGpkg(ParsedArgs args)
  {
    var path = args.Get("gpkg") ?? Prompt("Ruta del GeoPackage (.gpkg): ") ?? throw new UsageException("Falta --gpkg");
    return new GeoPackageReader(path.Trim('"'));
  }

  private static void ApplyOverrides(PublishOptions o, ParsedArgs a)
  {
    if (a.Get("provider") is { } p) o.Provider = p;
    if (a.Get("schema") is { } s) o.Schema = s;
    if (a.Get("nodes-table") is { } nt) o.Nodes.Table = nt;
    if (a.Get("segments-table") is { } st) o.Segments.Table = st;
  }

  /// <summary>--connection, o variable de entorno OSMIMPORT_CONNECTION, o prompt oculto. Nunca se imprime.</summary>
  private static string ResolveConnection(ParsedArgs args)
  {
    if (args.Get("connection") is { Length: > 0 } c) return c;
    if (Environment.GetEnvironmentVariable(ConnectionEnvVar) is { Length: > 0 } env) return env;
    if (Console.IsInputRedirected)
      throw new UsageException($"Falta la cadena de conexión: usa --connection o la variable de entorno {ConnectionEnvVar}.");

    Console.Write("Cadena de conexión (no se muestra): ");
    var sb = new StringBuilder();
    while (true)
    {
      var key = Console.ReadKey(intercept: true);
      if (key.Key == ConsoleKey.Enter) break;
      if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
      sb.Append(key.KeyChar);
    }
    Console.WriteLine();
    return sb.Length > 0 ? sb.ToString() : throw new UsageException("Cadena de conexión vacía.");
  }

  private static bool Confirm(ParsedArgs args, string message)
  {
    if (args.Flag("yes")) return true;
    if (Console.IsInputRedirected)
      throw new UsageException("Se requiere confirmación: agrega --yes para ejecutar sin preguntar.");

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Write($"{message}\n¿Continuar? escribe 'si' para confirmar: ");
    Console.ResetColor();
    var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
    return answer is "si" or "sí" or "s" or "yes" or "y";
  }

  private static string? Prompt(string label)
  {
    if (Console.IsInputRedirected) return null;
    Console.Write(label);
    var v = Console.ReadLine();
    return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
  }

  private static void PrintResult(PublishResult r)
  {
    if (r.DryRun && r.Mode == PublishMode.Replace && (r.PreviousNodes > 0 || r.PreviousSegments > 0))
      Log.Info($"  red actual en el destino: {r.PreviousSegments:N0} segmentos, {r.PreviousNodes:N0} nodos");

    if (r.DryRun && r.Dependencies.Count > 0)
    {
      if (!Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Ansi)
      {
        var depTable = new Table().Border(TableBorder.Rounded).Title("[bold yellow]Dependencias hacia la red vial[/]");
        depTable.AddColumn(new TableColumn("[bold]Estado[/]").Centered());
        depTable.AddColumn("[bold]Tabla . Columna[/]");
        depTable.AddColumn("[bold]Referencia[/]");
        depTable.AddColumn(new TableColumn("[bold]Filas[/]").RightAligned());
        depTable.AddColumn("[bold]Origen / Nota[/]");
        foreach (var d in r.Dependencies)
        {
          var status = d.RowsReferencing > 0 ? "[red]✗ Afectada[/]" : "[green]✓ Limpia[/]";
          depTable.AddRow(status, $"{d.Table}.{d.Column}", d.References, $"{d.RowsReferencing:N0}", $"{d.Source}{(d.Note is null ? "" : " — " + d.Note)}");
        }
        AnsiConsole.Write(depTable);
      }
      else
      {
        Log.Info("  dependencias hacia la red:");
        foreach (var d in r.Dependencies)
          Console.WriteLine($"    {(d.RowsReferencing > 0 ? "✗" : "✓")} {d.Table}.{d.Column} → {d.References}: {d.RowsReferencing:N0} filas ({d.Source}){(d.Note is null ? "" : " — " + d.Note)}");
      }
    }

    if (r.Sync is { } st)
    {
      if (!Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Ansi)
      {
        var syncTable = new Table().Border(TableBorder.Rounded).Title("[bold cyan]Balance de Sincronización Topológica[/]");
        syncTable.AddColumn("[bold]Elemento[/]");
        syncTable.AddColumn(new TableColumn("[bold]Existentes[/]").RightAligned());
        syncTable.AddColumn(new TableColumn("[bold]Conservados[/]").RightAligned());
        syncTable.AddColumn(new TableColumn("[bold](±1 celda)[/]").RightAligned());
        syncTable.AddColumn(new TableColumn("[bold yellow]Actualizados[/]").RightAligned());
        syncTable.AddColumn(new TableColumn("[bold green]Nuevos[/]").RightAligned());
        syncTable.AddColumn(new TableColumn("[bold red]Retirados[/]").RightAligned());
        syncTable.AddColumn(new TableColumn("[bold blue]Retenidos[/]").RightAligned());

        syncTable.AddRow("Segmentos", $"{st.SegmentsExisting:N0}", $"{st.SegmentsKeptExact + st.SegmentsKeptTolerant:N0}", $"{st.SegmentsKeptTolerant:N0}", $"{st.SegmentsUpdated:N0}", $"{st.SegmentsNew:N0}", $"{st.SegmentsRetired - st.SegmentsRetained:N0}", $"{st.SegmentsRetained:N0}");
        syncTable.AddRow("Nodos", $"{st.NodesExisting:N0}", $"{st.NodesKeptExact + st.NodesKeptTolerant:N0}", $"{st.NodesKeptTolerant:N0}", $"{st.NodesUpdated:N0}", $"{st.NodesNew:N0}", $"{st.NodesRetired - st.NodesRetained:N0}", $"{st.NodesRetained:N0}");
        AnsiConsole.Write(syncTable);
      }
      else
      {
        Console.WriteLine();
        Console.WriteLine($"    {"",-12} {"existentes",12} {"conservados",12} {"(±1 celda)",12} {"actualizados",13} {"nuevos",10} {"retirados",10} {"retenidos",10}");
        Console.WriteLine($"    {"Segmentos",-12} {st.SegmentsExisting,12:N0} {st.SegmentsKeptExact + st.SegmentsKeptTolerant,12:N0} {st.SegmentsKeptTolerant,12:N0} {st.SegmentsUpdated,13:N0} {st.SegmentsNew,10:N0} {st.SegmentsRetired - st.SegmentsRetained,10:N0} {st.SegmentsRetained,10:N0}");
        Console.WriteLine($"    {"Nodos",-12} {st.NodesExisting,12:N0} {st.NodesKeptExact + st.NodesKeptTolerant,12:N0} {st.NodesKeptTolerant,12:N0} {st.NodesUpdated,13:N0} {st.NodesNew,10:N0} {st.NodesRetired - st.NodesRetained,10:N0} {st.NodesRetained,10:N0}");
        Console.WriteLine();
      }

      if (r.DependencyOutcomes.Count > 0)
      {
        Log.Info("  dependencias:");
        foreach (var d in r.DependencyOutcomes)
        {
          var detail = d.Affected == 0
            ? "sin filas afectadas"
            : $"{d.Affected:N0} afectadas → reasignadas {d.Remapped:N0}, null {d.SetNull:N0}, retenidas {d.Retained:N0}, colgando {d.Dangling:N0}";
          Console.WriteLine($"    {(d.Affected == d.Remapped + d.SetNull ? "✓" : "!")} {d.Table}.{d.Column} → {d.References} [{d.Strategy}/{d.Fallback} ≤{d.MaxDistanceMeters} m]: {detail}");
          if (d.SampleUnresolved.Count > 0) Console.WriteLine($"        sin resolver (ej.): {string.Join(", ", d.SampleUnresolved.Take(10))}");
        }
      }
    }

    if (r.DryRun)
    {
      Log.Info("  plan:");
      foreach (var s in r.Steps) Console.WriteLine($"    • {s}");
    }
    else
    {
      Log.Info($"  escritos: {r.NodesWritten:N0} nodos, {r.SegmentsWritten:N0} segmentos");
      foreach (var b in r.Backups) Log.Info($"  respaldo: {b}");
    }
  }

  private static void PrintParity(ParityReport r)
  {
    Log.Info($"  segmentize: destino={r.Diagnostics.GetValueOrDefault("segmentize_method")} gpkg={r.GpkgSegmentizeMethod}");
    if (r.Diagnostics.GetValueOrDefault("segmentize_method") is { } m && r.GpkgSegmentizeMethod is { } g && !m.Equals(g, StringComparison.OrdinalIgnoreCase))
      Log.Warn($"el método de segmentize difiere: reconstruye con --segmentize-method {m} para comparar en igualdad de condiciones.");

    if (!Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Ansi)
    {
      var table = new Table().Border(TableBorder.Rounded).Title("[bold green]Reporte de Paridad Geoespacial[/]");
      table.AddColumn("[bold]Métrica[/]");
      table.AddColumn(new TableColumn("[bold]Destino[/]").RightAligned());
      table.AddColumn(new TableColumn("[bold]GPKG[/]").RightAligned());
      table.AddColumn(new TableColumn("[bold]Coinciden[/]").RightAligned());
      table.AddColumn(new TableColumn("[bold]% Coincidencia[/]").RightAligned());

      table.AddRow("Nodos", $"{r.DbNodes:N0}", $"{r.GpkgNodes:N0}", $"{r.NodesMatched:N0}", $"[green]{r.NodesMatchedPercentOfDb}%[/]");
      table.AddRow("Segmentos (exactos)", $"{r.DbSegments:N0}", $"{r.GpkgSegments:N0}", $"{r.SegmentsMatched:N0}", "-");
      table.AddRow("Segmentos (±1 celda)", "-", "-", $"{r.SegmentsMatchedWithinOneCell:N0}", "-");
      table.AddRow("[bold]Total segmentos coincidentes[/]", $"{r.DbSegments:N0}", $"{r.GpkgSegments:N0}", $"[bold]{r.SegmentsMatched + r.SegmentsMatchedWithinOneCell:N0}[/]", $"[bold green]{r.SegmentsMatchedPercentOfDb}%[/]");
      table.AddRow("Vías OSM", $"{r.DbOsmWays:N0}", $"{r.GpkgOsmWays:N0}", $"Solo dest: {r.OsmWaysOnlyInDb:N0}", $"Solo gpkg: {r.OsmWaysOnlyInGpkg:N0}");
      table.AddRow("Kilómetros de red", $"{r.DbKm:N1} km", $"{r.GpkgKm:N1} km", "-", "-");
      AnsiConsole.Write(table);

      if (r.ByHighway.Count > 0)
      {
        var hwTable = new Table().Border(TableBorder.Simple).Title("[bold]Desglose por Highway[/]");
        hwTable.AddColumn("[bold cyan]Highway[/]");
        hwTable.AddColumn(new TableColumn("[bold]Seg Destino[/]").RightAligned());
        hwTable.AddColumn(new TableColumn("[bold]Seg GPKG[/]").RightAligned());
        hwTable.AddColumn(new TableColumn("[bold]Km Destino[/]").RightAligned());
        hwTable.AddColumn(new TableColumn("[bold]Km GPKG[/]").RightAligned());
        foreach (var h in r.ByHighway)
          hwTable.AddRow(h.Highway, $"{h.DbSegments:N0}", $"{h.GpkgSegments:N0}", $"{h.DbKm:N1}", $"{h.GpkgKm:N1}");
        AnsiConsole.Write(hwTable);
      }
    }
    else
    {
      Console.WriteLine();
      Console.WriteLine($"    {"",-28} {"destino",14} {"gpkg",14} {"coinciden",14}");
      Console.WriteLine($"    {"Nodos",-28} {r.DbNodes,14:N0} {r.GpkgNodes,14:N0} {r.NodesMatched,14:N0}  ({r.NodesMatchedPercentOfDb}% del destino)");
      Console.WriteLine($"    {"Segmentos (exactos)",-28} {r.DbSegments,14:N0} {r.GpkgSegments,14:N0} {r.SegmentsMatched,14:N0}");
      Console.WriteLine($"    {"Segmentos (±1 celda)",-28} {"",14} {"",14} {r.SegmentsMatchedWithinOneCell,14:N0}");
      Console.WriteLine($"    {"Total segmentos coincidentes",-28} {"",14} {"",14} {r.SegmentsMatched + r.SegmentsMatchedWithinOneCell,14:N0}  ({r.SegmentsMatchedPercentOfDb}% del destino, {r.SegmentsMatchedPercentOfGpkg}% del gpkg)");
      Console.WriteLine($"    {"Vías OSM",-28} {r.DbOsmWays,14:N0} {r.GpkgOsmWays,14:N0}  solo destino={r.OsmWaysOnlyInDb:N0} solo gpkg={r.OsmWaysOnlyInGpkg:N0}");
      Console.WriteLine($"    {"Km",-28} {r.DbKm,14:N1} {r.GpkgKm,14:N1}");
      Console.WriteLine();
      Console.WriteLine($"    {"highway",-16} {"seg destino",12} {"seg gpkg",12} {"km destino",12} {"km gpkg",12}");
      foreach (var h in r.ByHighway)
        Console.WriteLine($"    {h.Highway,-16} {h.DbSegments,12:N0} {h.GpkgSegments,12:N0} {h.DbKm,12:N1} {h.GpkgKm,12:N1}");
      Console.WriteLine();
    }
  }
}
