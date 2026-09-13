# tc3-build

tc3-build is a lightweight, C#-based command-line interface (CLI) designed to automate the build process of Beckhoff TwinCAT 3 projects. By leveraging the TwinCAT Automation Interface, this tool enables seamless integration of PLC projects into modern CI/CD pipelines.

## Build tool

The Visual Studio solution is `Tc3Build\Tc3Build.slnx`. The console project opens the selected TwinCAT solution through the installed Visual Studio Automation Interface (preferred) or TwinCAT XAE Shell and builds the selected project or explicitly all projects. Command-line parsing is provided by [System.CommandLine](https://github.com/dotnet/command-line-api).

The automation host preference is Visual Studio 2026 (`VisualStudio.DTE.18.0`), then Visual Studio 2022 and Visual Studio 2019, followed by the corresponding TwinCAT XAE Shell versions. A running Visual Studio/XAE instance is reused when its DTE is available; otherwise the registered host executable is started visibly and awaited with progress output. A COM message filter retries calls while the IDE is busy, and the tool additionally retries transient `E_ABORT`/RPC errors while the IDE finishes loading. For TwinCAT installations where the selected host cannot open the modern `.slnx` format through DTE, the tool automatically uses the adjacent classic `Project.sln` fallback.

Requirements:

- Visual Studio 2026 / .NET 10 SDK for compiling the console project
- TwinCAT XAE Shell installed on the machine that runs the build

Build and run from the repository root:

```powershell
dotnet build .\Tc3Build\Tc3Build.slnx
dotnet run --project .\Tc3Build\Tc3Build\Tc3Build.csproj -- build --project .\TwinCAT\TwinCAT.slnx --project-name Project --configuration Debug
```

Validate without building:

```powershell
dotnet run --project .\Tc3Build\Tc3Build\Tc3Build.csproj -- validate --project .\TwinCAT\TwinCAT.slnx --project-name Project
```

Activate an already built configuration:

```powershell
dotnet run --project .\Tc3Build\Tc3Build\Tc3Build.csproj -- activate --project .\TwinCAT\TwinCAT.slnx --project-name Project
```

Build and install a PLC library:

```powershell
dotnet run --project .\Tc3Build\Tc3Build\Tc3Build.csproj -- install-library --project .\TwinCAT\TwinCAT.slnx --project-name Library
```

Activation writes the configuration to the TwinCAT registry. The runtime is not restarted automatically.
Depending on the target configuration, activation can take several minutes; the CLI logs the elapsed time when it completes.
If `--platform` is omitted, the current TwinCAT target platform is reused. Specify it explicitly to switch targets.

The IDE UI is visible by default. Add `--silent` (`-s`) to `build`, `validate`, `activate`, or `install-library` for unattended execution; console logging remains enabled. Silent mode starts a dedicated automation host with the solution path already supplied, hides and verifies the Visual Studio window, and configures TwinCAT's `TcAutomationSettings.SilentMode`. This prevents the Visual Studio Get started/recent-project page during normal silent startup. If an already running IDE has to be reused, the tool reports that it cannot change that instance's visibility.

### Selecting the automation host

By default, `--host auto` selects the newest registered Visual Studio version first and then falls back to the newest available TwinCAT XAE Shell. The selected version can be forced with `--host` (`-H`):

```powershell
# Use Visual Studio 2019 only.
Tc3Build.exe build -p .\TwinCAT\TwinCAT.slnx -n Project --host vs2019

# Use the TwinCAT XAE Shell associated with Visual Studio 2019 only.
Tc3Build.exe build -p .\TwinCAT\TwinCAT.slnx -n Project --host xae2019

# Let Tc3Build choose: VS2026, VS2022, VS2019, XAE2022, XAE2019, XAE2017.
Tc3Build.exe build -p .\TwinCAT\TwinCAT.slnx -n Project --host auto
```

Supported host values are `auto`, `vs2026`, `vs2022`, `vs2019`, `xae2022`, `xae2019`, `xae2017`, and `xae`. The exact COM ProgID can also be supplied, for example `--host VisualStudio.DTE.16.0`. If a host is selected explicitly, Tc3Build does not silently switch to another version when that host cannot be started; this makes the selected toolchain unambiguous.

When Tc3Build owns the automation host, it closes the TwinCAT solution at the end with `SaveFirst=false`; pending solution/project changes are therefore discarded and no save confirmation is shown. An IDE instance that was already running is left open and is not closed by the tool.

The command arrangement is intentionally operation-based: `build` only builds, `validate` only validates, `activate` activates a configuration, and `install-library` builds and installs a PLC library. The library is saved as `<LibraryProject>.library` next to the `.tspproj` file. Use `--library-output` (`-o`) with `install-library` to choose another output path.

### Selecting a project

`--project` (`-p`) accepts either a solution or an individual TwinCAT project:

```powershell
# Select a project directly; its containing solution is found automatically.
Tc3Build.exe install-library -p .\TwinCAT\Library\Library.tspproj -s

# Select a project by name inside a solution.
Tc3Build.exe install-library -p .\TwinCAT\TwinCAT.slnx -n Library -s

# Select the XAE project by name.
Tc3Build.exe build -p .\TwinCAT\TwinCAT.slnx -n Project -c Debug -t "TwinCAT RT (x64)"

# Explicitly build every TwinCAT project in the solution.
Tc3Build.exe build -p .\TwinCAT\TwinCAT.slnx -A
```

If a solution contains multiple TwinCAT projects and neither `-n` nor `-A` is specified, Tc3Build stops and lists the available projects. This prevents an unintended complete solution build.

### Parameter examples

The following examples use the sample project paths from this repository:

| Parameter | Meaning | Complete example |
| --- | --- | --- |
| `-p`, `--project` | Solution or direct TwinCAT project path. | `Tc3Build.exe build -p .\TwinCAT\TwinCAT.slnx -n Project` |
| `-n`, `--project-name` | Project name or relative project path inside the selected solution. | `Tc3Build.exe build -p .\TwinCAT\TwinCAT.slnx -n Library` |
| `-c`, `--configuration` | TwinCAT build configuration, for example `Debug` or `Release`. | `Tc3Build.exe build -p .\TwinCAT\TwinCAT.slnx -n Project -c Debug` |
| `-t`, `--platform` | TwinCAT target platform. | `Tc3Build.exe build -p .\TwinCAT\TwinCAT.slnx -n Project -t "TwinCAT RT (x64)"` |
| `-s`, `--silent` | Hide the IDE while keeping console logging enabled. | `Tc3Build.exe build -p .\TwinCAT\TwinCAT.slnx -n Project -s` |
| `-H`, `--host` | Select the automation host or version. | `Tc3Build.exe build -p .\TwinCAT\TwinCAT.slnx -n Project -H vs2019` |
| `-o`, `--library-output` | Output file for `install-library`; the extension must be `.library` or `.compiled-library`. | `Tc3Build.exe install-library -p .\TwinCAT\Library\Library.tspproj -o .\artifacts\Tc3Build.library` |
| `-A`, `--all-projects` | Explicitly build every TwinCAT project in a solution. | `Tc3Build.exe build -p .\TwinCAT\TwinCAT.slnx -A` |

The options can be combined. For example, this builds only the library project in `Release` for TwinCAT RT, keeps the IDE hidden, and writes the generated file to a custom location:

```powershell
Tc3Build.exe install-library `
  -p .\TwinCAT\TwinCAT.slnx `
  -n Library `
  -c Release `
  -t "TwinCAT RT (x64)" `
  -o .\artifacts\Tc3Build.library `
  -s
```

Visual Studio/TwinCAT and `Tc3Build.exe` must run under the same Windows user and with the same elevation level. Otherwise the IDE may be visible but its DTE object is not available through the COM Running Object Table.

Use `--help` on the application or a command to display the available options.
The available short forms are `-p`/`--project`, `-n`/`--project-name`, `-c`/`--configuration`, `-t`/`--platform`, `-s`/`--silent`, `-H`/`--host`, `-o`/`--library-output`, `-A`/`--all-projects`, and `-v`/`--version`. The `--project`/`-p` option is required for `build`, `validate`, `activate`, and `install-library`.

### Tests

Run the deterministic unit tests with:

```powershell
dotnet test .\Tc3Build\Tc3Build.slnx -c Debug
```

The test suite covers the complete command handler contract: each command is invoked with a controlled executor and its resulting `BuildOptions` and exit code are verified. It also covers every supported option alias, host selection by key and COM ProgID, and the automatic Visual Studio/XAE priority. Real IDE/TwinCAT end-to-end tests are opt-in because they start external applications and can change the active TwinCAT configuration. When enabled, each configured host is started and used through the real `Tc3Build.exe` process for `build`, `validate`, `activate`, and `install-library`:

```powershell
$env:TC3BUILD_RUN_INTEGRATION_TESTS = "1"
$env:TC3BUILD_INTEGRATION_HOSTS = "vs2026,vs2022,vs2019,xae2022,xae2019,xae2017"
$env:TC3BUILD_INTEGRATION_PROJECT = "D:\Git\tc3-build\TwinCAT\TwinCAT.slnx"
$env:TC3BUILD_INTEGRATION_PROJECT_NAME = "Project"
$env:TC3BUILD_INTEGRATION_LIBRARY_PROJECT_NAME = "Library"
$env:TC3BUILD_INTEGRATION_CONFIGURATION = "Debug"
$env:TC3BUILD_INTEGRATION_PLATFORM = "TwinCAT RT (x64)"
dotnet test .\Tc3Build\Tc3Build.slnx -c Debug --filter FullyQualifiedName~AutomationHostIntegrationTests
```

Without `TC3BUILD_RUN_INTEGRATION_TESTS=1`, these four tests are reported as `Skipped`; they are never counted as successful tests without actually starting the application. With integration tests enabled, `build` and `validate` run immediately. `activate` and `install-library` are only included after their respective explicit opt-in variables are set, so a complete four-command run uses:

```powershell
$env:TC3BUILD_INTEGRATION_ALLOW_ACTIVATE = "1"
$env:TC3BUILD_INTEGRATION_ALLOW_INSTALL_LIBRARY = "1"
dotnet test .\Tc3Build\Tc3Build.slnx -c Debug --filter FullyQualifiedName~AutomationHostIntegrationTests
```

The integration tests invoke the actual executable copied next to the test assembly. Set `TC3BUILD_EXECUTABLE` to test another build output explicitly. By default the build test runs with the selected project name; omit `TC3BUILD_INTEGRATION_PROJECT_NAME` to test `--all-projects`. The build test performs a real build. The library test uses `TC3BUILD_INTEGRATION_LIBRARY_PROJECT_NAME` (for the sample solution: `Library`); alternatively set `TC3BUILD_INTEGRATION_LIBRARY_PROJECT` to a direct `.tspproj` path. Set `TC3BUILD_INTEGRATION_ALLOW_ACTIVATE=1` to allow the activation test and `TC3BUILD_INTEGRATION_ALLOW_INSTALL_LIBRARY=1` to allow the library installation test. Set `TC3BUILD_INTEGRATION_SILENT=0` if the IDE should remain visible. Hosts that are not installed should be removed from `TC3BUILD_INTEGRATION_HOSTS` for that machine.

For a focused diagnostic run, set `TC3BUILD_INTEGRATION_HOST_FILTER` to one host key or COM ProgID, for example `$env:TC3BUILD_INTEGRATION_HOST_FILTER = "xae2022"`. This limits the parameterized real-IDE tests to that host while keeping all four commands available.

In Visual Studio, select `Tc3Build\Tc3Build.Integration.runsettings` through **Test > Configure Run Settings > Select Solution Wide runsettings File**. Then run the tests from Test Explorer. The file contains all supported Visual Studio and TwinCAT XAE Shell hosts (`vs2026`, `vs2022`, `vs2019`, `xae2022`, `xae2019`, `xae2017`, and `xae`), and creates four real command tests for every host that is registered on the current machine. Unregistered hosts are automatically omitted from the real matrix; the catalog tests still verify that every supported host key and COM ProgID can be selected, and the CLI reports an unavailable host clearly. The tests are marked with the `Integration` category. Activation changes the active TwinCAT configuration, so keep `TC3BUILD_INTEGRATION_ALLOW_ACTIVATE` at `0` or remove it when only build, validation, and library installation should be tested.
