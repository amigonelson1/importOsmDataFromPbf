using System.Globalization;
using OsmImporter.Cli;
using OsmImporter.Core;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("es-CO");
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-CO");
Console.OutputEncoding = System.Text.Encoding.UTF8;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
  e.Cancel = true;
  Log.Warn("\nOperación cancelada por el usuario (Ctrl+C). Limpiando recursos...");
  cts.Cancel();
};

try
{
  var parsed = ArgParser.Parse(args);
  Log.Verbose = parsed.Flag("verbose");

  return parsed.Command switch
  {
    null or "help" or "--help" or "-h" => Commands.Help(),
    "build" => Commands.Build(parsed, cts.Token),
    "publish" => await PublishCommands.PublishAsync(parsed, cts.Token),
    "compare" => await PublishCommands.CompareAsync(parsed, cts.Token),
    "regions" => await DownloadCommands.RegionsAsync(parsed, cts.Token),
    "download" => await DownloadCommands.DownloadAsync(parsed, cts.Token),
    "version" or "--version" => Commands.Version(),
    _ => Commands.Unknown(parsed.Command),
  };
}
catch (OperationCanceledException)
{
  Log.Warn("Operación cancelada.");
  return 130;
}
catch (UsageException ex)
{
  Log.Error(ex.Message);
  Console.WriteLine("Usa 'osmimport help' para ver las opciones.");
  return 2;
}
catch (OsmImporter.Publish.DependentDataException ex)
{
  Log.Error(ex.Message);
  return 3;
}
catch (Npgsql.NpgsqlException ex)
{
  Log.Error($"Base de datos: {ex.Message}");
  return 1;
}
catch (HttpRequestException ex)
{
  Log.Error($"Descarga: {ex.Message}");
  return 1;
}
catch (Microsoft.Data.SqlClient.SqlException ex)
{
  Log.Error($"Base de datos (SQL Server): {ex.Message}");
  return 1;
}
catch (MySqlConnector.MySqlException ex)
{
  Log.Error($"Base de datos (MySQL): {ex.Message}");
  return 1;
}
catch (Exception ex) when (ex is ArgumentException or NotSupportedException or FileNotFoundException or FormatException or InvalidOperationException or InvalidDataException)
{
  Log.Error(ex.Message);
  return 1;
}
