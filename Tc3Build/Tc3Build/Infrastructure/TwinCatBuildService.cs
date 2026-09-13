using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32;
using System.Security;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CSharp.RuntimeBinder;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using Tc3Build.Options;

namespace Tc3Build.Infrastructure;

internal sealed class TwinCatBuildService
{
    private const int AutomationHostTimeoutSeconds = 60;
    private readonly BuildLogger logger = new();

    private static IReadOnlyList<AutomationHostDefinition> AutomationHosts => AutomationHostCatalog.All;

    public int Execute(BuildOptions options)
    {
        var inputPath = options.ResolvedProjectPath;
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("The selected TwinCAT solution or project was not found.", inputPath);

        var selection = ResolveBuildSelection(
            inputPath,
            options.TargetProjectName,
            options.BuildAllProjects,
            requiresProjectSelection: options.ExecuteBuild);
        var solutionPath = selection.SolutionPath;
        var targetProjectPath = selection.TargetProjectPath;

        object? dte = null;
        object? solution = null;
        AutomationHost? automationHost = null;
        dynamic? ide = null;
        dynamic? twinCatSolution = null;
        dynamic? solutionBuild = null;
        var visualStudioFallbackAttempted = false;
        var solutionOpenedByTool = false;
        using var silentDialogHandler = SilentIdeDialogHandler.Start(options.Silent, logger.Info);

        try
        {
            logger.Info($"Selection: {inputPath}");
            logger.Info($"Solution: {solutionPath}");
            if (targetProjectPath is not null)
                logger.Info($"Selected project: {targetProjectPath}");
            logger.Info(options.Platform is null
                ? "Requested platform: automatic (current TwinCAT target)."
                : $"Requested platform: {options.Platform}");
            logger.Info(options.Configuration is null
                ? "Requested configuration: automatic (TwinCAT solution has no explicit build type)."
                : $"Requested configuration: {options.Configuration}");
            logger.Info($"Automation host selection: {options.AutomationHost ?? "auto"}.");
            logger.Info(
                $"Windows identity: {WindowsIdentity.GetCurrent().Name}; " +
                $"elevated: {DescribeElevation(TryGetProcessElevation(Process.GetCurrentProcess()))}.");
            while (true)
            {
                logger.Info("Starting automation host (Visual Studio preferred)...");
                automationHost = CreateAutomationInstance(
                    solutionPath,
                    options.Silent,
                    skipVisualStudio: visualStudioFallbackAttempted,
                    hostSelection: options.AutomationHost);
                dte = automationHost.Instance;
                silentDialogHandler?.RegisterAutomationProcess(dte, automationHost.ProcessId);

                try
                {
                    // XAE must finish loading its project system before
                    // Solution.Open is called. Visible mode is the default;
                    // silent mode is useful for CI.
                    ide = dte;
                    ide.SuppressUI = options.Silent;
                    ConfigureIdeVisibility(ide, options.Silent, automationHost.OwnsInstance);
                    // Set this before opening the solution. XAE can show a
                    // reload/project-start dialog while it initializes the
                    // project tree; setting SilentMode afterwards is too late.
                    ConfigureTwinCatSilentMode(ide, options.Silent);

                    var hostName = automationHost.ProgId.StartsWith("VisualStudio.", StringComparison.OrdinalIgnoreCase)
                        ? "Visual Studio"
                        : "TwinCAT XAE Shell";
                    logger.Success($"Automation host started: {hostName} ({automationHost.ProgId})");
                    logger.Info(options.Silent
                        ? "IDE UI: silent (console logging remains enabled)."
                        : "IDE UI: visible.");
                    var automationPath = ResolveAutomationSolutionPath(solutionPath);
                    logger.Info($"Opening TwinCAT solution: {automationPath}");
                    solution = ide.Solution;
                    twinCatSolution = solution;
                    if (IsSolutionAlreadyOpen(twinCatSolution, automationPath))
                    {
                        logger.Info("TwinCAT solution was already opened by the automation host.");
                        // A newly started host opened it from the command line;
                        // it is therefore still safe for us to close it later.
                        solutionOpenedByTool = automationHost.OwnsInstance;
                    }
                    else
                    {
                        OpenTwinCatSolution(twinCatSolution, automationPath);
                        solutionOpenedByTool = true;
                        logger.Success("TwinCAT solution opened.");
                    }
                    WaitForTwinCatProjects(twinCatSolution);
                    ConfigureTwinCatSilentMode(ide, options.Silent);
                    ConfigureIdeVisibility(ide, options.Silent, automationHost.OwnsInstance);
                    solutionBuild = twinCatSolution.SolutionBuild;
                    break;
                }
                catch (Exception exception) when
                    (!visualStudioFallbackAttempted &&
                     automationHost.ProgId.StartsWith("VisualStudio.", StringComparison.OrdinalIgnoreCase) &&
                     AutomationHostCatalog.IsAutomaticSelection(options.AutomationHost))
                {
                    logger.Warning(
                        $"Visual Studio could not initialize the TwinCAT project ({exception.Message}). " +
                        "Falling back to TwinCAT XAE Shell.");
                    ReleaseComObject(solution);
                    solution = null;
                    QuitAndRelease(dte, automationHost.OwnsInstance, automationHost.ProcessId, automationHost.ProgId);
                    dte = null;
                    ide = null;
                    twinCatSolution = null;
                    solutionBuild = null;
                    solutionOpenedByTool = false;
                    automationHost = null;
                    visualStudioFallbackAttempted = true;
                }
            }

            var platform = ResolveTargetPlatform(twinCatSolution, options.Platform);
            var solutionConfigurationName = ActivateConfiguration(solutionBuild, options.Configuration, platform);
            logger.Success("TwinCAT solution configuration selected.");

            if (!options.ExecuteBuild)
            {
                if (options.Activate)
                    ActivateTwinCatConfiguration(twinCatSolution, platform);
                else
                    logger.Success("Validation completed without building.");
                return 0;
            }

            logger.Info("Starting TwinCAT build...");
            var stopwatch = Stopwatch.StartNew();
            BuildTwinCatSolution(solutionBuild, twinCatSolution, targetProjectPath, solutionConfigurationName);
            stopwatch.Stop();

            var errorCount = (int)solutionBuild.LastBuildInfo;
            logger.Info($"TwinCAT build finished after {stopwatch.Elapsed.TotalSeconds:F1}s.");
            if (errorCount == 0)
            {
                logger.Success("TwinCAT build succeeded with 0 errors.");
                if (options.InstallLibrary || options.LibraryOutputPath is not null)
                    SaveTwinCatLibrary(
                        twinCatSolution,
                        targetProjectPath,
                        options.LibraryOutputPath,
                        options.InstallLibrary);
                if (options.Activate)
                    ActivateTwinCatConfiguration(twinCatSolution, platform);
                return 0;
            }

            logger.Error($"TwinCAT build completed with {errorCount} error(s).");
            LogBuildErrors(ide);
            return 1;
        }
        finally
        {
            CloseSolutionWithoutSaving(
                solution,
                solutionOpenedByTool && (automationHost?.OwnsInstance ?? true),
                automationHost?.ProcessId,
                automationHost?.ProgId);
            ReleaseComObject(solution);
            QuitAndRelease(
                dte,
                automationHost?.OwnsInstance ?? true,
                automationHost?.ProcessId,
                automationHost?.ProgId);
        }
    }

    private void ActivateTwinCatConfiguration(dynamic twinCatSolution, string platform)
    {
        var systemManager = FindSystemManager(twinCatSolution);

        ConfigureTwinCatTargetPlatform(systemManager, platform);
        logger.Info("Activating TwinCAT configuration (Save To Registry; this may take several minutes)...");
        var stopwatch = Stopwatch.StartNew();
        ExecuteCom("Activating the TwinCAT configuration", () => systemManager.ActivateConfiguration());
        stopwatch.Stop();
        logger.Success($"TwinCAT configuration activated after {stopwatch.Elapsed.TotalSeconds:F1}s.");
        logger.Info("The TwinCAT runtime was not restarted. Use the TwinCAT UI to restart it when required.");
    }

