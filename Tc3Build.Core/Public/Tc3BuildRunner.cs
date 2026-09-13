using Tc3Build.Options;

namespace Tc3Build.Core;

/// <summary>
/// Operation supported by the reusable TwinCAT build API.
/// </summary>
public enum Tc3BuildOperation
{
    Build,
    Validate,
    Activate,
    InstallLibrary
}

/// <summary>
/// Describes one TwinCAT build-tool operation.
/// </summary>
public sealed record Tc3BuildRequest(
    string ProjectPath,
    Tc3BuildOperation Operation = Tc3BuildOperation.Build,
    string? Configuration = null,
    string? Platform = null,
    bool Silent = false,
    string? LibraryOutputPath = null,
    string? TargetProjectName = null,
    bool AllProjects = false,
    string? AutomationHost = null);

/// <summary>
/// Runs TwinCAT Automation Interface operations in the current process.
/// The calling process must run on Windows with a compatible Visual Studio or
/// TwinCAT XAE installation available.
/// </summary>
public sealed class Tc3BuildRunner
{
    /// <summary>
    /// Executes an already mapped build operation.
    /// </summary>
    public int Run(BuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Infrastructure.ComMessageFilter.Register();
        try
        {
            return new Infrastructure.TwinCatBuildService().Execute(options);
        }
        finally
        {
            Infrastructure.ComMessageFilter.Revoke();
        }
    }

    /// <summary>
    /// Executes the requested operation and returns its process-style exit code.
    /// </summary>
    public int Run(Tc3BuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectPath);

        var options = new BuildOptions(
            request.ProjectPath,
            request.Configuration,
            request.Platform,
            ExecuteBuild: request.Operation is Tc3BuildOperation.Build or Tc3BuildOperation.InstallLibrary,
            Activate: request.Operation is Tc3BuildOperation.Activate,
            request.Silent,
            request.LibraryOutputPath,
            InstallLibrary: request.Operation is Tc3BuildOperation.InstallLibrary,
            request.TargetProjectName,
            request.AllProjects,
            request.AutomationHost);

        return Run(options);
    }
}
