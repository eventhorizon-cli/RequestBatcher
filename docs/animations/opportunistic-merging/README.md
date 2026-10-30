# Opportunistic merging animation

Open `index.html` directly in a browser. No build, server, or external dependencies are required.
The page supports English and Simplified Chinese, pause, replay, playback speed, and timeline seeking.
One loop takes 10 seconds at the default playback speed.
Use `index.html?lang=zh-CN` to start in Chinese. Reduced-motion preferences start playback paused.

## Diagram contract

This is an illustrative single-partition timeline, not a measured execution trace.
`MaxConcurrency = 2` and `BatchSize = 4`. The two labeled areas represent Handler execution
slots, not dedicated threads or independent consumers. `BatchDispatchLoop` reserves a slot before
pulling the requests currently queued, up to four. A dispatched batch has fixed membership.
Terminology follows the [dispatch design](../../batch-dispatch-redesign.md): pending requests in
BufferQueue, one dispatch loop, execution slots, and Handler batches. A and B identify visual
positions only; execution permits have no public identity.

Requests 01 and 02 each dispatch immediately on arrival. While both slots are busy,
requests 03–05 arrive together, followed by another group, 06–08. These are simultaneous arrivals,
not a claim that an explicit batch submission becomes one Handler call. When 01 completes,
the loop acquires slot A and dispatches 03–06. When 02 completes, the loop acquires slot B and
dispatches 07–08 without waiting for another two requests. The final two-request batch
finishes before the four-request batch; the library does not guarantee execution ordering.
Every completed request receives its own result. No polling timer or collection window is shown.
Conveyor motion and transfer duration are visual metaphors, not additional scheduling delays.
Cancellation, failure, shutdown, admission capacity, and multiple partitions are omitted.
Pending requests are in memory and cannot be recovered after process failure.

## Source and exported assets

- `index.html`: accessible page structure and inline SVG scene.
- `styles.css`: layout, typography, and colors.
- `animation.js`: shared bilingual labels, deterministic timeline, and playback controls.
- `export.json`: crop, timing, seek operation, and expected state checkpoints for GIF export.

Keep editable sources here. The README embeds the looping GIFs in `docs/assets/`:
`request-batcher-conveyor.gif` and `request-batcher-conveyor.zh-CN.gif`. No static PNG is exported.
The HTML page also works independently of these assets.

Rendering uses the global `render-technical-animations` Codex skill. Its Python renderer and
Playwright/Pillow dependencies live outside this repository. After installing the skill and
following its setup instructions, run from the repository root:

```sh
~/.cache/codex/render-technical-animations/venv/bin/python \
  ~/.codex/skills/render-technical-animations/scripts/render_animation.py \
  --source docs/animations/opportunistic-merging/index.html \
  --config docs/animations/opportunistic-merging/export.json \
  --output docs/assets/request-batcher-conveyor.gif --query lang=en
```

Repeat with `--query lang=zh-CN` and output `docs/assets/request-batcher-conveyor.zh-CN.gif`.
Use `--browser /path/to/chrome` to select an installed Chrome executable instead of Playwright's
Chromium. The export renders a 1100-pixel-wide layout at a target width of 3840 pixels (3840 × 2363),
captures 20 frames per second for a 10-second loop, hides playback controls,
and checks queue, active-batch, and completed-request counts before writing the GIF.

For visual inspection, pause and seek to 0.5, 2, 4.5, 5.75, 7, and 9.5 seconds. Check that the queue,
active batches, and completed counts agree with the visible request IDs. Resize to a narrow
viewport and check keyboard operation of the playback and timeline controls.
