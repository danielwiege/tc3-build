using System.CommandLine;
using Tc3Build.Commands;
using Tc3Build.Infrastructure;

namespace Tc3Build;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ComMessageFilter.Register();
        try
        {
            var rootCommand = RootCommandFactory.Create();
            return rootCommand.Parse(args).Invoke();
        }
        finally
        {
            ComMessageFilter.Revoke();
        }
    }
}
