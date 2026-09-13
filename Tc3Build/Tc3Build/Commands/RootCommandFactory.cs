using System.CommandLine;
using Tc3Build.Infrastructure;
using Tc3Build.Options;

namespace Tc3Build.Commands;

internal static class RootCommandFactory
{
    public static RootCommand Create(Func<BuildOptions, int>? executor = null)
    {
        executor ??= options => new TwinCatBuildService().Execute(options);
        var rootCommand = new RootCommand("Build, validate, activate, and install TwinCAT 3 projects.");
        rootCommand.Subcommands.Add(CreateCommand(
            "build",
            "Build a selected TwinCAT project or all projects in a solution.\n\n" +
            "Examples:\n" +
            "  Tc3Build.exe build -p D:\\Git\\tc3-build\\twincat\\Project\\Project.slnx -n Project -c Debug -t \"TwinCAT RT (x64)\"\n" +
            "  Tc3Build.exe build -p D:\\Git\\tc3-build\\twincat\\Project\\Project\\Project.tsproj -s\n" +
            "  Tc3Build.exe build -p D:\\Git\\tc3-build\\twincat\\Project\\Project.slnx -A\n" +
            "  Tc3Build.exe build -p D:\\Git\\tc3-build\\twincat\\Project\\Project.slnx -n Project --host vs2019",
            executeBuild: true,
            activate: false,
            installLibrary: false,
            includeAllProjects: true,
            includeLibraryOutput: false,
            executor));
        rootCommand.Subcommands.Add(CreateCommand(
            "validate",
            "Validate a TwinCAT solution or selected project without building.\n\n" +
            "Examples:\n" +
            "  Tc3Build.exe validate -p D:\\Git\\tc3-build\\twincat\\Project\\Project.slnx -n Project\n" +
            "  Tc3Build.exe validate -p D:\\Git\\tc3-build\\twincat\\Project\\Project\\Project.tsproj -s\n" +
            "  Tc3Build.exe validate -p D:\\Git\\tc3-build\\twincat\\Project\\Project.slnx --host xae2019",
            executeBuild: false,
            activate: false,
            installLibrary: false,
            includeAllProjects: false,
            includeLibraryOutput: false,
            executor));
        rootCommand.Subcommands.Add(CreateCommand(
            "activate",
            "Activate an already built TwinCAT configuration.\n\n" +
            "Examples:\n" +
            "  Tc3Build.exe activate -p D:\\Git\\tc3-build\\twincat\\Project\\Project.slnx -c Debug -t \"TwinCAT RT (x64)\"\n" +
            "  Tc3Build.exe activate -p D:\\Git\\tc3-build\\twincat\\Project\\Project.slnx -s\n" +
            "  Tc3Build.exe activate -p D:\\Git\\tc3-build\\twincat\\Project\\Project.slnx --host vs2019",
            executeBuild: false,
            activate: true,
            installLibrary: false,
            includeAllProjects: false,
            includeLibraryOutput: false,
            executor));
        rootCommand.Subcommands.Add(CreateCommand(
            "install-library",
            "Build and install a selected TwinCAT PLC library.\n\n" +
            "Examples:\n" +
            "  Tc3Build.exe install-library -p D:\\Git\\tc3-build\\twincat\\Project\\Project.slnx -n Library -c Debug -t \"TwinCAT RT (x64)\" -s\n" +
            "  Tc3Build.exe install-library -p D:\\Git\\tc3-build\\twincat\\Project\\Library\\Library.tspproj -o D:\\Build\\Library.library -s\n" +
            "  Tc3Build.exe install-library -p D:\\Git\\tc3-build\\twincat\\Project\\Library\\Library.tspproj --host xae2019",
            executeBuild: true,
            activate: false,
            installLibrary: true,
            includeAllProjects: false,
            includeLibraryOutput: true,
            executor));
        return rootCommand;
    }

    private static Command CreateCommand(
        string name,
        string description,
        bool executeBuild,
        bool activate,
        bool installLibrary,
        bool includeAllProjects,
        bool includeLibraryOutput,
        Func<BuildOptions, int> executor)
    {
        var command = new Command(name, description);
        var projectOption = new Option<string>("--project", "-p")
        {
            Description = "Path to a .slnx/.sln solution or a .tsproj/.tspproj/.plcproj project. Example: -p .\\twincat\\Project\\Project.slnx",
            Required = true
        };
        var targetProjectOption = new Option<string?>("--project-name", "-n")
        {
            Description = "Project name or relative path inside a solution. Example: -n Library"
        };
        var configurationOption = new Option<string?>("--configuration", "-c")
        {
            Description = "TwinCAT solution configuration. Example: -c Debug"
        };
        var platformOption = new Option<string?>("--platform", "-t")
        {
            Description = "TwinCAT target platform. Example: -t \"TwinCAT RT (x64)\". Defaults to the current target."
        };
        var silentOption = new Option<bool>("--silent", "-s")
        {
            Description = "Run without showing the IDE UI. Example: -s. Console logging remains enabled."
        };
        var automationHostOption = new Option<string?>("--host", "-H")
        {
            Description = "Automation host selection. Default: auto. Examples: --host vs2019 or --host xae2019."
        };

        command.Options.Add(projectOption);
        command.Options.Add(targetProjectOption);
        command.Options.Add(configurationOption);
        command.Options.Add(platformOption);
        command.Options.Add(silentOption);
        command.Options.Add(automationHostOption);

        Option<string?>? libraryOutputOption = null;
        if (includeLibraryOutput)
        {
            libraryOutputOption = new Option<string?>("--library-output", "-o")
            {
                Description = "Output path for the generated .library file. Example: -o .\\artifacts\\My.library"
            };
            command.Options.Add(libraryOutputOption);
        }

        Option<bool>? allProjectsOption = null;
        if (includeAllProjects)
        {
            allProjectsOption = new Option<bool>("--all-projects", "-A")
            {
                Description = "Build all TwinCAT projects in the selected solution. Example: -A"
            };
            command.Options.Add(allProjectsOption);
        }

        command.SetAction(parseResult => Execute(
            parseResult,
            projectOption,
            configurationOption,
            platformOption,
            executeBuild,
            activate,
            installLibrary,
            parseResult.GetValue(silentOption),
            parseResult.GetValue(automationHostOption),
            libraryOutputOption is null ? null : parseResult.GetValue(libraryOutputOption),
            parseResult.GetValue(targetProjectOption),
            allProjectsOption is not null && parseResult.GetValue(allProjectsOption),
            executor));

        return command;
    }

    private static int Execute(
        ParseResult parseResult,
        Option<string> projectOption,
        Option<string?> configurationOption,
        Option<string?> platformOption,
        bool executeBuild,
        bool activate,
        bool installLibrary,
        bool silent,
        string? automationHost,
        string? libraryOutputPath,
        string? targetProjectName,
        bool buildAllProjects,
        Func<BuildOptions, int> executor)
    {
        try
        {
            var options = new BuildOptions(
                parseResult.GetValue(projectOption)!,
                parseResult.GetValue(configurationOption),
                parseResult.GetValue(platformOption),
                executeBuild,
                activate,
                silent,
                libraryOutputPath,
                installLibrary,
                targetProjectName,
                buildAllProjects,
                automationHost);

            return executor(options);
        }
        catch (Exception exception)
        {
            var logger = new BuildLogger();
            logger.Error(exception.Message);
            if (exception.InnerException is not null)
                logger.Error($"Details: {exception.InnerException.Message}");
            return 1;
        }
    }
}
