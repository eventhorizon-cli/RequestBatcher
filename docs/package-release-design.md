# Package releases

## Contract

`RequestBatcher` and `RequestBatcher.OpenTelemetry` have independent stable versions derived from Git tags.
`core-v<major>.<minor>.<patch>` publishes only `RequestBatcher`; `otel-v<major>.<minor>.<patch>` publishes only
`RequestBatcher.OpenTelemetry`. Tags must reference a commit reachable from `main`. Merge changes before tagging.
Project files contain no fixed release versions. Existing unscoped tags remain historical releases.

## Release flow

The release workflow parses the tag inline, following the package-specific release convention used by
[EventHorizon.RocketMQ](https://github.com/eventhorizon-cli/EventHorizon.RocketMQ/tree/main/.github/workflows).
It validates a stable three-part version and maps the prefix to one package and project. Shared build properties map
the tag-derived `RequestBatcherVersion` and `RequestBatcherOpenTelemetryVersion` environment variables to the corresponding
project's Version. Restore, format, build, and tests use those same values. No solution-wide Version override is used.

For an otel release, the latest stable `core-v*` tag reachable from the release commit supplies the minimum core version.
Core production sources must match that core tag, and the required core package must already be downloadable from NuGet.
This lets an integration-only version advance without publishing a matching core version. When both packages change,
publish core first, then otel. The core and integration release streams are serialized independently.

The workflow builds and tests the solution but packs and pushes only the selected project. After pushing, a bounded
registry check verifies that the selected package is downloadable. One GitHub Release is then published for that tag,
containing only the selected package's `.nupkg` and `.snupkg`. Completion requires both NuGet availability and a public
GitHub Release. A rerun can skip an existing NuGet version and replace its release assets.

## Compatibility and validation

Package identifiers and source/meter names remain stable. Versions of the two packages need not match; the integration
package declares a minimum compatible core version. Ordinary PR CI continues to build, test, and pack both projects
without publishing them. No additional release script or scripting runtime is required.

Validation exercises both inline tag routes, rejects malformed and prerelease tags, and checks that only one package is
selected. Local packaging uses different core and otel versions and inspects the generated NuGet dependency metadata.
Existing unit and integration tests validate the runtime changes. Release CI also preserves the main-ancestry check.

## Publishing

After merging and updating the local `main`, tag the desired commit and push only that tag:

```bash
git tag core-v0.3.4
git push origin core-v0.3.4
```

Wait for the workflow, the core NuGet package, and its GitHub Release to complete before publishing a dependent update:

```bash
git tag otel-v0.3.4
git push origin otel-v0.3.4
```

For an integration-only update, use its own next version, such as `otel-v0.3.5`; no new core tag is needed when core
production sources are unchanged. Published package versions and tags are immutable; use a new patch version for fixes.
