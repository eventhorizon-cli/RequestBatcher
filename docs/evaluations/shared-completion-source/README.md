# Shared explicit completion source

The internal shared completion directly derives from `TaskCompletionSource`.
All items still report their own terminal outcome into one remaining-item
counter, and the last item publishes the shared task. Exception identity and
aggregation, cancellation precedence, asynchronous continuations, and waiting
for all items remain unchanged. This removes one wrapper object per submission,
not one task per item. No task or pending state is pooled.

## Validation and measurement plan

Use the same benchmark source before and after the production change. A shared
completion primitive benchmark attributes allocation to the object layout;
public-API benchmarks measure full queue/handler/completion paths for groups of
10 and 1,000 items. Include success, observed handler failure, cancellation after
one item has entered the handler, capacity waiting, and mixed singles/groups.
Setups and input arrays stay outside measurement; validation and outcome
observation stay inside. Cancellation benchmarks must drain canceled queue
entries before the next operation. Do not interpret differences in timer or
scheduler behavior as a demonstrated throughput improvement.

Run deterministic completion/lifecycle regressions and the complete test suite
on both net8.0 and net10.0. Keep the benchmark runtime fixed at net8.0. Report
allocation and elapsed time independently, including neutral or negative
results, and retain the change only if a measurable allocation benefit does
not introduce a correctness or material performance regression.
