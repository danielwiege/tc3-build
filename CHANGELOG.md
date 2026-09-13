# Changelog

All notable changes to Tc3Build are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

No unreleased changes yet.

## [0.2.0] - 2026-09-13

### Added

- `Tc3Build` as a NuGet-packaged .NET tool for reproducible CI/CD installation.
- `Tc3Build.Core` as a reusable NuGet library for direct use from C# projects.
- A public `Tc3BuildRunner` API with typed operation and request models.
- ZIP and NuGet packaging metadata for the same implementation.

## [0.1.0] - 2026-09-13

### Added

- CLI commands for `build`, `validate`, `activate`, and `install-library`.
- Direct selection of `.tsproj`, `.tspproj`, and `.plcproj` projects.
- Selection of a project by name inside a TwinCAT solution.
- Explicit all-project builds with `-A`/`--all-projects`.
- Visual Studio-first automation host selection with support for VS2026, VS2022, VS2019, and TwinCAT XAE Shell variants.
- Silent execution with `-s`/`--silent` and timestamped console logging.
- Host selection with `-H`/`--host` and version output with `-v`/`--version`.
- PLC library output selection with `-o`/`--library-output`.
- Deterministic command-line tests and opt-in real-IDE integration tests.
- Standard Visual Studio and TwinCAT generated-file exclusions in `.gitignore`.

### Fixed

- TwinCAT solution platform and configuration selection.
- Visual Studio/XAE startup and COM automation retries while the IDE is loading.
- Silent-mode startup dialogs and save prompts when Tc3Build owns the IDE process.
- Automatic fallback from modern `.slnx` to the adjacent classic `.sln` where required by the selected automation host.

[Unreleased]: https://github.com/danielwiege/tc3-build/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/danielwiege/tc3-build/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/danielwiege/tc3-build/releases/tag/v0.1.0
