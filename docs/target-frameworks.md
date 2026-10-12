# Target Frameworks

## Intended Contract

The RequestBatcher library supports `net8.0` and `net10.0`. The package exposes the same public API on both target
frameworks; consumers can select the asset compatible with their application target. The library does not add a
framework-specific API or require a new production dependency for the additional target.

The unit-test project targets both `net8.0` and `net10.0` so the same behavioral test suite validates each library
asset. Benchmarks and the deduplication sample remain `net8.0` targets because they are execution environments rather
than package compatibility checks.

## Build Selection

The SDK selected by `global.json` remains authoritative, and the repository-wide C# language version remains C# 12.
Multi-target projects use the SDK's standard `TargetFrameworks` selection. A restore, build, test, or pack command that
does not specify `-f` processes both `net8.0` and `net10.0` targets for the library and unit tests. A command may use
`-f net8.0` or `-f net10.0` when iterating on one target, but release validation must cover both.

## Compatibility

The `net8.0` asset preserves compatibility with existing .NET 8 applications. The `net10.0` asset lets .NET 10
applications consume a native target while using the same API and behavior. No target framework change is made to the
benchmark or sample projects.

## Testing

The standard solution restore and build compile both library targets. The unit-test project runs its tests for both
targets, and the package is packed from the multi-target library. CI should keep using the project-level commands so
both target frameworks remain part of restore, build, test, and pack validation.
