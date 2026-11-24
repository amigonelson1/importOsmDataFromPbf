using System.Globalization;

namespace OsmImporter.Cli;

public sealed class UsageException(string message) : Exception(message);

public sealed class ParsedArgs
{
  public string? Command { get; init; }
  public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
  public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

  public string? Get(string name) => Values.GetValueOrDefault(name);

  public bool Flag(string name) => Flags.Contains(name);

  public double? GetDouble(string name)
  {
    if (Get(name) is not { } v) return null;
    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
      throw new UsageException($"--{name}: '{v}' no es un número válido (usa punto decimal)");
    return d;
  }
}

/// <summary>Parser mínimo: comando + "--clave valor" + "--flag".</summary>
public static class ArgParser
{
  /// <summary>Opciones que no llevan valor.</summary>
  private static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase)
  {
    "verbose", "legacy-buffer", "no-reverse-oneway", "include-areas", "coverage-only", "overwrite", "yes", "dry-run", "no-backup", "force",
  };

  public static ParsedArgs Parse(string[] args)
  {
    var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : null;
    var result = new ParsedArgs { Command = command };

    for (var i = command is null ? 0 : 1; i < args.Length; i++)
    {
      var a = args[i];
      if (a is "-h" or "--help") { result.Flags.Add("help"); continue; }
      if (!a.StartsWith("--")) throw new UsageException($"Argumento inesperado: '{a}'");

      var name = a[2..];
      string? inline = null;
      var eq = name.IndexOf('=');
      if (eq > 0) { inline = name[(eq + 1)..]; name = name[..eq]; }

      if (KnownFlags.Contains(name))
      {
        result.Flags.Add(name);
        continue;
      }

      var value = inline ?? (i + 1 < args.Length ? args[++i] : throw new UsageException($"--{name} requiere un valor"));
      result.Values[name] = value;
    }

    return result;
  }
}
