using System.CommandLine;
using Tc3Build.Commands;

namespace Tc3Build;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var rootCommand = RootCommandFactory.Create();
        return rootCommand.Parse(args).Invoke();
    }
}