    private void SaveTwinCatLibrary(
        dynamic twinCatSolution,
        string? targetProjectPath,
        string? requestedOutputPath,
        bool install)
    {
        dynamic libraryProject = FindLibraryProject(twinCatSolution, targetProjectPath);
        var projectPath = (string?)libraryProject.FullName;
        if (string.IsNullOrWhiteSpace(projectPath))
            throw new InvalidOperationException("The TwinCAT library project has no file path.");

        ValidateLibraryMetadata(projectPath);

        var outputPath = string.IsNullOrWhiteSpace(requestedOutputPath)
            ? Path.Combine(
                Path.GetDirectoryName(projectPath)!,
                $"{Path.GetFileNameWithoutExtension(projectPath)}.library")
            : Path.GetFullPath(requestedOutputPath);
        var extension = Path.GetExtension(outputPath);
        if (!string.Equals(extension, ".library", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".compiled-library", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The library output path must end with '.library' or '.compiled-library'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        logger.Info($"Saving TwinCAT library: {outputPath}");
        SaveLibraryProject(twinCatSolution, libraryProject, projectPath, outputPath, install);
        logger.Success(install
            ? $"TwinCAT library saved and installed: {outputPath}"
            : $"TwinCAT library saved: {outputPath}");
    }

    private void SaveLibraryProject(
        dynamic twinCatSolution,
        dynamic libraryProject,
        string projectPath,
        string outputPath,
        bool install)
    {
        var systemManager = FindSystemManager(twinCatSolution);
        var plcRoot = (TCatSysManagerLib.ITcSmTreeItem)systemManager.LookupTreeItem("TIPC");
        var plcProjectPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "PLC", "PLC.plcproj");
        logger.Info($"Temporarily loading standalone PLC project: {plcProjectPath}");
        var temporaryProjectName = $"Tc3BuildLibrary_{Guid.NewGuid():N}";
        var importedProject = plcRoot.CreateChild(temporaryProjectName, 0, string.Empty, plcProjectPath);
        var temporaryCopyDirectory = FindTemporaryCopyDirectory(plcProjectPath, temporaryProjectName);
        var importedProjectPath = (string?)importedProject.PathName;
        var importedProjectName = (string?)importedProject.Name;
        try
        {
            var projectTree = (TCatSysManagerLib.ITcSmTreeItem)importedProject;
            if (!TrySaveLibraryTreeItem(projectTree, outputPath, install, depth: 0))
                throw new InvalidOperationException(
                    "No PLC project node exposing ITcPlcIECProject.SaveAsLibrary() was found in the imported library project.");
        }
        finally
        {
            logger.Info("Removing temporary PLC library project from the active configuration...");
            RemoveTemporaryLibraryProject(plcRoot, importedProjectPath, importedProjectName);
            RemoveTemporaryCopyDirectory(temporaryCopyDirectory);
        }
    }

    private static string? FindTemporaryCopyDirectory(string plcProjectPath, string temporaryProjectName)
    {
        var searchDirectory = Directory.GetParent(Path.GetDirectoryName(plcProjectPath)!)?.Parent;
        for (var depth = 0; searchDirectory is not null && depth < 3; depth++, searchDirectory = searchDirectory.Parent)
        {
            var match = Directory.EnumerateDirectories(
                    searchDirectory.FullName,
                    temporaryProjectName,
                    SearchOption.AllDirectories)
                .FirstOrDefault();
            if (match is not null)
                return match;
        }

        return null;
    }

    private void RemoveTemporaryCopyDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        var directoryName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
        if (!directoryName.StartsWith("Tc3BuildLibrary_", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                logger.Success("Temporary copied PLC library project removed from disk.");
            }
        }
        catch (IOException exception)
        {
            logger.Warning($"The temporary copied PLC library project could not be removed: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.Warning($"The temporary copied PLC library project could not be removed: {exception.Message}");
        }
    }

    private bool TrySaveLibraryTreeItem(
        TCatSysManagerLib.ITcSmTreeItem treeItem,
        string outputPath,
        bool install,
        int depth)
    {
        logger.Info($"  Library tree item: {treeItem.Name} ({treeItem.PathName})");

        // A PLC project has two parts. Beckhoff exposes the IEC project on
        // the nested project (ITcProjectRoot.NestedProject), while the outer
        // node and the project instance do not implement ITcPlcIECProject.
        try
        {
            var projectRoot = (TCatSysManagerLib.ITcProjectRoot)treeItem;
            var nestedProject = projectRoot.NestedProject;
            if (nestedProject is not null)
            {
                logger.Info($"  Nested PLC project: {nestedProject.Name} ({nestedProject.PathName})");
                if (TrySaveLibraryTreeItem(nestedProject, outputPath, install, depth + 1))
                    return true;
            }
        }
        catch (InvalidCastException)
        {
            // This tree item is not a PLC project root.
        }

        try
        {
            var iecProject = (TCatSysManagerLib.ITcPlcIECProject)treeItem;
            try
            {
                iecProject.SaveAsLibrary(outputPath, install);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Saving the TwinCAT PLC project as a library failed. " +
                    $"HRESULT=0x{exception.HResult:X8} ({exception.Message})",
                    exception);
            }

            WaitForLibraryFile(outputPath);
            return true;
        }
        catch (InvalidCastException)
        {
            // This is a container node; continue with its children.
        }

        if (depth >= 8)
            return false;

        for (var index = 1; index <= treeItem.ChildCount; index++)
        {
            var child = treeItem.get_Child(index);
            if (child is not null && TrySaveLibraryTreeItem(child, outputPath, install, depth + 1))
                return true;
        }

        return false;
    }

