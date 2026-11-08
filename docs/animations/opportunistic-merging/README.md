# Opportunistic merging animation

The READMEs embed self-contained animated SVGs:
[English](../../assets/request-batcher-conveyor.svg) and
[Simplified Chinese](../../assets/request-batcher-conveyor.zh-CN.svg).
Open either SVG directly in a browser to play its 15-second loop. Native SVG animation handles
request movement, batch progress, counters, and narration without JavaScript or an HTML wrapper.
The SVG files are also their editable sources. Reduced-motion preferences select a static
midpoint with two queued requests, two active batches, and one completed request.

For playback controls, open `index.html` directly in a browser. No build, server, or external
dependencies are required. This optional HTML version supports English and Simplified Chinese,
pause, replay, playback speed, and timeline seeking. Use `index.html?lang=zh-CN` to start in Chinese.
Reduced-motion preferences start HTML playback paused. The HTML timeline uses logical seconds
from 0 to 10; its default playback speed is 2/3×, matching the SVG's 15-second loop.

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

- `../../assets/request-batcher-conveyor.svg` and `.zh-CN.svg`: native animation source and README assets.
- `index.html`: accessible page structure and inline SVG scene.
- `styles.css`: layout, typography, and colors.
- `animation.js`: shared bilingual labels, deterministic timeline, and playback controls.
- `export.json`: crop, timing, seek operation, and expected state checkpoints for GIF export.

Keep the SVG and HTML versions aligned with the same relative arrival and completion times,
batch membership, labels, and narration. SVG time is 1.5 times HTML timeline time. Preserve
matching geometry and timing between languages. The HTML page works independently
of these assets and is not an intermediate step for SVG output.

## Optional GIF export

Generate GIF only when requested. GIF rendering uses the global `render-technical-animations`
Codex skill. Its Python renderer and Playwright/Pillow dependencies live outside this repository.
After installing the skill and following its setup instructions, run from the repository root:

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
captures 20 frames per second for a 15-second loop, hides playback controls,
and checks queue, active-batch, and completed-request counts before writing the GIF.

## Visual inspection

Inspect both languages at idle, queued, dispatch, and completed states. Open the SVG directly,
then use the browser console to pause and seek deterministically:

```js
document.documentElement.pauseAnimations();
document.documentElement.setCurrentTime(8.625);
```

Check both sides of the SVG events at 0.75, 2.25, 3.75, 5.25, 7.875, 9.75, 12.375, 13.5, and 15 seconds,
including a second loop. Queue, active-batch, and completed counts must agree with request IDs,
slot labels, and narration. Also test autonomous playback through an `<img>` element, narrow
image widths, and the reduced-motion still. Page-level media emulation may not propagate into
an SVG image; direct SVG reduced-motion rendering and image-context animation are checked
separately, while image-context reduced-motion behavior needs verification with browser or OS
preferences. HTML controls can be checked by seeking to the corresponding logical timeline time; resize the
page and check keyboard operation of playback and timeline controls.
