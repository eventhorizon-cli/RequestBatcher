"use strict";

// A deterministic illustration of one partition; all times are presentation seconds.
const duration = 10;
const batches = [
  { slot: 0, start: 0.5, end: 5.25, ids: [1] },
  { slot: 1, start: 1.5, end: 6.5, ids: [2] },
  { slot: 0, start: 5.25, end: 9, ids: [3, 4, 5, 6] },
  { slot: 1, start: 6.5, end: 8.25, ids: [7, 8] }
];
const arrivals = [0.5, 1.5, 2.5, 2.5, 2.5, 3.5, 3.5, 3.5];
const copy = {
  en: {
    eyebrow: "HOW IT WORKS", title: "An available slot. Immediate dispatch.",
    intro: "Take what is queued. Process it together. Never wait to fill a batch.",
    partition: "Single partition example", incoming: "PENDING REQUESTS", queueNote: "BufferQueue · one partition",
    dispatch: "BatchDispatchLoop", take: "Acquire slot → pull ≤ 4", results: "RESULTS",
    slotsNote: "Each Handler batch holds one execution slot until it completes.",
    timeNote: "Illustrative timing · conveyor motion adds no collection window",
    queued: "Queued", active: "Active batches", completed: "Completed requests",
    replay: "Replay", pause: "Pause", play: "Play", timeline: "Timeline", speed: "Playback speed",
    footer: "In-process, in-memory batching. Pending requests do not survive process failure.",
    slot: "Execution slot", idle: "Available", processing: "Occupied", size: "Batch size", waiting: "No active Handler batch",
    arriving: "Arrive together",
    sceneTitle: "Requests move from a queue to two execution slots",
    sceneDescription: "Two single requests dispatch immediately. Two groups of three arrive while both slots are occupied, then dispatch as Handler batches of four and two.",
    steps: [
      ["Ready when you are", "Two execution slots are available. No requests, no work to dispatch."],
      ["One request is enough", "The loop acquires slot A and dispatches request 01. BatchSize is a maximum, not a target."],
      ["Two batches can run together", "The loop acquires slot B and dispatches request 02 while the first Handler batch is still running."],
      ["Two groups arrive. Both slots are occupied.", "03–05 arrive together, followed by 06–08. All six queue until an execution slot is available."],
      ["A slot is released. Four go together.", "After 01 completes, the loop acquires slot A and pulls 03–06. Arrival groups need not match Handler batches."],
      ["Two remaining requests dispatch immediately", "After 02 completes, the loop acquires slot B and pulls 07–08 without waiting to fill a batch."],
      ["Batches can finish out of order", "The later two-request batch finishes first. The four-request batch still holds slot A."],
      ["Eight requests. Four handler calls.", "Every request has its result. Batch sizes were 1, 1, 4, and 2. Ready for the next arrivals."]
    ]
  },
  "zh-CN": {
    eyebrow: "请求如何合并", title: "有可用的执行名额，就立即分派。",
    intro: "请求到达后，有可用的执行名额就处理；名额用满时排队，不等凑满一批。",
    partition: "单分区示例", incoming: "待处理请求", queueNote: "BufferQueue · 单分区队列",
    dispatch: "BatchDispatchLoop", take: "先获取名额，再取最多 4 个", results: "返回结果",
    slotsNote: "每个 Handler 批次占用一个执行名额，完成后释放。",
    timeNote: "时间仅作演示 · 传送带运动不表示额外的收集等待",
    queued: "排队请求", active: "执行中批次", completed: "已完成请求",
    replay: "重播", pause: "暂停", play: "播放", timeline: "播放进度", speed: "播放速度",
    footer: "请求保存在进程内存中，进程故障后无法恢复待处理请求。",
    slot: "执行名额", idle: "可用", processing: "已占用", size: "本批请求", waiting: "无执行中的 Handler 批次",
    arriving: "同时到达",
    sceneTitle: "调度循环获取执行名额后拉取并分派请求",
    sceneDescription: "前两个单请求分别立即执行。两个执行名额占满时，两组各三个请求到达并排队，随后分成两个 Handler 批次，分别包含四个和两个请求。",
    steps: [
      ["等待请求到达", "当前没有请求。有可用的执行名额时，调度循环等待数据通知，不设收集时间窗口。"],
      ["只有一个，也立即处理", "调度循环获取执行名额 A，立即分派请求 01。BatchSize 是每批上限，无需凑满。"],
      ["两批请求，并行处理", "第一批仍在处理，调度循环获取执行名额 B，分派请求 02。两个名额都已占用。"],
      ["请求成组到达，等待执行名额", "03–05 同时到达，随后 06–08 一起到达。两个执行名额都已占用，六个请求先排队。"],
      ["名额释放，一次取走 4 个", "01 完成后，调度循环获取执行名额 A，拉取 03–06。到达时的分组与 Handler 批次不必一致。"],
      ["剩下 2 个，也立即分派", "02 完成后，调度循环获取执行名额 B，拉取 07–08，不再等待另外两个请求来凑满。"],
      ["后开始的一批，也可以先完成", "包含两个请求的批次先完成。包含四个请求的批次仍占用名额 A，批次不保证执行顺序。"],
      ["8 个请求，4 次 Handler 调用", "每个请求都拿到了结果。四批大小依次为 1、1、4、2，等待下一轮请求。"]
    ]
  }
};
let language = new URLSearchParams(location.search).get("lang") === "zh-CN" ? "zh-CN" : "en";
let time = 0;
let playing = !matchMedia("(prefers-reduced-motion: reduce)").matches;
let previous = null;
const byId = id => document.getElementById(id);
const clamp = n => Math.max(0, Math.min(1, n));
const smooth = n => { const p = clamp(n); return p * p * (3 - 2 * p); };
const number = id => String(id).padStart(2, "0");
const mix = (a, b, p) => a + (b - a) * p;

