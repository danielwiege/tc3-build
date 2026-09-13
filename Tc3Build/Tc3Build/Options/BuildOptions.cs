namespace Tc3Build.Options;

internal sealed record BuildOptions(
    string ProjectPath,
    string? Configuration,
    string? Platform,
    bool ExecuteBuild,
    bool Activate,
    bool Silent,
    string? LibraryOutputPath,
    bool InstallLibrary,
    string? TargetProjectName,
    bool BuildAllProjects,
    string? AutomationHost)
{
    public string ResolvedProjectPath => Path.GetFullPath(ProjectPath);
}
