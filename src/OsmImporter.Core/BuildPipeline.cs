using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OsmImporter.Core.Geo;
using OsmImporter.Core.Graph;
using OsmImporter.Core.Options;
using OsmImporter.Core.Osm;
using OsmImporter.Core.Output;

namespace OsmImporter.Core;

public sealed class BuildRequest
{
  public string PbfPath { get; init; } = default!;
  public string OutputPath { get; init; } = default!;
  public BuildOptions Options { get; init; } = default!;
  public string? ProfileName { get; init; }
  public bool CoverageOnly { get; init; }
}

public sealed class BuildReport
{
  public string ToolVersion { get; init; } = default!;
  public DateTime CreatedAtUtc { get; init; }
  public string? Profile { get; init; }
  public string PbfFile { get; init; } = default!;
  public long PbfBytes { get; init; }
  public string? PbfSha256 { get; set; }
  public string? Output { get; set; }
  public BuildOptions Options { get; init; } = default!;
  public double Buffer3857Applied { get; set; }

  public long WaysScanned { get; set; }
  public long HighwayWays { get; set; }
  public long NodesScanned { get; set; }
  public long NodesRequired { get; set; }
  public long NodesFound { get; set; }
  public long WaysWithMissingNodes { get; set; }
  public long WaysWithoutGeometry { get; set; }
  public long WaysExcludedAsArea { get; set; }
  public long WaysSelected { get; set; }

  public List<HighwayCoverage> Coverage { get; set; } = new();
  public GraphBuildStats? Graph { get; set; }
  public GraphQuality? Quality { get; set; }
  public Dictionary<string, string> Timings { get; } = new();

  public static readonly JsonSerializerOptions Json = new()
  {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
  };
}

public static class BuildPipeline
{
  public static string ToolVersion =>
    typeof(BuildPipeline).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

