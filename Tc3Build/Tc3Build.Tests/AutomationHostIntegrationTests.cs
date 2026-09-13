using System.Diagnostics;
using Tc3Build.Infrastructure;
using Xunit;

namespace Tc3Build.Tests;

[CollectionDefinition("TwinCAT automation", DisableParallelization = true)]
public sealed class TwinCatAutomationCollection
{
}

internal static class IntegrationHostTestSettings
{
    public static IEnumerable<object[]> ConfiguredHosts()
    {
        var configuredHosts = Environment.GetEnvironmentVariable("TC3BUILD_INTEGRATION_HOSTS");
        if (string.IsNullOrWhiteSpace(configuredHosts))
        {
            yield return ["__not_configured__"];
            yield break;
        }

        var registeredHosts = new List<string>();
        var hostFilter = Environment.GetEnvironmentVariable("TC3BUILD_INTEGRATION_HOST_FILTER");
        foreach (var selection in configuredHosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var host in AutomationHostCatalog.Resolve(selection))
            {
                if (!string.IsNullOrWhiteSpace(hostFilter) &&
                    !string.Equals(host.Key, hostFilter, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(host.ProgId, hostFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!IsRegistered(host.ProgId))
                    continue;

                if (!registeredHosts.Contains(host.Key, StringComparer.OrdinalIgnoreCase))
                    registeredHosts.Add(host.Key);
            }
        }

        if (registeredHosts.Count == 0)
        {
            yield return ["__not_configured__"];
            yield break;
        }

        foreach (var host in registeredHosts)
            yield return [host];
    }

    public static bool HasRegisteredConfiguredHost()
    {
        var configuredHosts = Environment.GetEnvironmentVariable("TC3BUILD_INTEGRATION_HOSTS");
        return !string.IsNullOrWhiteSpace(configuredHosts) &&
            configuredHosts
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(selection => AutomationHostCatalog.Resolve(selection))
                .Any(host => IsRegistered(host.ProgId));
    }

    private static bool IsRegistered(string progId) =>
        Type.GetTypeFromProgID(progId, throwOnError: false) is not null;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealIdeTheoryAttribute : TheoryAttribute
{
    public RealIdeTheoryAttribute(string? optInVariable = null)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TC3BUILD_RUN_INTEGRATION_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Real IDE integration tests are opt-in. Set TC3BUILD_RUN_INTEGRATION_TESTS=1.";
        }
        else if (!IntegrationHostTestSettings.HasRegisteredConfiguredHost())
        {
            Skip = "No configured automation host is registered on this machine.";
        }
        else if (!string.IsNullOrWhiteSpace(optInVariable) &&
                 !string.Equals(Environment.GetEnvironmentVariable(optInVariable), "1", StringComparison.Ordinal))
        {
            Skip = $"Set {optInVariable}=1 to enable this mutating integration test.";
        }
    }
}

[Collection("TwinCAT automation")]
[Trait("Category", "Integration")]
public sealed class AutomationHostIntegrationTests
{
    public static IEnumerable<object[]> ConfiguredHosts() => IntegrationHostTestSettings.ConfiguredHosts();

    [RealIdeTheory]
    [MemberData(nameof(ConfiguredHosts))]
    public void Build_command_runs_end_to_end_with_real_ide(string hostSelection)
    {
        RunTc3Build(hostSelection, "build");
    }

    [RealIdeTheory]
    [MemberData(nameof(ConfiguredHosts))]
    public void Validate_command_runs_end_to_end_with_real_ide(string hostSelection)
    {
        RunTc3Build(hostSelection, "validate");
    }

    [RealIdeTheory("TC3BUILD_INTEGRATION_ALLOW_ACTIVATE")]
    [MemberData(nameof(ConfiguredHosts))]
    public void Activate_command_runs_end_to_end_with_real_ide(string hostSelection)
    {
        RunTc3Build(hostSelection, "activate");
    }

    [RealIdeTheory("TC3BUILD_INTEGRATION_ALLOW_INSTALL_LIBRARY")]
    [MemberData(nameof(ConfiguredHosts))]
    public void Install_library_command_runs_end_to_end_with_real_ide(string hostSelection)
    {
        RunTc3Build(hostSelection, "install-library");
    }

