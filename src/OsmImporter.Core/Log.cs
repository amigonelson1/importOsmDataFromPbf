using System.Diagnostics;

namespace OsmImporter.Core;

public static class Log
{
  private static readonly Stopwatch Clock = Stopwatch.StartNew();

  public static bool Verbose { get; set; }

  public static void Info(string message) => Write(message, null);

  public static void Debug(string message)
  {
    if (Verbose) Write(message, ConsoleColor.DarkGray);
  }

  public static void Warn(string message) => Write("ADVERTENCIA: " + message, ConsoleColor.Yellow);

  public static void Error(string message) => Write("ERROR: " + message, ConsoleColor.Red);

  public static void Step(string message) => Write("▶ " + message, ConsoleColor.Cyan);

  private static void Write(string message, ConsoleColor? color)
  {
    var prefix = $"[{Clock.Elapsed:hh\\:mm\\:ss}] ";
    if (color is { } c) Console.ForegroundColor = c;
    Console.WriteLine(prefix + message);
    if (color is not null) Console.ResetColor();
  }
}