  public static BuildReport Run(BuildRequest req, CancellationToken ct = default)
  {
    var options = req.Options;
    options.Validate();
    ct.ThrowIfCancellationRequested();

    var pbf = new FileInfo(req.PbfPath);
    if (!pbf.Exists) throw new FileNotFoundException($"No existe el archivo pbf: {pbf.FullName}");

    var report = new BuildReport
    {
      ToolVersion = ToolVersion,
      CreatedAtUtc = DateTime.UtcNow,
      Profile = req.ProfileName,
      PbfFile = pbf.Name,
      PbfBytes = pbf.Length,
      Options = options,
    };
    var total = Stopwatch.StartNew();
    var sw = Stopwatch.StartNew();
    void Mark(string stage) { report.Timings[stage] = sw.Elapsed.ToString(@"hh\:mm\:ss"); sw.Restart(); }

    var area = new OperationalArea(options);
    report.Buffer3857Applied = Math.Round(area.Buffer3857, 1);

    Log.Step($"Leyendo {pbf.Name} ({pbf.Length / 1024.0 / 1024.0:N0} MB)");
    var hashTask = Task.Run(() => Sha256(pbf.FullName), ct);
    var read = PbfRoadReader.Read(pbf.FullName, ct);
    report.WaysScanned = read.TotalWaysScanned;
    report.HighwayWays = read.Ways.Count;
    report.NodesScanned = read.TotalNodesScanned;
    report.NodesRequired = read.NodeIds.Length;
    report.NodesFound = read.NodesFound;
    Mark("read");

    ct.ThrowIfCancellationRequested();
    Log.Step("Filtrando por área operativa y tipo de vía");
    var selection = WaySelector.Select(read, options, area);
    report.Coverage = selection.Coverage;
    report.WaysWithMissingNodes = selection.WaysWithMissingNodes;
    report.WaysWithoutGeometry = selection.WaysWithoutGeometry;
    report.WaysExcludedAsArea = selection.WaysExcludedAsArea;
    report.WaysSelected = selection.Ways.Count;
    Mark("select");

    PrintCoverage(selection);
    if (selection.WaysWithMissingNodes > 0)
      Log.Warn($"{selection.WaysWithMissingNodes:N0} vías referencian nodos que no están en el pbf (normal en recortes regionales, se usan los nodos disponibles).");

    if (!req.CoverageOnly)
    {
      if (selection.Ways.Count == 0)
        throw new InvalidOperationException("No hay vías seleccionadas: revisa bbox, buffer y highways.");

      ct.ThrowIfCancellationRequested();
      Log.Step("Construyendo grafo");
      var (graph, stats) = GraphBuilder.Build(selection.Ways, options, read.Restrictions, read);
      read = null!; // liberar memoria de la lectura tras extraer coordenadas de restricciones
      selection = null!;
      report.Graph = stats;
      if (graph.Restrictions.Count > 0)
        Log.Info($"  {graph.Restrictions.Count:N0} restricciones de giro incluidas");
      Log.Info($"  {stats.CandidateSegments:N0} micro-segmentos candidatos → {graph.Segments.Count:N0} segmentos, {graph.Nodes.Count:N0} nodos " +
               $"(descartados: {stats.DroppedShort:N0} cortos, {stats.DroppedLoops:N0} lazos)");
      Mark("graph");

      report.Quality = GraphAnalysis.Analyze(graph);
      PrintQuality(report.Quality);
      Mark("analyze");

      report.PbfSha256 = hashTask.Result;
      report.Output = Path.GetFullPath(req.OutputPath);

      ct.ThrowIfCancellationRequested();
      Log.Step($"Escribiendo {report.Output}");
      GeoPackageWriter.Write(req.OutputPath, graph, BuildInfo(report), ct);
      Mark("write");
    }
    else
    {
      report.PbfSha256 = hashTask.Result;
    }

    report.Timings["total"] = total.Elapsed.ToString(@"hh\:mm\:ss");

    var reportPath = req.CoverageOnly
      ? Path.ChangeExtension(req.OutputPath, ".coverage.json")
      : req.OutputPath + ".report.json";
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, BuildReport.Json));
    Log.Info($"Reporte: {Path.GetFullPath(reportPath)}");

    return report;
  }

  private static Dictionary<string, string> BuildInfo(BuildReport r) => new()
  {
    ["format"] = "osm-road-graph",
    ["format_version"] = "1",
    ["tool_version"] = r.ToolVersion,
    ["created_at_utc"] = r.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
    ["profile"] = r.Profile ?? "",
    ["pbf_file"] = r.PbfFile,
    ["pbf_bytes"] = r.PbfBytes.ToString(CultureInfo.InvariantCulture),
    ["pbf_sha256"] = r.PbfSha256 ?? "",
    ["snap_degrees"] = r.Options.SnapDegrees.ToString("R", CultureInfo.InvariantCulture),
    ["node_key"] = "GridKey.Pack(round(lon/snap), round(lat/snap)) = ((ix + 2^30) << 31) | (iy + 2^30)",
    ["segment_key"] = "osm_id:from_node_key:to_node_key[#n]",
    ["options_json"] = JsonSerializer.Serialize(r.Options, BuildReport.Json),
    ["quality_json"] = JsonSerializer.Serialize(r.Quality, BuildReport.Json),
    ["graph_stats_json"] = JsonSerializer.Serialize(r.Graph, BuildReport.Json),
  };

  private static void PrintCoverage(SelectionResult selection)
  {
    Log.Info("  Cobertura de highway en el área (* = seleccionado):");
    Console.WriteLine($"      {"highway",-22} {"vías",12} {"km",12}");
    foreach (var c in selection.Coverage.Take(30))
      Console.WriteLine($"    {(c.Selected ? "*" : " ")} {c.Highway,-22} {c.Ways,12:N0} {c.Km,12:N1}");
    if (selection.Coverage.Count > 30)
      Console.WriteLine($"      ... y {selection.Coverage.Count - 30} tipos más (ver reporte JSON)");
    Log.Info($"  Vías seleccionadas: {selection.Ways.Select(w => w.Way.Id).Distinct().Count():N0} ({selection.Ways.Count:N0} tramos)");
  }

  private static void PrintQuality(GraphQuality q)
  {
    Log.Info($"  Red: {q.Segments:N0} segmentos, {q.Nodes:N0} nodos, {q.TotalKm:N1} km");
    Log.Info($"  Validaciones (deben dar 0): sin nodos={q.SegmentsWithoutNodes}, lazos={q.SelfLoops}, nodos huérfanos={q.OrphanNodes}");
    Log.Info($"  Valence: {string.Join(", ", q.ValenceHistogram.Select(kv => $"{kv.Key}→{kv.Value:N0}"))}");
    Log.Info($"  Componentes conexas: {q.ConnectedComponents:N0} (la mayor tiene el {q.LargestComponentNodePercent}% de los nodos)");
  }

  private static string Sha256(string path)
  {
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
  }
}
