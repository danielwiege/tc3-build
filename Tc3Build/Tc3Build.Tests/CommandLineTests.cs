using System.CommandLine;
using Tc3Build.Commands;
using Tc3Build.Options;
using Xunit;

namespace Tc3Build.Tests;

public sealed class CommandLineTests
{
    public static TheoryData<string> Operations =>
    [
        "build",
        "validate",
        "activate",
        "install-library"
    ];

    [Theory]
    [MemberData(nameof(Operations))]
    public void Every_operation_requires_a_project(string operation)
    {
        var executorCalled = false;
        var parseResult = RootCommandFactory.Create(_ =>
        {
            executorCalled = true;
            return 0;
        }).Parse(operation);

        Assert.NotEmpty(parseResult.Errors);
        Assert.Contains(parseResult.Errors, error => error.Message.Contains("project", StringComparison.OrdinalIgnoreCase));
        Assert.False(executorCalled);
    }

    [Fact]
    public void Build_executes_handler_and_maps_every_short_option()
    {
        var project = @"D:\Git\tc3-build\twincat\Project\Project.slnx";
        var exitCode = InvokeWithCapture([
            "build",
            "-p", project,
            "-n", "Project",
            "-c", "Release",
            "-t", "TwinCAT RT (x64)",
            "-s",
            "-H", "vs2019",
            "-A"],
            out var actualOptions);

        Assert.Equal(23, exitCode);
        Assert.Equal(new BuildOptions(
            project,
            "Release",
            "TwinCAT RT (x64)",
            ExecuteBuild: true,
            Activate: false,
            Silent: true,
            LibraryOutputPath: null,
            InstallLibrary: false,
            TargetProjectName: "Project",
            BuildAllProjects: true,
            AutomationHost: "vs2019"), actualOptions);
    }

    [Fact]
    public void Validate_executes_handler_with_validation_contract()
    {
        var project = @"D:\Git\tc3-build\twincat\Project\Project.slnx";
        var exitCode = InvokeWithCapture([
            "validate",
            "--project", project,
            "--project-name", "Project",
            "--configuration", "Debug",
            "--platform", "TwinCAT RT (x64)",
            "--silent",
            "--host", "xae2019"],
            out var actualOptions);

        Assert.Equal(23, exitCode);
        Assert.Equal(new BuildOptions(
            project,
            "Debug",
            "TwinCAT RT (x64)",
            ExecuteBuild: false,
            Activate: false,
            Silent: true,
            LibraryOutputPath: null,
            InstallLibrary: false,
            TargetProjectName: "Project",
            BuildAllProjects: false,
            AutomationHost: "xae2019"), actualOptions);
    }

    [Fact]
    public void Activate_executes_handler_with_activation_contract()
    {
        var project = @"D:\Git\tc3-build\twincat\Project\Project.slnx";
        var exitCode = InvokeWithCapture([
            "activate",
            "-p", project,
            "-c", "Release",
            "-t", "TwinCAT RT (x64)",
            "-H", "vs2022"],
            out var actualOptions);

        Assert.Equal(23, exitCode);
        Assert.Equal(new BuildOptions(
            project,
            "Release",
            "TwinCAT RT (x64)",
            ExecuteBuild: false,
            Activate: true,
            Silent: false,
            LibraryOutputPath: null,
            InstallLibrary: false,
            TargetProjectName: null,
            BuildAllProjects: false,
            AutomationHost: "vs2022"), actualOptions);
    }

    [Fact]
    public void Install_library_executes_handler_with_library_contract()
    {
        var project = @"D:\Git\tc3-build\twincat\Project\Library\Library.tspproj";
        var output = @"D:\Build\Library.library";
        var exitCode = InvokeWithCapture([
            "install-library",
            "--project", project,
            "--project-name", "Library",
            "--configuration", "Release",
            "--platform", "TwinCAT RT (x64)",
            "--silent",
            "--host", "vs2019",
            "--library-output", output],
            out var actualOptions);

        Assert.Equal(23, exitCode);
        Assert.Equal(new BuildOptions(
            project,
            "Release",
            "TwinCAT RT (x64)",
            ExecuteBuild: true,
            Activate: false,
            Silent: true,
            LibraryOutputPath: output,
            InstallLibrary: true,
            TargetProjectName: "Library",
            BuildAllProjects: false,
            AutomationHost: "vs2019"), actualOptions);
    }

    [Fact]
    public void Build_accepts_long_all_projects_and_auto_host()
    {
        var parseResult = RootCommandFactory.Create(_ => 0).Parse([
            "build",
            "--project", @"D:\Git\tc3-build\twincat\Project\Project.slnx",
            "--all-projects",
            "--host", "auto"]);

        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void Executor_result_is_returned_to_the_command_line()
    {
        var exitCode = RootCommandFactory.Create(_ => 7).Parse([
            "validate",
            "-p", "Project.slnx"]).Invoke();

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public void Executor_exception_is_converted_to_failure_exit_code()
    {
        var exitCode = RootCommandFactory.Create(_ =>
            throw new InvalidOperationException("controlled test failure"))
            .Parse(["validate", "-p", "Project.slnx"])
            .Invoke();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Legacy_operation_flags_are_not_accepted()
    {
        var parseResult = RootCommandFactory.Create(_ => 0).Parse([
            "build",
            "-p", "Project.slnx",
            "--activate"]);

        Assert.NotEmpty(parseResult.Errors);
        Assert.Contains(parseResult.Errors, error => error.Message.Contains("activate", StringComparison.OrdinalIgnoreCase));
    }

    private static int InvokeWithCapture(string[] args, out BuildOptions? actualOptions)
    {
        BuildOptions? capturedOptions = null;
        var exitCode = RootCommandFactory.Create(options =>
        {
            capturedOptions = options;
            return 23;
        }).Parse(args).Invoke();
        actualOptions = capturedOptions;
        return exitCode;
    }
}
