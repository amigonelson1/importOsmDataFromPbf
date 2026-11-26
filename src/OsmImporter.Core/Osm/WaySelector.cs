using System.Collections.Concurrent;
using OsmImporter.Core.Geo;
using OsmImporter.Core.Options;

namespace OsmImporter.Core.Osm;

public sealed class SelectedWay
{
  public required WayRecord Way { get; init; }
  public required (double Lon, double Lat)[] Coords { get; init; }
  public int PieceOrdinal { get; init; }
}

public sealed class HighwayCoverage
{
  public string Highway { get; init; } = default!;
  public long Ways { get; set; }
  public double Km { get; set; }
  public bool Selected { get; init; }
}

public sealed class SelectionResult
{
  public required List<SelectedWay> Ways { get; init; }

  /// <summary>Cobertura de TODOS los valores de highway dentro del área (paso 8 de getDataOSM.txt).</summary>
  public required List<HighwayCoverage> Coverage { get; init; }

  public long WaysWithMissingNodes { get; init; }
  public long WaysWithoutGeometry { get; init; }
  public long WaysExcludedAsArea { get; init; }
}

/// <summary>Filtra por área operativa y tipo de vía, y calcula la cobertura de highway en paralelo y de forma determinista.</summary>
public static class WaySelector
{
  public static SelectionResult Select(PbfReadResult read, BuildOptions options, OperationalArea area)
  {
    var wanted = new HashSet<string>(options.Highways, StringComparer.OrdinalIgnoreCase);

    var partitioner = Partitioner.Create(0, read.Ways.Count);
    var localSelected = new ConcurrentBag<List<SelectedWay>>();
    var localCoverages = new ConcurrentBag<Dictionary<string, HighwayCoverage>>();
    long totalMissing = 0, totalNoGeometry = 0, totalExcludedArea = 0;

    Parallel.ForEach(partitioner, range =>
    {
      var threadSelected = new List<SelectedWay>(range.Item2 - range.Item1);
      var threadCoverage = new Dictionary<string, HighwayCoverage>(StringComparer.Ordinal);
      long missing = 0, noGeometry = 0, excludedArea = 0;
      var buffer = new List<(double Lon, double Lat)>(256);

      for (var idx = range.Item1; idx < range.Item2; idx++)
      {
        var way = read.Ways[idx];
        buffer.Clear();
        var lost = false;
        foreach (var id in way.Refs)
        {
          if (read.TryGetCoordinate(id, out var lon, out var lat)) buffer.Add((lon, lat));
          else lost = true;
        }

        if (lost) missing++;
        if (buffer.Count < 2) { noGeometry++; continue; }
        if (!area.Intersects(buffer)) continue; // descarte rápido: ninguna parte de la vía toca el área

        // Tramos (como filas de planet_osm_line) que tocan el área
        var pieces = WaySplitter.Split(buffer.ToArray(), options.SplitWaysAt3857);
        if (pieces.Count > 1) pieces = pieces.Where(p => area.Intersects(p)).ToList();
        if (pieces.Count == 0) continue;

        var isWanted = wanted.Contains(way.Highway);
        if (!threadCoverage.TryGetValue(way.Highway, out var cov))
          threadCoverage[way.Highway] = cov = new HighwayCoverage { Highway = way.Highway, Selected = isWanted };

        cov.Ways++;
        foreach (var piece in pieces)
          for (var i = 1; i < piece.Length; i++)
            cov.Km += Geodesy.SphereDistance(piece[i - 1].Lon, piece[i - 1].Lat, piece[i].Lon, piece[i].Lat) / 1000.0;

        if (!isWanted) continue;

        var closed = way.Refs.Length > 2 && way.Refs[0] == way.Refs[^1];
        if (options.ExcludeAreas && closed && way.AreaYes) { excludedArea++; continue; }

        for (var pIdx = 0; pIdx < pieces.Count; pIdx++)
          threadSelected.Add(new SelectedWay { Way = way, Coords = pieces[pIdx], PieceOrdinal = pIdx });
      }

      Interlocked.Add(ref totalMissing, missing);
      Interlocked.Add(ref totalNoGeometry, noGeometry);
      Interlocked.Add(ref totalExcludedArea, excludedArea);
      localSelected.Add(threadSelected);
      localCoverages.Add(threadCoverage);
    });

    // Consolidar cobertura agregada
    var consolidatedCoverage = new Dictionary<string, HighwayCoverage>(StringComparer.Ordinal);
    foreach (var covMap in localCoverages)
    {
      foreach (var (hw, cov) in covMap)
      {
        if (!consolidatedCoverage.TryGetValue(hw, out var totalCov))
          consolidatedCoverage[hw] = totalCov = new HighwayCoverage { Highway = hw, Selected = cov.Selected };
        totalCov.Ways += cov.Ways;
        totalCov.Km += cov.Km;
      }
    }

    // Consolidar selección ordenada de forma 100% determinista (por Way.Id y PieceOrdinal)
    var totalSelectedCount = localSelected.Sum(l => l.Count);
    var selected = new List<SelectedWay>(totalSelectedCount);
    foreach (var list in localSelected)
      selected.AddRange(list);

    selected.Sort((a, b) =>
    {
      var cmp = a.Way.Id.CompareTo(b.Way.Id);
      return cmp != 0 ? cmp : a.PieceOrdinal.CompareTo(b.PieceOrdinal);
    });

    return new SelectionResult
    {
      Ways = selected,
      Coverage = consolidatedCoverage.Values
        .OrderByDescending(c => c.Ways)
        .ThenBy(c => c.Highway, StringComparer.Ordinal)
        .ToList(),
      WaysWithMissingNodes = totalMissing,
      WaysWithoutGeometry = totalNoGeometry,
      WaysExcludedAsArea = totalExcludedArea,
    };
  }
}
