namespace Tc3Build.Infrastructure;

internal sealed class BuildLogger
{
    public void Info(string message) => Write("INFO", ConsoleColor.Gray, message);

    public void Success(string message) => Write(" OK ", ConsoleColor.Green, message);

    public void Warning(string message) => Write("WARN", ConsoleColor.Yellow, message);

    public void Error(string message) => Write("ERROR", ConsoleColor.Red, message, Console.Error);

    private static void Write(string level, ConsoleColor color, string message, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        var previousColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        Console.ForegroundColor = previousColor;
    }
}