function request(id, x, y, completed = false) {
  return `<g transform="translate(${x.toFixed(2)} ${y.toFixed(2)})"><rect width="38" height="38" rx="7" fill="${completed ? "#e0f1e8" : "#eaf2ff"}" stroke="${completed ? "#68a584" : "#7296c2"}" stroke-width="1.5"/><text x="19" y="24" text-anchor="middle" class="request-id">${number(id)}</text></g>`;
}

function stepAt(t) {
  return t < 0.5 ? 0 : t < 1.5 ? 1 : t < 2.5 ? 2 : t < 5.25 ? 3 : t < 6.5 ? 4 : t < 8.25 ? 5 : t < 9 ? 6 : 7;
}

function renderStation(slot, activeBatch, t) {
  const c = copy[language];
  const y = slot === 0 ? 59 : 263;
  const progress = activeBatch ? clamp((t - activeBatch.start) / (activeBatch.end - activeBatch.start)) : 0;
  const color = activeBatch ? "#75529b" : "#7a8998";
  byId(slot === 0 ? "station-a" : "station-b").innerHTML = `
    <rect x="650" y="${y}" width="258" height="146" rx="12" fill="${activeBatch ? "#faf7fd" : "#f8fafb"}" stroke="${activeBatch ? "#c9b8dc" : "#dbe2e9"}" stroke-width="1.5"/>
    <circle cx="671" cy="${y + 27}" r="4" fill="${color}"/>
    <text x="684" y="${y + 33}" class="slot-name">${c.slot} ${slot === 0 ? "A" : "B"}</text>
    <text x="892" y="${y + 32}" text-anchor="end" class="slot-state" style="fill:${color}">${activeBatch ? c.processing : c.idle}</text>
    <rect x="671" y="${y + 50}" width="216" height="53" rx="7" fill="white" stroke="${activeBatch ? "#dfd4e9" : "#e4e9ee"}"/>
    ${activeBatch ? "" : `<text x="779" y="${y + 82}" text-anchor="middle" class="small-note">${c.waiting}</text>`}
    <rect x="671" y="${y + 116}" width="112" height="5" rx="2" fill="#e9e3ef"/>
    <rect x="671" y="${y + 116}" width="${112 * progress}" height="5" rx="2" fill="#9574b3"/>
    <text x="891" y="${y + 123}" text-anchor="end" class="slot-state">${c.size} ${activeBatch ? activeBatch.ids.length : "–"} / 4</text>`;
}

