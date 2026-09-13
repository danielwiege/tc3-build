using System.Diagnostics;

var hostAssembly = Path.Combine(AppContext.BaseDirectory, "Tc3Build.dll");
if (!File.Exists(hostAssembly))
{
    Console.Error.WriteLine($"Tc3Build host assembly was not found: {hostAssembly}");
    return 1;
}

var startInfo = new ProcessStartInfo
{
    FileName = "dotnet",
    UseShellExecute = false,
    WorkingDirectory = Environment.CurrentDirectory
};
startInfo.ArgumentList.Add(hostAssembly);
foreach (var argument in args)
    startInfo.ArgumentList.Add(argument);

using var process = Process.Start(startInfo);
if (process is null)
{
    Console.Error.WriteLine("Could not start the Tc3Build host process.");
    return 1;
}

process.WaitForExit();
return process.ExitCode;