    private void ValidateLibraryMetadata(string libraryProjectPath)
    {
        var plcProjectPath = Path.Combine(Path.GetDirectoryName(libraryProjectPath)!, "PLC", "PLC.plcproj");
        if (!File.Exists(plcProjectPath))
            throw new FileNotFoundException("The PLC project belonging to the library project was not found.", plcProjectPath);

        var document = XDocument.Load(plcProjectPath);
        var propertyGroup = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "PropertyGroup");
        var requiredValues = new[]
        {
            (Name: "Company", XmlNames: new[] { "Company" }),
            (Name: "Title", XmlNames: new[] { "Title" }),
            // TwinCAT stores the UI's Version field as ProjectVersion in
            // generated PLC project files. Accept Version as a legacy form.
            (Name: "Version", XmlNames: new[] { "ProjectVersion", "Version" })
        };
        var missing = requiredValues
            .Where(required => !required.XmlNames.Any(xmlName =>
                !string.IsNullOrWhiteSpace(propertyGroup?.Elements()
                    .FirstOrDefault(element => element.Name.LocalName == xmlName)?.Value)))
            .Select(required => required.Name)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"The PLC library project is missing required metadata: {string.Join(", ", missing)}. " +
                "Set Company, Title, and Version in the TwinCAT PLC project properties.");
        }
    }

    private void RemoveTemporaryLibraryProject(
        TCatSysManagerLib.ITcSmTreeItem plcRoot,
        string? projectPath,
        string? projectName)
    {
        foreach (var identifier in new[] { projectPath, projectName }.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            try
            {
                plcRoot.DeleteChild(identifier!);
                logger.Success("Temporary PLC library project removed.");
                return;
            }
            catch (COMException)
            {
                // TwinCAT versions differ in whether DeleteChild expects the
                // tree path or the child name.
            }
        }

        logger.Warning("The temporary PLC library project could not be removed from the active configuration.");
    }

    private void WaitForLibraryFile(string outputPath)
    {
        var timeout = DateTime.UtcNow.AddMinutes(2);
        var nextProgress = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(outputPath) && DateTime.UtcNow < timeout)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            if (DateTime.UtcNow >= nextProgress)
            {
                logger.Info("TwinCAT is still generating the library file...");
                nextProgress = DateTime.UtcNow.AddSeconds(5);
            }
        }

        if (!File.Exists(outputPath))
            throw new InvalidOperationException(
                $"TwinCAT did not create the library file within 120 seconds: {outputPath}");
    }

    private dynamic FindLibraryProject(dynamic twinCatSolution, string? targetProjectPath)
    {
        dynamic projects = twinCatSolution.Projects;
        var libraryProjects = new List<dynamic>();
        for (var index = 1; index <= projects.Count; index++)
        {
            dynamic project = projects.Item(index);
            var fullName = (string?)project.FullName;
            if (fullName?.EndsWith(".tspproj", StringComparison.OrdinalIgnoreCase) == true)
            {
                logger.Info($"Library project: {fullName}");
                libraryProjects.Add(project);
            }
        }

        if (targetProjectPath is not null)
        {
            var selectedLibrary = libraryProjects.FirstOrDefault(project =>
                string.Equals(
                    (string?)project.FullName,
                    targetProjectPath,
                    StringComparison.OrdinalIgnoreCase));
            if (selectedLibrary is not null)
                return selectedLibrary;

            throw new InvalidOperationException(
                $"The selected project is not a TwinCAT PLC library project (.tspproj): {targetProjectPath}");
        }

        if (libraryProjects.Count == 1)
            return libraryProjects[0];

        if (libraryProjects.Count > 1)
        {
            throw new InvalidOperationException(
                "The solution contains multiple TwinCAT PLC library projects. " +
                "Select the desired .tspproj explicitly with --project/-p.");
        }

        throw new InvalidOperationException(
            "No TwinCAT .tspproj library project was found in the opened solution.");
    }

    private string ResolveTargetPlatform(dynamic twinCatSolution, string? requestedPlatform)
    {
        if (!string.IsNullOrWhiteSpace(requestedPlatform))
            return requestedPlatform;

        var systemManager = FindSystemManager(twinCatSolution);
        try
        {
            dynamic configurationManager = systemManager.ConfigurationManager;
            var currentPlatform = (string?)configurationManager.ActiveTargetPlatform;
            if (string.IsNullOrWhiteSpace(currentPlatform))
                throw new InvalidOperationException(
                    "TwinCAT did not report a current target platform. Specify --platform explicitly.");

            logger.Info($"Using current TwinCAT target platform: {currentPlatform}");
            return currentPlatform;
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                "Could not read the current TwinCAT target platform. Specify --platform explicitly. " +
                $"HRESULT=0x{exception.HResult:X8} ({exception.Message})",
                exception);
        }
        catch (RuntimeBinderException exception)
        {
            throw new InvalidOperationException(
                "The opened TwinCAT project does not expose ConfigurationManager.ActiveTargetPlatform. " +
                "Specify --platform explicitly.",
                exception);
        }
    }

    private dynamic FindSystemManager(dynamic twinCatSolution)
    {
        logger.Info("Locating TwinCAT system manager...");
        dynamic projects = twinCatSolution.Projects;

        for (var index = 1; index <= projects.Count; index++)
        {
            dynamic project = projects.Item(index);
            var fullName = (string?)project.FullName;
            logger.Info($"  Project: {fullName ?? "<unknown>"}");
            if (fullName?.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase) == true)
                return project.Object;
        }

        throw new InvalidOperationException("No TwinCAT .tsproj project was found in the opened solution.");
    }

    private string FindSolutionProjectName(dynamic twinCatSolution, string targetProjectPath)
    {
        dynamic projects = twinCatSolution.Projects;
        for (var index = 1; index <= projects.Count; index++)
        {
            dynamic project = projects.Item(index);
            var fullName = (string?)project.FullName;
            if (!string.Equals(fullName, targetProjectPath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var uniqueName = (string?)project.UniqueName;
                if (!string.IsNullOrWhiteSpace(uniqueName))
                    return uniqueName;
            }
            catch (RuntimeBinderException)
            {
                // Fall back to the display name for older project systems.
            }

            var name = (string?)project.Name;
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        throw new InvalidOperationException(
            $"The selected project is not loaded in the opened solution: {targetProjectPath}");
    }

    private BuildSelection ResolveBuildSelection(
        string inputPath,
        string? targetProjectName,
        bool buildAllProjects,
        bool requiresProjectSelection)
    {
        var extension = Path.GetExtension(inputPath);
        if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            if (!requiresProjectSelection && targetProjectName is null && !buildAllProjects)
                return new BuildSelection(inputPath, TargetProjectPath: null);

            if (targetProjectName is null && !buildAllProjects)
            {
                var projectPaths = GetTwinCatProjectsFromSolution(inputPath);
                if (projectPaths.Count == 1)
                    return new BuildSelection(inputPath, projectPaths[0]);

                if (projectPaths.Count == 0)
                    throw new InvalidOperationException(
                        $"The solution contains no top-level TwinCAT .tsproj or .tspproj projects: {inputPath}");

                throw new InvalidOperationException(
                    "The solution contains multiple TwinCAT projects. Select one with --project-name/-n " +
                    "or use --all-projects/-A for a complete solution build. Available projects: " +
                    string.Join(", ", projectPaths.Select(path => Path.GetFileName(path))));
            }

            if (targetProjectName is not null && buildAllProjects)
                throw new InvalidOperationException("--project-name/-n and --all-projects/-A cannot be used together.");

            if (targetProjectName is null)
                return new BuildSelection(inputPath, TargetProjectPath: null);

            var selectedProjectPath = SelectSolutionProject(inputPath, targetProjectName);
            return new BuildSelection(inputPath, selectedProjectPath);
        }

        if (targetProjectName is not null)
            throw new InvalidOperationException(
                "--project-name/-n can only be used when --project/-p points to a .sln or .slnx solution.");
        if (buildAllProjects)
            throw new InvalidOperationException(
                "--all-projects/-A can only be used when --project/-p points to a .sln or .slnx solution.");

        if (string.Equals(extension, ".plcproj", StringComparison.OrdinalIgnoreCase))
        {
            inputPath = FindContainingTwinCatProject(inputPath);
            extension = Path.GetExtension(inputPath);
        }

        if (!string.Equals(extension, ".tsproj", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".tspproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The --project/-p path must point to a .sln, .slnx, .tsproj, .tspproj, or .plcproj file.");
        }

        var solutionPath = FindContainingSolution(inputPath);
        return new BuildSelection(solutionPath, inputPath);
    }

    private static IReadOnlyList<string> GetTwinCatProjectsFromSolution(string solutionPath)
    {
        var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        IEnumerable<string?> projectPaths;
        if (string.Equals(Path.GetExtension(solutionPath), ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var document = XDocument.Load(solutionPath);
            projectPaths = document.Root?.Elements("Project")
                .Select(element => element.Attribute("Path")?.Value)
                ?? Enumerable.Empty<string?>();
        }
        else
        {
            projectPaths = File.ReadLines(solutionPath)
                .Where(line => line.TrimStart().StartsWith("Project(", StringComparison.Ordinal))
                .Select(ParseClassicSolutionProjectPath);
        }

        return projectPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Path.Combine(solutionDirectory, path!.Replace('/', '\\'))))
            .Where(path =>
                File.Exists(path) &&
                (path.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".tspproj", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ParseClassicSolutionProjectPath(string line)
    {
        var equalsIndex = line.IndexOf('=');
        if (equalsIndex < 0)
            return null;

        var firstQuote = line.IndexOf('"', equalsIndex);
        var secondQuote = firstQuote < 0 ? -1 : line.IndexOf('"', firstQuote + 1);
        var thirdQuote = secondQuote < 0 ? -1 : line.IndexOf('"', secondQuote + 1);
        var fourthQuote = thirdQuote < 0 ? -1 : line.IndexOf('"', thirdQuote + 1);
        return thirdQuote >= 0 && fourthQuote > thirdQuote
            ? line[(thirdQuote + 1)..fourthQuote]
            : null;
    }

    private static string SelectSolutionProject(string solutionPath, string targetProjectName)
    {
        var projects = GetTwinCatProjectsFromSolution(solutionPath);
        var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        var matches = projects.Where(path =>
        {
            var relativePath = Path.GetRelativePath(solutionDirectory, path).Replace('/', '\\');
            var fileName = Path.GetFileName(path);
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
            return string.Equals(targetProjectName, relativePath, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(targetProjectName, relativePath[..^Path.GetExtension(relativePath).Length], StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(targetProjectName, fileName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(targetProjectName, fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase);
        }).ToArray();

        if (matches.Length == 1)
            return matches[0];
        if (matches.Length > 1)
            throw new InvalidOperationException(
                $"The project name '{targetProjectName}' matches more than one TwinCAT project. " +
                "Use the relative project path instead.");

        throw new InvalidOperationException(
            $"The project '{targetProjectName}' was not found in the solution. Available projects: " +
            string.Join(", ", projects.Select(path => Path.GetRelativePath(solutionDirectory, path))));
    }

    private static string FindContainingTwinCatProject(string plcProjectPath)
    {
        var currentDirectory = new DirectoryInfo(Path.GetDirectoryName(plcProjectPath)!);
        while (currentDirectory is not null)
        {
            var candidates = currentDirectory.GetFiles("*.tspproj")
                .Concat(currentDirectory.GetFiles("*.tsproj"))
                .ToArray();
            var relativePlcPath = Path.GetRelativePath(
                currentDirectory.FullName,
                plcProjectPath).Replace('/', '\\');

            var matchingCandidate = candidates.FirstOrDefault(candidate =>
                File.ReadAllText(candidate.FullName).Contains(
                    relativePlcPath,
                    StringComparison.OrdinalIgnoreCase));
            if (matchingCandidate is not null)
                return matchingCandidate.FullName;

            if (candidates.Length == 1)
                return candidates[0].FullName;

            currentDirectory = currentDirectory.Parent!;
        }

        throw new InvalidOperationException(
            $"Could not find the TwinCAT .tsproj or .tspproj containing the PLC project: {plcProjectPath}");
    }

    private static string FindContainingSolution(string projectPath)
    {
        var currentDirectory = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);
        while (currentDirectory is not null)
        {
            var solutionFiles = currentDirectory.GetFiles("*.slnx")
                .Concat(currentDirectory.GetFiles("*.sln"))
                .OrderBy(file => string.Equals(file.Extension, ".slnx", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToArray();
            foreach (var solutionFile in solutionFiles)
            {
                if (SolutionReferencesProject(solutionFile.FullName, projectPath))
                    return solutionFile.FullName;
            }

            currentDirectory = currentDirectory.Parent!;
        }

        throw new InvalidOperationException(
            $"Could not find a solution containing the selected TwinCAT project: {projectPath}");
    }

    private static bool SolutionReferencesProject(string solutionPath, string projectPath)
    {
        var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        var relativeProjectPath = Path.GetRelativePath(solutionDirectory, projectPath)
            .Replace('/', '\\');

        if (string.Equals(Path.GetExtension(solutionPath), ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var document = XDocument.Load(solutionPath);
                return document.Root?.Elements("Project")
                    .Select(element => element.Attribute("Path")?.Value)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Any(path => string.Equals(
                        path!.Replace('/', '\\'),
                        relativeProjectPath,
                        StringComparison.OrdinalIgnoreCase)) == true;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        var solutionText = File.ReadAllText(solutionPath);
        return solutionText.Contains(
            $"\"{relativeProjectPath}\"",
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed record BuildSelection(string SolutionPath, string? TargetProjectPath);

    private void ConfigureTwinCatTargetPlatform(dynamic systemManager, string requestedPlatform)
    {
        try
        {
            dynamic configurationManager = systemManager.ConfigurationManager;
            var currentPlatform = (string?)configurationManager.ActiveTargetPlatform;
            logger.Info($"TwinCAT target platform: {currentPlatform ?? "<unknown>"}");
            if (string.Equals(currentPlatform, requestedPlatform, StringComparison.OrdinalIgnoreCase))
                return;

            logger.Info($"Adjusting TwinCAT target platform to '{requestedPlatform}'...");
            configurationManager.ActiveTargetPlatform = requestedPlatform;
            logger.Success($"TwinCAT target platform set to '{requestedPlatform}'.");
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                $"Could not set the TwinCAT target platform to '{requestedPlatform}'. " +
                $"HRESULT=0x{exception.HResult:X8} ({exception.Message})",
                exception);
        }
        catch (RuntimeBinderException exception)
        {
            throw new InvalidOperationException(
                "The opened TwinCAT project does not expose ConfigurationManager.ActiveTargetPlatform.",
                exception);
        }
    }

    private string ResolveAutomationSolutionPath(string projectPath)
    {
        if (!string.Equals(Path.GetExtension(projectPath), ".slnx", StringComparison.OrdinalIgnoreCase))
            return projectPath;

        var classicSolutionPath = Path.ChangeExtension(projectPath, ".sln");
        if (File.Exists(classicSolutionPath))
        {
            logger.Info($"TwinCAT DTE compatibility: using classic solution {classicSolutionPath}");
            return classicSolutionPath;
        }

        var document = XDocument.Load(projectPath);
        var project = document.Root?.Element("Project")?.Attribute("Path")?.Value;
        if (string.IsNullOrWhiteSpace(project))
            throw new InvalidOperationException($"TwinCAT solution contains no project entry: {projectPath}");

        var projectPathFromSolution = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, project));
        if (!File.Exists(projectPathFromSolution))
            throw new FileNotFoundException("TwinCAT project referenced by the solution was not found.", projectPathFromSolution);

        throw new InvalidOperationException(
            $"TwinCAT XAE does not support this .slnx directly and no classic fallback exists. " +
            $"Create or provide: {classicSolutionPath}");
    }

    private AutomationHost CreateAutomationInstance(
        string projectPath,
        bool silent,
        bool skipVisualStudio,
        string? hostSelection)
    {
        var candidateHosts = ResolveAutomationHosts(hostSelection, skipVisualStudio);
        var startupErrors = new List<string>();
        var registeredHostFound = false;
        foreach (var host in candidateHosts)
        {
            var progId = host.ProgId;

            var type = Type.GetTypeFromProgID(progId, throwOnError: false);
            if (type is null)
                continue;

            registeredHostFound = true;
            logger.Info(
                $"Automation host registered: {host.DisplayName} ({progId}); " +
                $"executable: {FindAutomationExecutable(progId) ?? "<not found>"}.");

            // Silent execution gets its own automation host so that an
            // already open developer session is neither shown nor modified.
            var runningInstance = silent ? null : TryGetRunningInstance(progId);
            if (runningInstance is not null)
            {
                logger.Info($"Using already running automation host: {progId}");
                return new AutomationHost(runningInstance, progId, OwnsInstance: false, ProcessId: null);
            }

            // The official automation path is COM activation. It starts the IDE
            // with its automation interface and returns the DTE object directly.
            // Use it first when no host process exists; a normal devenv.exe or
            // TcXaeShell.exe does not necessarily publish a DTE in the ROT.
            if (!host.IsVisualStudio &&
                FindAutomationExecutable(progId) is not null &&
                (!HasAnyHostProcess(progId) || silent))
            {
                var processIdsBeforeActivation = GetHostProcessIds(progId);
                var createdInstance = TryCreateInstance(type, progId);
                if (createdInstance is not null)
                {
                    var processId = FindNewHostProcessId(progId, processIdsBeforeActivation);
                    return new AutomationHost(createdInstance, progId, OwnsInstance: true, processId);
                }
            }

            // Fallback for installations where COM activation cannot create the
            // server. The explicit startup keeps non-silent runs observable.
            var startedHost = StartOrWaitForAutomationHost(progId, projectPath, silent);
            if (startedHost is not null)
                return startedHost;

            startupErrors.Add($"{progId}: timed out waiting for a DTE instance");
        }

        if (startupErrors.Count > 0)
        {
            var runningHostProcesses = candidateHosts
                .Select(host => host.ProgId)
                .Select(GetProcessName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => Process.GetProcessesByName(name).Length > 0)
                .ToArray();
            var elevationHint = runningHostProcesses.Length == 0
                ? string.Empty
                : $" Running host process(es) detected ({string.Join(", ", runningHostProcesses)}), " +
                  "but their DTE is not visible; start Tc3Build under the same Windows user and elevation level, " +
                  "or close the existing IDE instance. ";
            throw new InvalidOperationException(
                "No Visual Studio or TwinCAT XAE Automation host could be started. " +
                "Verify that Visual Studio with TwinCAT XAE integration or XAE Shell is installed." +
                elevationHint +
                string.Join("; ", startupErrors));
        }

        if (!registeredHostFound && !AutomationHostCatalog.IsAutomaticSelection(hostSelection))
        {
            throw new InvalidOperationException(
                $"The requested automation host '{hostSelection}' is not installed or not registered on this machine.");
        }

        throw new InvalidOperationException(
            "No Visual Studio or TwinCAT XAE Automation Interface was found. " +
            "Install Visual Studio with TwinCAT XAE integration or TwinCAT XAE Shell first.");
    }

    private static IReadOnlyList<AutomationHostDefinition> ResolveAutomationHosts(
        string? selection,
        bool skipVisualStudio)
    {
        var selectedHosts = AutomationHostCatalog.Resolve(selection);

        if (skipVisualStudio)
            selectedHosts = selectedHosts.Where(host => !host.IsVisualStudio).ToArray();

        if (selectedHosts.Count == 0)
        {
            throw new InvalidOperationException(
                "Visual Studio could not initialize the TwinCAT project and no XAE Shell fallback is available for the selected host. " +
                "Use --host auto to allow automatic fallback.");
        }

        return selectedHosts;
    }

    private AutomationHost? StartOrWaitForAutomationHost(string progId, string projectPath, bool silent)
    {
        var processName = GetProcessName(progId);
        var isVisualStudio = progId.StartsWith("VisualStudio.", StringComparison.OrdinalIgnoreCase);
        var hostProcesses = Process.GetProcessesByName(processName);
        // A normal devenv.exe does not necessarily publish a DTE moniker.
        // For Visual Studio always start a dedicated automation instance when
        // no usable DTE was found above. Waiting on an arbitrary devenv.exe is
        // what caused the former two-minute delay and XAE fallback.
        var existingHost = !silent && !isVisualStudio && hostProcesses.Any(process => IsSameElevation(process));
        if (!existingHost && hostProcesses.Length > 0)
        {
            logger.Info(isVisualStudio
                ? $"{progId} is running without a usable DTE; starting a dedicated automation instance."
                : $"{progId} is running under another user or elevation level and cannot be used by this process. " +
                  "Starting a host at the current elevation instead.");
        }
        Process? startedProcess = null;

        if (!existingHost)
        {
            var executablePath = FindAutomationExecutable(progId);
            if (executablePath is null)
            {
                logger.Warning($"No automation host executable found for {progId}.");
                return null;
            }

            logger.Info($"Starting automation host: {executablePath}");
            startedProcess = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                // Launch with the solution already supplied. This prevents
                // Visual Studio from displaying the Get started/recent-project
                // page while the DTE is being published.
                Arguments = $"\"{GetLaunchSolutionPath(projectPath)}\"",
                UseShellExecute = true,
                WindowStyle = silent ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
            });
        }
        else
        {
            logger.Info($"{progId} is already running; waiting for its automation interface...");
        }

        if (startedProcess is null && isVisualStudio)
            throw new InvalidOperationException($"Could not start a dedicated {progId} automation process.");

        var timeout = DateTime.UtcNow.AddSeconds(AutomationHostTimeoutSeconds);
        var nextProgressMessage = DateTime.UtcNow.AddSeconds(10);
        logger.Info($"Waiting up to {AutomationHostTimeoutSeconds} seconds for {progId} to publish its automation interface...");
        while (DateTime.UtcNow < timeout)
        {
            var runningInstance = TryGetRunningInstance(progId, startedProcess?.Id);
            if (runningInstance is not null)
                return new AutomationHost(
                    runningInstance,
                    progId,
                    OwnsInstance: startedProcess is not null,
                    ProcessId: startedProcess?.Id);

            if (DateTime.UtcNow >= nextProgressMessage)
            {
                logger.Info($"Still waiting for {progId}...");
                nextProgressMessage = DateTime.UtcNow.AddSeconds(10);
            }

            Thread.Sleep(500);
        }

        if (startedProcess is not null && !startedProcess.HasExited)
        {
            logger.Warning($"No DTE instance appeared for {progId}; stopping the host started by Tc3Build.");
            try
            {
                startedProcess.Kill(entireProcessTree: true);
                startedProcess.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // The host may have exited between the state check and Kill().
            }
        }

        return null;
    }

    private object? TryCreateInstance(Type type, string progId)
    {
        logger.Info($"Creating automation host through COM: {progId}...");
        try
        {
            // Keep activation on the process' main STA. The COM message filter
            // is registered there and the returned RCW therefore remains in
            // the apartment where it will be used later.
            var createdInstance = Activator.CreateInstance(type);
            if (createdInstance is not null && IsUsableAutomationInstance(createdInstance))
            {
                logger.Success($"Automation host created through COM: {progId}");
                return createdInstance;
            }

            ReleaseComObject(createdInstance);
            logger.Warning($"COM activation returned no usable DTE instance for {progId}; continuing with process/ROT fallback.");
        }
        catch (COMException exception)
        {
            logger.Warning($"COM activation failed for {progId}: HRESULT=0x{exception.HResult:X8} ({exception.Message})");
        }
        catch (Exception exception)
        {
            logger.Warning($"COM activation failed for {progId}: {exception.Message}");
        }

        return null;
    }

    private static string GetProcessName(string progId) =>
        progId.StartsWith("VisualStudio.", StringComparison.OrdinalIgnoreCase)
            ? "devenv"
            : "TcXaeShell";

    private static string GetLaunchSolutionPath(string projectPath)
    {
        if (string.Equals(Path.GetExtension(projectPath), ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var classicPath = Path.ChangeExtension(projectPath, ".sln");
            if (File.Exists(classicPath))
                return classicPath;
        }

        return projectPath;
    }

    private static string? FindAutomationExecutable(string progId)
    {
        var registeredPath = FindRegisteredAutomationExecutable(progId);
        if (registeredPath is not null)
            return registeredPath;

        if (progId.StartsWith("TcXaeShell.", StringComparison.OrdinalIgnoreCase))
        {
            var expectedProductVersion = progId switch
            {
                var value when value.EndsWith("17.0", StringComparison.OrdinalIgnoreCase) => "17.",
                var value when value.EndsWith("16.0", StringComparison.OrdinalIgnoreCase) => "16.",
                var value when value.EndsWith("15.0", StringComparison.OrdinalIgnoreCase) => "15.",
                _ => null
            };
            var xaeCandidates = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432")
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => Path.Combine(root, "Beckhoff", "TcXaeShell", "Common7", "IDE", "TcXaeShell.exe"));

            return xaeCandidates.FirstOrDefault(path =>
                File.Exists(path) &&
                (expectedProductVersion is null ||
                 FileVersionInfo.GetVersionInfo(path).ProductVersion?.StartsWith(
                     expectedProductVersion,
                     StringComparison.OrdinalIgnoreCase) == true));
        }

        var vswherePath = FindVsWhereExecutable();
        if (vswherePath is not null)
        {
            var discoveredPath = FindVisualStudioWithVsWhere(vswherePath, progId);
            if (discoveredPath is not null)
                return discoveredPath;
        }

        var installationFolders = progId switch
        {
            var value when value.EndsWith("18.0", StringComparison.OrdinalIgnoreCase) => new[] { "18" },
            var value when value.EndsWith("17.0", StringComparison.OrdinalIgnoreCase) => new[] { "2022", "17" },
            var value when value.EndsWith("16.0", StringComparison.OrdinalIgnoreCase) => new[] { "2019", "16" },
            _ => new[] { "2017", "15" }
        };
        var programFilesRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432")
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => path!)
        .Distinct(StringComparer.OrdinalIgnoreCase);
        var candidates = programFilesRoots
            .SelectMany(root => installationFolders
                .SelectMany(folder => new[] { "Community", "Professional", "Enterprise" }
                    .Select(edition => Path.Combine(
                        root,
                        "Microsoft Visual Studio", folder, edition, "Common7", "IDE", "devenv.exe"))))
            .ToArray();

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindVsWhereExecutable()
    {
        var programFilesRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("ProgramW6432")
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => path!)
        .Distinct(StringComparer.OrdinalIgnoreCase);

        return programFilesRoots
            .Select(root => Path.Combine(root, "Microsoft Visual Studio", "Installer", "vswhere.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static string? FindVisualStudioWithVsWhere(string vswherePath, string progId)
    {
        var versionRange = progId switch
        {
            var value when value.EndsWith("18.0", StringComparison.OrdinalIgnoreCase) => "[18.0,19.0)",
            var value when value.EndsWith("17.0", StringComparison.OrdinalIgnoreCase) => "[17.0,18.0)",
            var value when value.EndsWith("16.0", StringComparison.OrdinalIgnoreCase) => "[16.0,17.0)",
            _ => "[15.0,16.0)"
        };

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = vswherePath,
                Arguments = $"-version \"{versionRange}\" -products * -latest -property productPath",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (process is null || !process.WaitForExit(5000))
                return null;

            var path = process.StandardOutput.ReadToEnd().Trim();
            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            // vswhere is only an additional discovery mechanism. The registry
            // and conventional install paths remain available as fallbacks.
            return null;
        }
    }

    private static string? FindRegisteredAutomationExecutable(string progId)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var classesRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                var executable = FindRegisteredAutomationExecutable(classesRoot, progId);
                if (executable is not null)
                    return executable;
            }
            catch (SecurityException)
            {
                // Continue with the other registry view.
            }
            catch (UnauthorizedAccessException)
            {
                // Continue with the other registry view.
            }
        }

        return null;
    }

    private static string? FindRegisteredAutomationExecutable(RegistryKey classesRoot, string progId)
    {
        using var progIdKey = classesRoot.OpenSubKey($"{progId}\\CLSID");
        var clsid = progIdKey?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(clsid))
            return null;

        using var serverKey = classesRoot.OpenSubKey($"CLSID\\{clsid}\\LocalServer32") ??
                               classesRoot.OpenSubKey($"Wow6432Node\\CLSID\\{clsid}\\LocalServer32");
        var command = serverKey?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var executable = command.Trim();
        if (executable.StartsWith('"'))
        {
            var closingQuote = executable.IndexOf('"', 1);
            if (closingQuote > 1)
                executable = executable[1..closingQuote];
        }
        else
        {
            var firstSpace = executable.IndexOf(' ');
            if (firstSpace > 0)
                executable = executable[..firstSpace];
        }

        return File.Exists(executable) ? executable : null;
    }

    private static object? TryGetRunningInstance(string progId, int? processId = null)
    {
        if (processId.HasValue || progId.StartsWith("VisualStudio.", StringComparison.OrdinalIgnoreCase))
            return FindRunningMonikerInstance(progId, processId);

        object? instance = null;
        try
        {
            var clsid = Guid.Empty;
            var clsidResult = CLSIDFromProgID(progId, out clsid);
            if (clsidResult != 0)
                return null;

            var result = GetActiveObject(ref clsid, IntPtr.Zero, out instance);
            if (result == 0 && instance is not null && IsUsableAutomationInstance(instance))
                return instance;

            ReleaseComObject(instance);
        }
        catch (COMException)
        {
            // Fall back to enumerating the ROT below. GetActiveObject can be
            // ambiguous or unavailable when several Visual Studio instances exist.
        }

        return FindRunningMonikerInstance(progId);
    }

    private static bool HasAnyHostProcess(string progId) =>
        Process.GetProcessesByName(GetProcessName(progId)).Length > 0;

    private static HashSet<int> GetHostProcessIds(string progId) =>
        Process.GetProcessesByName(GetProcessName(progId))
            .Select(process =>
            {
                try
                {
                    return process.Id;
                }
                finally
                {
                    process.Dispose();
                }
            })
            .ToHashSet();

    private static int? FindNewHostProcessId(string progId, IReadOnlySet<int> processIdsBeforeActivation)
    {
        return Process.GetProcessesByName(GetProcessName(progId))
            .Where(process => !processIdsBeforeActivation.Contains(process.Id))
            .OrderByDescending(process =>
            {
                try
                {
                    return process.StartTime;
                }
                catch (InvalidOperationException)
                {
                    return DateTime.MinValue;
                }
            })
            .Select(process =>
            {
                try
                {
                    return (int?)process.Id;
                }
                finally
                {
                    process.Dispose();
                }
            })
            .FirstOrDefault();
    }

    private static bool IsSameElevation(Process process)
    {
        var processElevation = TryGetProcessElevation(process);
        var currentElevation = TryGetProcessElevation(Process.GetCurrentProcess());
        return processElevation.HasValue && currentElevation.HasValue &&
               processElevation.Value == currentElevation.Value;
    }

    private static bool? TryGetProcessElevation(Process process)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        const uint TokenQuery = 0x0008;
        const int TokenElevation = 20;

        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (processHandle == IntPtr.Zero)
            return null;

        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
                return null;

            try
            {
                var elevation = new TokenElevation();
                var size = Marshal.SizeOf<TokenElevation>();
                if (!GetTokenInformation(tokenHandle, TokenElevation, ref elevation, size, out _))
                    return null;

                return elevation.TokenIsElevated != 0;
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private void OpenTwinCatSolution(dynamic twinCatSolution, string solutionPath)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                ExecuteCom("Opening the TwinCAT solution", () => twinCatSolution.Open(solutionPath));
                return;
            }
            catch (InvalidOperationException exception) when
                (attempt < maxAttempts && IsTransientAutomationError(exception.InnerException as COMException))
            {
                logger.Warning(
                    $"TwinCAT solution is still loading ({DescribeHResult(exception.InnerException as COMException)}); " +
                    $"retrying in 2 seconds ({attempt}/{maxAttempts - 1}).");
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }

        throw new InvalidOperationException("Opening the TwinCAT solution failed after several retries.");
    }

    private void WaitForTwinCatProjects(dynamic twinCatSolution)
    {
        var timeout = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < timeout)
        {
            try
            {
                if ((int)twinCatSolution.Projects.Count > 0)
                    return;
            }
            catch (COMException exception) when (IsTransientAutomationError(exception))
            {
                // The project system is still being initialized.
            }

            Thread.Sleep(1000);
        }

        throw new InvalidOperationException(
            "The TwinCAT solution opened, but no TwinCAT project became available within 60 seconds.");
    }

    private void ConfigureTwinCatSilentMode(dynamic ide, bool silent)
    {
        try
        {
            dynamic settings = ide.GetObject("TcAutomationSettings");
            settings.SilentMode = silent;
            logger.Info(silent
                ? "TwinCAT Automation SilentMode enabled."
                : "TwinCAT Automation SilentMode disabled (visible mode).");
        }
        catch (COMException exception) when (!silent)
        {
            // Older TwinCAT versions may not expose TcAutomationSettings. The
            // normal visible mode remains usable in that case.
            logger.Warning($"TwinCAT Automation SilentMode is unavailable: {exception.Message}");
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                "Could not enable TwinCAT Automation SilentMode.",
                exception);
        }
    }

    private void ConfigureIdeVisibility(dynamic ide, bool silent, bool ownsInstance)
    {
        if (!silent)
        {
            if (ownsInstance)
                ide.MainWindow.Visible = true;
            return;
        }

        if (!ownsInstance)
        {
            logger.Warning(
                "Silent mode is using an existing IDE instance; its current window visibility was left unchanged.");
            return;
        }

        try
        {
            ide.MainWindow.Visible = false;
            if ((bool)ide.MainWindow.Visible)
            {
                logger.Warning(
                    "Visual Studio did not accept the hidden-window request; the IDE may still show UI during startup.");
            }
            else
            {
                logger.Info("Visual Studio UI hidden for silent execution.");
            }
        }
        catch (COMException exception)
        {
            logger.Warning(
                "Could not verify the hidden Visual Studio window; silent mode may still show UI. " +
                $"HRESULT=0x{exception.HResult:X8} ({exception.Message})");
        }
    }

    private static bool IsSolutionAlreadyOpen(dynamic solution, string solutionPath)
    {
        try
        {
            var openPath = (string?)solution.FullName;
            return !string.IsNullOrWhiteSpace(openPath) &&
                   string.Equals(
                       Path.GetFullPath(openPath),
                       Path.GetFullPath(solutionPath),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (COMException)
        {
            return false;
        }
        catch (RuntimeBinderException)
        {
            return false;
        }
    }

    private void BuildTwinCatSolution(
        dynamic solutionBuild,
        dynamic twinCatSolution,
        string? targetProjectPath,
        string solutionConfigurationName)
    {
        // Build(false) returns immediately. BuildState values are the public
        // EnvDTE values: 1 = not started, 2 = in progress, 3 = done.
        if (targetProjectPath is null)
        {
            ExecuteCom("Starting the TwinCAT solution build", () => solutionBuild.Build(false));
        }
        else
        {
            var projectName = FindSolutionProjectName(twinCatSolution, targetProjectPath);
            logger.Info($"Starting build for selected TwinCAT project: {projectName}");
            ExecuteCom(
                "Starting the selected TwinCAT project build",
                () => solutionBuild.BuildProject(solutionConfigurationName, projectName));
        }

        logger.Info("Build request accepted; waiting for TwinCAT to finish...");

        var startedAt = DateTime.UtcNow;
        var nextProgressMessage = startedAt.AddSeconds(10);
        var acceptDoneAfter = startedAt.AddSeconds(2);
        var notStartedDeadline = startedAt.AddSeconds(60);
        var hasSeenInProgress = false;

        while (true)
        {
            int buildState;
            try
            {
                buildState = (int)solutionBuild.BuildState;
            }
            catch (COMException exception) when (IsTransientAutomationError(exception))
            {
                buildState = 2;
            }

            if (buildState == 2)
                hasSeenInProgress = true;

            if (buildState == 3 && (hasSeenInProgress || DateTime.UtcNow >= acceptDoneAfter))
                return;

            if (buildState == 1 && DateTime.UtcNow >= notStartedDeadline)
            {
                throw new InvalidOperationException(
                    "TwinCAT accepted the build request, but the build did not start within 60 seconds.");
            }

            if (DateTime.UtcNow >= nextProgressMessage)
            {
                var elapsed = DateTime.UtcNow - startedAt;
                var stateDescription = buildState switch
                {
                    1 => "queued",
                    2 => "in progress",
                    3 => "finishing",
                    _ => $"state {buildState}"
                };
                logger.Info($"TwinCAT build still {stateDescription} ({elapsed.TotalSeconds:F0}s elapsed)...");
                nextProgressMessage = DateTime.UtcNow.AddSeconds(10);
            }

            Thread.Sleep(500);
        }
    }

    private void LogBuildErrors(dynamic ide)
    {
        try
        {
            // ToolWindows is exposed by the EnvDTE80 DTE2 interface. Some
            // XAE versions return the DTE through a late-bound RCW where the
            // dynamic binder cannot discover that inherited COM interface.
            var dte2 = (EnvDTE80.DTE2)ide;
            EnvDTE80.ErrorItems errorItems = dte2.ToolWindows.ErrorList.ErrorItems;
            var count = (int)errorItems.Count;
            if (count == 0)
                return;

            logger.Info($"Visual Studio Error List contains {count} item(s):");
            for (var index = 1; index <= count; index++)
            {
                var item = errorItems.Item(index);
                var description = item.Description ?? "<no description>";
                var fileName = item.FileName;
                var line = item.Line;
                var location = string.IsNullOrWhiteSpace(fileName)
                    ? string.Empty
                    : $" ({fileName}{(line is > 0 ? $":{line}" : string.Empty)})";
                logger.Error($"  {description}{location}");
            }
        }
        catch (Exception exception)
        {
            logger.Warning($"Could not read the Visual Studio Error List: {exception.Message}");
        }
    }

    private static bool IsTransientAutomationError(COMException? exception) => exception is not null &&
        (exception.HResult == unchecked((int)0x80004004) || // E_ABORT
         exception.HResult == unchecked((int)0x80010001) || // RPC_E_CALL_REJECTED
         exception.HResult == unchecked((int)0x8001010A) || // RPC_E_SERVERCALL_RETRYLATER
         exception.HResult == unchecked((int)0x80010105) || // RPC_E_SERVERFAULT
         exception.HResult == unchecked((int)0x80010108));  // RPC_E_DISCONNECTED

    private static string DescribeHResult(COMException? exception) => exception is null
        ? "a transient COM error"
        : $"HRESULT=0x{exception.HResult:X8} ({exception.Message})";

    private static string DescribeElevation(bool? elevated) => elevated switch
    {
        true => "yes",
        false => "no",
        null => "unknown"
    };

    private static object? FindRunningMonikerInstance(string progId, int? processId = null)
    {
        if (GetRunningObjectTable(0, out var runningObjectTable) != 0)
            return null;

        if (CreateBindCtx(0, out var bindContext) != 0)
            return null;

        try
        {
            runningObjectTable.EnumRunning(out var monikerEnumerator);
            var monikers = new ComTypes.IMoniker[1];
            while (monikerEnumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    moniker.GetDisplayName(bindContext, null, out var displayName);
                    var expectedMoniker = processId.HasValue ? $"{progId}:{processId.Value}" : null;
                    var matches = expectedMoniker is null
                        ? displayName?.Contains(progId, StringComparison.OrdinalIgnoreCase) == true
                        : displayName?.EndsWith(expectedMoniker, StringComparison.OrdinalIgnoreCase) == true;
                    if (matches)
                    {
                        runningObjectTable.GetObject(moniker, out var instance);
                        if (instance is not null && IsUsableAutomationInstance(instance))
                            return instance;

                        ReleaseComObject(instance);
                    }
                }
                finally
                {
                    ReleaseComObject(moniker);
                }
            }
        }
        finally
        {
            ReleaseComObject(bindContext);
            ReleaseComObject(runningObjectTable);
        }

        return null;
    }

    private static bool IsUsableAutomationInstance(object instance)
    {
        try
        {
            dynamic dte = instance;
            _ = dte.SuppressUI;
            _ = dte.Solution;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (RuntimeBinderException)
        {
            return false;
        }
    }

    private string ActivateConfiguration(dynamic solutionBuild, string? configuration, string platform)
    {
        var retryUntil = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                dynamic configurations = solutionBuild.SolutionConfigurations;
                var count = (int)configurations.Count;
                logger.Info($"Found {count} TwinCAT solution configuration(s).");
                for (var index = 1; index <= count; index++)
                {
                    logger.Info($"  Reading TwinCAT solution configuration #{index}...");
                    dynamic candidate = configurations.Item(index);
                    var parts = GetSolutionConfigurationParts((object)candidate);
                    logger.Info($"  {parts.ConfigurationName}|{parts.PlatformName}");
                    if ((configuration is null || string.Equals(parts.ConfigurationName, configuration, StringComparison.OrdinalIgnoreCase)) &&
                        string.Equals(parts.PlatformName, platform, StringComparison.OrdinalIgnoreCase))
                    {
                        ExecuteCom("Activating the TwinCAT solution configuration", () => candidate.Activate());
                        return parts.ConfigurationName;
                    }
                }

                throw new InvalidOperationException(configuration is null
                    ? $"TwinCAT platform '{platform}' was not found in the opened solution."
                    : $"TwinCAT configuration '{configuration}' with platform '{platform}' was not found.");
            }
            catch (Exception exception) when (DateTime.UtcNow < retryUntil && IsTransientAutomationError(FindComException(exception)))
            {
                var comException = FindComException(exception);
                logger.Warning(
                    $"TwinCAT solution configurations are still initializing ({DescribeHResult(comException)}); " +
                    "retrying in 1 second.");
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static COMException? FindComException(Exception exception) => exception switch
    {
        COMException comException => comException,
        InvalidOperationException { InnerException: COMException comException } => comException,
        _ => null
    };

    private static (string ConfigurationName, string PlatformName) GetSolutionConfigurationParts(object candidate)
    {
        dynamic dynamicCandidate = candidate;
        var name = (string)dynamicCandidate.Name;
        string? platform = null;
        try
        {
            platform = (string?)dynamicCandidate.PlatformName;
        }
        catch (RuntimeBinderException)
        {
            // Some TwinCAT/XAE versions expose the platform on SolutionContexts
            // instead of directly on SolutionConfiguration.
        }

        if (string.IsNullOrWhiteSpace(platform))
        {
            try
            {
                dynamic contexts = dynamicCandidate.SolutionContexts;
                for (var index = 1; index <= (int)contexts.Count; index++)
                {
                    dynamic context = contexts.Item(index);
                    platform = (string?)context.PlatformName;
                    if (!string.IsNullOrWhiteSpace(platform))
                        break;
                }
            }
            catch (RuntimeBinderException)
            {
                // Fall through to parsing the combined name below.
            }
        }

        if (!string.IsNullOrWhiteSpace(platform))
            return (name, platform);

        var separator = name.LastIndexOf('|');
        if (separator > 0 && separator < name.Length - 1)
            return (name[..separator], name[(separator + 1)..]);

        return (name, string.Empty);
    }

    private void ExecuteCom(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                $"{operation} failed. HRESULT=0x{exception.HResult:X8} ({exception.Message})",
                exception);
        }
    }

    private void QuitAndRelease(object? dte, bool ownsInstance, int? processId, string? progId)
    {
        if (dte is null)
            return;

        try
        {
            if (ownsInstance && IsOwnedHostRunning(processId, progId))
            {
                Exception? failure = null;
                var quitThread = new Thread(() =>
                {
                    try
                    {
                        ((dynamic)dte).Quit();
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                })
                {
                    IsBackground = true,
                    Name = "Tc3Build automation host cleanup"
                };
                quitThread.SetApartmentState(ApartmentState.STA);
                quitThread.Start();
                if (!quitThread.Join(TimeSpan.FromSeconds(15)))
                {
                    logger.Warning("The automation host did not quit within 15 seconds; stopping the host started by Tc3Build.");
                    StopOwnedHostProcess(processId, progId);
                    quitThread.Join(TimeSpan.FromSeconds(5));
                }

                if (failure is not null)
                {
                    logger.Warning(
                        $"The automation host reported a cleanup error; the build result is unchanged. {failure.Message}");
                }
            }
        }
        catch (COMException exception)
        {
            // TwinCAT/Visual Studio may disconnect its DTE while the solution
            // is being closed. The build result must not be changed by this
            // best-effort host cleanup.
            if (IsExpectedHostDisconnect(exception))
            {
                logger.Info("Automation host was already disconnected after the solution was closed.");
            }
            else
            {
                logger.Warning(
                    "The automation host disconnected while it was being closed; cleanup was completed as far as possible. " +
                    $"HRESULT=0x{exception.HResult:X8} ({exception.Message})");
            }
        }
        catch (RuntimeBinderException exception)
        {
            logger.Warning(
                $"The automation host was already closed during cleanup: {exception.Message}");
        }
        finally
        {
            ReleaseComObject(dte);
        }
    }

    private void CloseSolutionWithoutSaving(
        object? solution,
        bool closeSolution,
        int? processId,
        string? progId)
    {
        if (!closeSolution || solution is null)
            return;

        Exception? failure = null;
        var closeThread = new Thread(() =>
        {
            try
            {
                ((EnvDTE.Solution)solution).Close(SaveFirst: false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "Tc3Build solution cleanup"
        };

        try
        {
            closeThread.SetApartmentState(ApartmentState.STA);
            closeThread.Start();
            if (!closeThread.Join(TimeSpan.FromSeconds(15)))
            {
                logger.Warning("The TwinCAT solution did not close within 15 seconds; stopping the host started by Tc3Build.");
                StopOwnedHostProcess(processId, progId);
                closeThread.Join(TimeSpan.FromSeconds(5));
            }

            if (failure is COMException exception)
                throw exception;
            if (failure is not null)
                throw new InvalidOperationException(failure.Message, failure);

            logger.Info("TwinCAT solution closed without saving changes.");
        }
        catch (COMException exception)
        {
            logger.Warning(
                "Could not close the TwinCAT solution without saving; the IDE may show a save prompt. " +
                $"HRESULT=0x{exception.HResult:X8} ({exception.Message})");
        }
    }

    private static bool IsOwnedHostRunning(int? processId, string? progId)
    {
        if (!processId.HasValue || string.IsNullOrWhiteSpace(progId))
            return true;

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return !process.HasExited &&
                   string.Equals(process.ProcessName, GetProcessName(progId), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void StopOwnedHostProcess(int? processId, string? progId)
    {
        if (!processId.HasValue || string.IsNullOrWhiteSpace(progId))
            return;

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (process.HasExited ||
                !string.Equals(process.ProcessName, GetProcessName(progId), StringComparison.OrdinalIgnoreCase))
                return;

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            logger.Info($"Stopped the automation host process {processId.Value} started by Tc3Build.");
        }
        catch (ArgumentException)
        {
            // The host exited between the state check and cleanup.
        }
        catch (InvalidOperationException)
        {
            // The host exited between the state check and cleanup.
        }
    }

    private static bool IsExpectedHostDisconnect(COMException exception) =>
        exception.HResult == unchecked((int)0x800706BA) || // RPC_S_SERVER_UNAVAILABLE
        exception.HResult == unchecked((int)0x800706BE) || // RPC_S_CALL_FAILED
        exception.HResult == unchecked((int)0x80010108);   // RPC_E_DISCONNECTED

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private sealed record AutomationHost(object Instance, string ProgId, bool OwnsInstance, int? ProcessId);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(
        ref Guid clsid,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        ref TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(
        uint reserved,
        out ComTypes.IRunningObjectTable runningObjectTable);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(
        uint reserved,
        out ComTypes.IBindCtx bindContext);
}
