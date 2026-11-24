using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace OsmImporter.Core.Download;

public sealed record GeofabrikRegion(string Id, string? Parent, string Name, string PbfUrl);

/// <summary>
/// Descarga de extractos .osm.pbf desde download.geofabrik.de:
/// - La región se indica por id del índice oficial ("argentina", "chile") o por ruta ("south-america/argentina").
/// - "-latest" redirige al archivo con fecha (argentina-261116.osm.pbf): se guarda con ese nombre, así cada
///   descarga queda versionada y trazable en el GeoPackage (build_info.pbf_file).
/// - Se verifica el MD5 publicado; si el archivo ya existe con el MD5 correcto no se vuelve a bajar.
/// </summary>
public static class Geofabrik
{
  public const string BaseUrl = "https://download.geofabrik.de/";
  private const string IndexUrl = BaseUrl + "index-v1-nogeom.json";

  private static readonly HttpClient Http = CreateClient();

  private static HttpClient CreateClient()
  {
    var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("osmimport", BuildPipeline.ToolVersion));
    return client;
  }

  public static async Task<List<GeofabrikRegion>> ListRegionsAsync(CancellationToken ct = default)
  {
    using var response = await Http.GetAsync(IndexUrl, ct);
    response.EnsureSuccessStatusCode();
    await using var stream = await response.Content.ReadAsStreamAsync(ct);
    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

    var list = new List<GeofabrikRegion>();
    foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
    {
      var p = f.GetProperty("properties");
      if (!p.TryGetProperty("urls", out var urls) || !urls.TryGetProperty("pbf", out var pbf)) continue;
      list.Add(new GeofabrikRegion(
        p.GetProperty("id").GetString()!,
        p.TryGetProperty("parent", out var parent) ? parent.GetString() : null,
        p.GetProperty("name").GetString()!,
        pbf.GetString()!));
    }
    return list.OrderBy(r => r.Parent).ThenBy(r => r.Id).ToList();
  }

  /// <summary>Descarga (o reutiliza) el pbf de la región y devuelve la ruta local.</summary>
  public static async Task<string> DownloadAsync(string region, string outDir, bool force = false, CancellationToken ct = default)
  {
    var latestUrl = await ResolveLatestUrlAsync(region, ct);

    // -latest redirige al archivo con fecha: se usa ese nombre y su .md5
    var url = latestUrl;
    using (var head = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, latestUrl), ct))
    {
      if (head.StatusCode is HttpStatusCode.NotFound)
        throw new InvalidOperationException($"Geofabrik no tiene {latestUrl}");
      if ((int)head.StatusCode is >= 300 and < 400 && head.Headers.Location is { } location)
        url = new Uri(new Uri(latestUrl), location).ToString();
    }

    var fileName = Path.GetFileName(new Uri(url).LocalPath);
    var expectedMd5 = await TryGetMd5Async(url, ct) ?? await TryGetMd5Async(latestUrl, ct);
    Directory.CreateDirectory(outDir);
    var target = Path.Combine(outDir, fileName);

    if (File.Exists(target) && !force)
    {
      if (expectedMd5 is null)
      {
        Log.Info($"  ya existe {target} (Geofabrik no publicó MD5; usa --force para volver a bajarlo)");
        return target;
      }
      Log.Info($"  ya existe {fileName}, verificando MD5...");
      if (string.Equals(await Md5OfFileAsync(target, ct), expectedMd5, StringComparison.OrdinalIgnoreCase))
      {
        Log.Info("  MD5 correcto: se reutiliza el archivo descargado");
        return target;
      }
      Log.Warn("el archivo existente no coincide con el MD5 publicado: se vuelve a descargar");
    }

    var part = target + ".part";
    long existingBytes = 0;
    if (File.Exists(part))
    {
      existingBytes = new FileInfo(part).Length;
    }

    var request = new HttpRequestMessage(HttpMethod.Get, url);
    var isResuming = false;
    if (existingBytes > 0)
    {
      request.Headers.Range = new RangeHeaderValue(existingBytes, null);
      isResuming = true;
    }

    Log.Info(isResuming
      ? $"  reanudando descarga desde {existingBytes / 1048576.0:N0} MB: {url}"
      : $"  descargando {url}");

    using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
    {
      if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
      {
        Log.Warn("  rango no válido en el servidor: reiniciando descarga desde el inicio...");
        File.Delete(part);
        using var retryReq = new HttpRequestMessage(HttpMethod.Get, url);
        using var retryResp = await Http.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, ct);
        retryResp.EnsureSuccessStatusCode();
        await StreamDownloadAsync(retryResp, part, 0, expectedMd5, ct);
      }
      else
      {
        response.EnsureSuccessStatusCode();
        if (isResuming && response.StatusCode != HttpStatusCode.PartialContent)
        {
          Log.Info("  el servidor no soporta rango; descargando archivo completo...");
          existingBytes = 0;
        }

        await StreamDownloadAsync(response, part, existingBytes, expectedMd5, ct);
      }
    }

    File.Move(part, target, overwrite: true);
    return target;
  }

  private static async Task StreamDownloadAsync(HttpResponseMessage response, string part, long existingBytes, string? expectedMd5, CancellationToken ct)
  {
    var contentLength = response.Content.Headers.ContentLength;
    long? totalBytes = response.Content.Headers.ContentRange?.Length
      ?? (contentLength.HasValue ? existingBytes + contentLength.Value : null);

    await using var source = await response.Content.ReadAsStreamAsync(ct);
    await using var dest = new FileStream(part, existingBytes > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);

    var buffer = new byte[1 << 20];
    long done = existingBytes;
    var nextReport = totalBytes > 0 ? Math.Ceiling(((double)done / totalBytes.Value) * 10) / 10.0 : 0.1;
    if (nextReport <= 0) nextReport = 0.1;

    int read;
    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
    {
      await dest.WriteAsync(buffer.AsMemory(0, read), ct);
      done += read;
      if (totalBytes is > 0 && (double)done / totalBytes.Value >= nextReport)
      {
        Log.Info($"    {done / 1048576.0:N0} / {totalBytes.Value / 1048576.0:N0} MB ({100.0 * done / totalBytes.Value:N0}%)");
        nextReport += 0.1;
      }
    }

    await dest.FlushAsync(ct);
    dest.Close();

    if (expectedMd5 is not null)
    {
      var actual = await Md5OfFileAsync(part, ct);
      if (!string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase))
      {
        File.Delete(part);
        throw new InvalidDataException($"MD5 no coincide (esperado {expectedMd5}, obtenido {actual}): descarga corrupta, se descartó.");
      }
      Log.Info("  descarga completa, MD5 verificado");
    }
    else
    {
      Log.Info("  descarga completa (sin MD5 publicado para verificar)");
    }
  }

  private static async Task<string> ResolveLatestUrlAsync(string region, CancellationToken ct)
  {
    region = region.Trim().Trim('/');
    if (region.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return region;
    if (region.Contains('/')) return $"{BaseUrl}{region}-latest.osm.pbf";

    var regions = await ListRegionsAsync(ct);
    var match = regions.FirstOrDefault(r => string.Equals(r.Id, region, StringComparison.OrdinalIgnoreCase));
    if (match is not null) return match.PbfUrl;

    var suggestions = regions
      .Where(r => r.Id.Contains(region, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(region, StringComparison.OrdinalIgnoreCase))
      .Take(10).Select(r => $"{r.Id} ({r.Name})").ToList();
    throw new ArgumentException($"Región '{region}' no encontrada en Geofabrik." +
      (suggestions.Count > 0 ? " ¿Quisiste decir: " + string.Join(", ", suggestions) + "?" : " Usa 'osmimport regions --search <texto>'."));
  }

  private static async Task<string?> TryGetMd5Async(string pbfUrl, CancellationToken ct)
  {
    try
    {
      using var response = await Http.GetAsync(pbfUrl + ".md5", ct);
      if (!response.IsSuccessStatusCode) return null;
      var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
      var hash = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
      return hash is { Length: 32 } ? hash.ToLowerInvariant() : null;
    }
    catch (HttpRequestException)
    {
      return null;
    }
  }

  private static async Task<string> Md5OfFileAsync(string path, CancellationToken ct)
  {
    await using var stream = File.OpenRead(path);
    return Convert.ToHexString(await MD5.HashDataAsync(stream, ct)).ToLowerInvariant();
  }
}
