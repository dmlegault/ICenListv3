# `deploy/` — built sample payloads

Generated, not authored. Each of the four reference apps under [`samples/`](../samples/) builds
straight into `deploy/<AppName>/` (via the project's `OutputPath`), producing a directory shaped
exactly like an already-deployed application — the same layout a runner is handed as `--app`, and
the same bytes `enlist-deploy` zips and uploads. `dotnet build` on a sample recreates its folder
here, so the whole directory is disposable and safe to delete before a rebuild.

Not to be confused with [`src/Enlist.Deploy/`](../src/Enlist.Deploy/), which is the `enlist-deploy`
CLI project. This directory is that CLI's typical *input*, not the tool itself.

Currently: `SampleService`, `OrderProcessor`, `DataPipeline` (net10.0) and `LegacySample` (net472).

**What reads it**

- The test suite, through `RepoPaths` in `tests/Enlist.TestSupport/` (`SampleServiceDir()` and
  friends point here) — including the container tests, which bind-mount these folders read-only.
- The samples' own `--dev` / `--check` launch profiles (`--app ../../deploy/<AppName>`).
- The setup walkthrough's `enlist-deploy --source deploy/<AppName>`.

**Documented in detail** in [`docs/01-start-here/Developer-Setup-Guide.md`](../docs/01-start-here/Developer-Setup-Guide.md)
§6 (the per-app table of services, jobs and runtimes, and the build-and-deploy steps), and named in
the [root `README.md`](../README.md) project-layout table.