function render() {
  const c = copy[language];
  const active = batches.filter(b => time >= b.start && time < b.end);
  const done = batches.filter(b => time >= b.end).flatMap(b => b.ids);
  const queued = arrivals.map((arrival, i) => ({ id: i + 1, arrival }))
    .filter(r => time >= r.arrival && time < batches.find(b => b.ids.includes(r.id)).start);
  byId("queued-count").textContent = queued.length;
  byId("active-count").textContent = active.length;
  byId("completed-count").textContent = done.length;
  for (let slot = 0; slot < 2; slot++) renderStation(slot, active.find(b => b.slot === slot), time);

  // The belt is a visual metaphor. Dispatch decisions use the timeline, never travel distance.
  byId("belt-marks").innerHTML = Array.from({ length: 17 }, (_, i) => {
    const x = 17 + i * 32 + ((time * 28) % 32);
    return `<path d="M ${x} 194 V 272" stroke="#dce3ea" stroke-width="2"/>`;
  }).join("");
  let items = "";
  for (const [i, r] of queued.entries()) {
    const target = 422 - i * 50;
    // After the loop pulls four items for slot A, the remaining two move toward the head.
    const beforePull = r.id >= 7 && time >= 5.25 ? target - 200 : target;
    const position = mix(beforePull, target, smooth((time - 5.25) / 0.3));
    const groupEnd = r.id <= 5 ? 5 : 8;
    const startX = 48 + (groupEnd - r.id) * 50;
    items += request(r.id, mix(startX, position, smooth((time - r.arrival) / 0.375)), 214);
  }
  for (const b of active) {
    const p = smooth((time - b.start) / 0.4);
    b.ids.forEach((id, i) => {
      const startX = id < 3 ? 48 : 422 - i * 50;
      const endX = 681 + (b.ids.length - 1 - i) * 51;
      const endY = b.slot === 0 ? 117 : 321;
      items += request(id, mix(startX, endX, p), mix(214, endY, p));
    });
  }
  byId("requests").innerHTML = items;
  const arriving = time >= 2.5 && time < 3.25 ? "03 + 04 + 05" : time >= 3.5 && time < 4.25 ? "06 + 07 + 08" : "";
  byId("arrival-group").textContent = arriving ? `${c.arriving}: ${arriving}` : "";
  byId("result-items").innerHTML = done.map(id => {
    const x = 972 + ((id - 1) % 2) * 41;
    const y = 133 + Math.floor((id - 1) / 2) * 51;
    return request(id, x, y, true);
  }).join("");
  const step = stepAt(time);
  byId("step-number").textContent = number(step + 1);
  byId("step-title").textContent = c.steps[step][0];
  byId("step-description").textContent = c.steps[step][1];
  byId("timeline").value = time;
  byId("time").textContent = `${time.toFixed(1)} / ${duration} s`;
  byId("play").textContent = playing ? c.pause : c.play;
  byId("play").setAttribute("aria-pressed", String(playing));
}

function translate() {
  document.documentElement.lang = language;
  document.querySelectorAll("[data-copy]").forEach(el => { el.textContent = copy[language][el.dataset.copy]; });
  byId("language").textContent = language === "en" ? "中文" : "English";
  byId("language").setAttribute("aria-label", language === "en" ? "Switch to Simplified Chinese" : "切换到英文");
  byId("scene-title").textContent = copy[language].sceneTitle;
  byId("scene-description").textContent = copy[language].sceneDescription;
  document.querySelector(".demo").setAttribute("aria-label", copy[language].sceneTitle);
  document.title = `RequestBatcher · ${language === "en" ? "Opportunistic merging" : "机会式合并"}`;
  render();
}

byId("play").addEventListener("click", () => { playing = !playing; previous = null; render(); });
byId("replay").addEventListener("click", () => { time = 0; playing = true; previous = null; render(); });
byId("timeline").addEventListener("input", event => { time = Number(event.target.value); playing = false; previous = null; render(); });
byId("language").addEventListener("click", () => { language = language === "en" ? "zh-CN" : "en"; translate(); });
document.addEventListener("visibilitychange", () => { previous = null; });
function frame(now) {
  if (playing && previous !== null && !document.hidden) {
    time = (time + Math.min((now - previous) / 1000, 0.1) * Number(byId("speed").value)) % duration;
    render();
  }
  previous = now;
  requestAnimationFrame(frame);
}
translate();
requestAnimationFrame(frame);
