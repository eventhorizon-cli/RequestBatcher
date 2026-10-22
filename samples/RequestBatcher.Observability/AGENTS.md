# AGENTS.md

These instructions apply to the OpenTelemetry observability sample and its validation.

- Run the Docker Compose stack only for a bounded validation session. Stop it as soon as the required traces, metrics,
  screenshots, or test results have been collected; do not leave it running during unrelated work or after the task.
- Arrange cleanup before starting the stack, using `try`/`finally` in C# automation or an exit trap for a shell session.
  Cleanup must also run after startup failures, failed assertions, timeouts, and cancellation. If an interruption prevents
  automatic cleanup, shut down the stack when work resumes before continuing with other tasks.
- From the repository root, shut down the sample with:

  ```bash
  docker compose -f samples/RequestBatcher.Observability/compose.yaml down
  docker compose -f samples/RequestBatcher.Observability/compose.yaml ps --all
  ```

- Verify that no sample containers remain. If a custom Compose project name or override file was used for startup,
  use the same options for cleanup and verification. Preserve named volumes unless the user requests a reset or the
  validation explicitly created disposable data.
- Leave the stack running only when the user explicitly requests it, and state that it remains running when handing off.