    private static void RunTc3Build(string hostSelection, string command)
    {
        var libraryProjectPath = Environment.GetEnvironmentVariable("TC3BUILD_INTEGRATION_LIBRARY_PROJECT");
        var projectPath = command == "install-library" && !string.IsNullOrWhiteSpace(libraryProjectPath)
            ? libraryProjectPath
            : Environment.GetEnvironmentVariable("TC3BUILD_INTEGRATION_PROJECT");
        Assert.False(string.IsNullOrWhiteSpace(projectPath),
            "TC3BUILD_INTEGRATION_PROJECT must point to a TwinCAT .slnx/.sln or project file.");

        var executablePath = Environment.GetEnvironmentVariable("TC3BUILD_EXECUTABLE");
        executablePath = string.IsNullOrWhiteSpace(executablePath)
            ? Path.Combine(AppContext.BaseDirectory, "Tc3Build.exe")
            : Path.GetFullPath(executablePath);
        Assert.True(File.Exists(executablePath), $"Tc3Build executable was not found: {executablePath}");

        var isolatedProject = CreateIsolatedProject(projectPath!);
        var temporaryLibraryOutput = command == "install-library" &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TC3BUILD_INTEGRATION_LIBRARY_OUTPUT"))
            ? Path.Combine(Path.GetTempPath(), $"Tc3Build.Integration.{Guid.NewGuid():N}.library")
            : null;

        try
        {
            using var process = Process.Start(CreateStartInfo(
                executablePath,
                isolatedProject.ProjectPath,
                hostSelection,
                command,
                temporaryLibraryOutput));
            Assert.NotNull(process);

            var outputTask = process!.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var timeoutSeconds = ReadPositiveInt("TC3BUILD_INTEGRATION_TIMEOUT_SECONDS", 900);
            if (!process.WaitForExit(timeoutSeconds * 1000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10000);
                }
                catch (InvalidOperationException)
                {
                    // The process may have exited between the timeout and cleanup.
                }

                Assert.Fail($"Tc3Build timed out after {timeoutSeconds}s for {hostSelection}/{command}.");
            }

            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            Assert.True(
                process.ExitCode == 0,
                $"Tc3Build failed for {hostSelection}/{command} with exit code {process.ExitCode}." +
                Environment.NewLine + output + Environment.NewLine + error);

            if (temporaryLibraryOutput is not null)
            {
                Assert.True(
                    File.Exists(temporaryLibraryOutput),
                    $"The real install-library command did not create the expected file: {temporaryLibraryOutput}");
            }
        }
        finally
        {
            if (temporaryLibraryOutput is not null && File.Exists(temporaryLibraryOutput))
                File.Delete(temporaryLibraryOutput);
            DeleteDirectory(isolatedProject.RootPath);
        }
    }

    private static (string RootPath, string ProjectPath) CreateIsolatedProject(string projectPath)
    {
        var sourcePath = Path.GetFullPath(projectPath);
        var sourceRoot = Path.GetDirectoryName(sourcePath)!;
        var isolatedRoot = Path.Combine(
            Path.GetTempPath(),
            $"Tc3Build.Integration.Project.{Guid.NewGuid():N}");
        CopyDirectory(sourceRoot, isolatedRoot);
        return (isolatedRoot, Path.Combine(isolatedRoot, Path.GetFileName(sourcePath)));
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)));

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var directoryName = Path.GetFileName(directory);
            if (string.Equals(directoryName, ".vs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(directoryName, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(directoryName, "obj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyDirectory(directory, Path.Combine(destinationDirectory, directoryName));
        }
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // The IDE may still release a generated file shortly after exit.
        }
        catch (UnauthorizedAccessException)
        {
            // Do not hide the actual test result because cleanup was delayed.
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string projectPath,
        string hostSelection,
        string command,
        string? temporaryLibraryOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(hostSelection);

        var projectNameVariable = command == "install-library"
            ? "TC3BUILD_INTEGRATION_LIBRARY_PROJECT_NAME"
            : "TC3BUILD_INTEGRATION_PROJECT_NAME";
        AddArgumentFromEnvironment(startInfo, projectNameVariable, "--project-name");
        if (command == "install-library" &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(projectNameVariable)))
        {
            AddArgumentFromEnvironment(startInfo, "TC3BUILD_INTEGRATION_PROJECT_NAME", "--project-name");
        }
        AddArgumentFromEnvironment(startInfo, "TC3BUILD_INTEGRATION_CONFIGURATION", "--configuration");
        AddArgumentFromEnvironment(startInfo, "TC3BUILD_INTEGRATION_PLATFORM", "--platform");

        if (!string.Equals(Environment.GetEnvironmentVariable("TC3BUILD_INTEGRATION_SILENT"), "0", StringComparison.Ordinal))
            startInfo.ArgumentList.Add("--silent");

        if (command == "install-library")
        {
            if (temporaryLibraryOutput is not null)
            {
                startInfo.ArgumentList.Add("--library-output");
                startInfo.ArgumentList.Add(temporaryLibraryOutput);
            }
            else
            {
                AddArgumentFromEnvironment(startInfo, "TC3BUILD_INTEGRATION_LIBRARY_OUTPUT", "--library-output");
            }
        }

        if (command == "build" &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TC3BUILD_INTEGRATION_PROJECT_NAME")))
            startInfo.ArgumentList.Add("--all-projects");

        return startInfo;
    }

    private static void AddArgumentFromEnvironment(ProcessStartInfo startInfo, string variableName, string optionName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            startInfo.ArgumentList.Add(optionName);
            startInfo.ArgumentList.Add(value);
        }
    }

    private static int ReadPositiveInt(string variableName, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(variableName), out var value) && value > 0
            ? value
            : fallback;
    }
}
