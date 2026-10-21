"use strict";

// ---------------------------------------------------------------- bridge
const pending = new Map();
let seq = 0;
const host = window.chrome && window.chrome.webview;

function call(cmd, args = {}) {
  return new Promise((resolve, reject) => {
    if (!host) return reject(new Error("Not running inside Hub"));
    const id = ++seq;
    pending.set(id, { resolve, reject });
    host.postMessage({ id, cmd, args });
  });
}

if (host) {
  host.addEventListener("message", (e) => {
    const m = e.data;
    if (m.id && pending.has(m.id)) {
      const p = pending.get(m.id);
      pending.delete(m.id);
      m.ok ? p.resolve(m.data) : p.reject(new Error(m.error));
    } else if (m.event === "state") {
      onState(m.data);
    } else if (m.event === "devices" && isVisible("system")) {
      renderDevices();
    }
  });
}

async function act(what, fn, okMessage) {
  try {
    const r = await fn();
    if (okMessage !== null) toast(okMessage || `${what} applied`);
    return r;
  } catch (err) {
    toast(`${what}: ${err.message}`, true);
    throw err;
  }
}

// ---------------------------------------------------------------- helpers
const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const h = (tag, attrs = {}, ...kids) => {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") el.className = v;
    else if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
    else if (k === "html") el.innerHTML = v;
    else el.setAttribute(k, v);
  }
  for (const kid of kids.flat()) if (kid != null) el.append(kid);
  return el;
};

let toastTimer;
function toast(msg, error = false) {
  const t = $("#toast");
  t.textContent = msg;
  t.className = "toast show" + (error ? " error" : "");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.className = "toast"), 2600);
}

function paintRange(input) {
  const min = +input.min, max = +input.max;
  input.style.setProperty("--fill", ((+input.value - min) / (max - min)) * 100 + "%");
}
document.addEventListener("input", (e) => { if (e.target.type === "range") paintRange(e.target); });
function setRange(input, value) { input.value = value; paintRange(input); }

function segmented(container, items, active, onPick) {
  container.replaceChildren(...items.map(({ value, label }) =>
    h("button", { class: value === active ? "active" : "", onclick: (e) => {
      $$("button", container).forEach((b) => b.classList.remove("active"));
      e.currentTarget.classList.add("active");
      onPick(value);
    } }, label)));
}

// ---------------------------------------------------------------- navigation
$$("#nav button").forEach((b) => b.addEventListener("click", () => showPage(b.dataset.page)));
function showPage(page) {
  $$("#nav button").forEach((x) => x.classList.toggle("active", x.dataset.page === page));
  $$(".page").forEach((p) => p.classList.toggle("active", p.id === "page-" + page));
  if (page === "mouse") loadMouse();
  if (page === "headset") loadHeadset();
  if (page === "audio") loadAudio();
  if (page === "settings") loadSettings();
  if (page === "system") loadSystem();
  setLive(page === "system" || page === "overview");
  setMouseTest(page === "mouse" && $("#mouseTabs [data-tab=test]").classList.contains("active"));
}
$$("#mouseTabs button").forEach((b) => b.addEventListener("click", () => {
  $$("#mouseTabs button").forEach((x) => x.classList.toggle("active", x === b));
  $$("#page-mouse .tab").forEach((t) => t.classList.toggle("active", t.dataset.tab === b.dataset.tab));
  if (b.dataset.tab === "advanced") drawCurve();
  setMouseTest(b.dataset.tab === "test");
}));

// ---------------------------------------------------------------- mouse test
let mouseTestTimer = null;

/** The raw-input test runs only while the test tab is on screen in a visible window. */
function setMouseTest(on) {
  on = on && !document.hidden;
  if (!on) {
    if (mouseTestTimer) { clearTimeout(mouseTestTimer); mouseTestTimer = null; call("mouse.test.stop").catch(() => {}); }
    return;
  }
  if (mouseTestTimer) return;
  mouseTestTimer = -1;
  call("mouse.test.start").then(() => {
    const tick = async () => {
      if (!mouseTestTimer) return;
      try { renderMouseTest(await call("mouse.test.read", { thresholdMs: +$("#mtThreshold").value })); } catch { /* next tick */ }
      if (mouseTestTimer) mouseTestTimer = setTimeout(tick, 250);
    };
    tick();
  }).catch((err) => { mouseTestTimer = null; toast("Mouse test: " + err.message, true); });
}
const mouseTestVisible = () => isVisible("mouse") && $("#mouseTabs [data-tab=test]").classList.contains("active");
document.addEventListener("visibilitychange", () => setMouseTest(mouseTestVisible()));

function renderMouseTest(t) {
  $("#mtDevice").textContent = t.device || "";
  $("#mtHz").textContent = t.samples > 20 ? Math.round(t.hz) : "—";
  $("#mtSet").textContent = mouse?.polling ? `set: ${mouse.polling} Hz` : "";
  $("#mtSteady").textContent = t.samples <= 20 ? "—" : t.fastEnough ? Math.round(t.steady * 100) + " %" : "move faster";
  const c = $("#mtHistory"), g = c.getContext("2d"), max = Math.max(mouse?.polling || 1000, ...t.history) * 1.1;
  g.clearRect(0, 0, c.width, c.height);
  g.fillStyle = token("--line"); g.fillRect(0, c.height - 1, c.width, 1);
  const w = c.width / 10;
  t.history.forEach((hz, i) => {
    const h = (hz / max) * (c.height - 24), x = i * w + 6;
    g.fillStyle = token("--accent"); g.fillRect(x, c.height - h, w - 12, h);
    g.fillStyle = token("--muted"); g.font = `20px ${token("--label-font")}`; g.fillText(Math.round(hz), x, c.height - h - 6);
  });
  $("#mtButtons").replaceChildren(...(t.buttons.length ? t.buttons.map((b) => h("div", { class: "mt-btn" + (b.chatter ? " bad" : "") },
    h("span", {}, b.name), h("span", {}, `${b.presses} press${b.presses === 1 ? "" : "es"}`),
    h("b", {}, b.chatter ? `${b.chatter} chatter` : "ok"),
    h("small", {}, b.fastestGapMs == null ? "" : `fastest gap ${b.fastestGapMs.toFixed(1)} ms`))) : [h("p", { class: "muted" }, "No clicks yet.")]));
}
$("#mtThreshold").addEventListener("input", () => ($("#mtThresholdVal").textContent = $("#mtThreshold").value + " ms"));
$("#mtThresholdVal").textContent = "30 ms";
$("#aimLaunch").addEventListener("click", () => act("Aim lab", () => call("aim.launch"), null));
$("#mtReset").addEventListener("click", () => call("mouse.test.reset").catch(() => {}));
// the pad swallows clicks so testing never presses a control; raw input still sees every press
["mousedown", "mouseup", "click", "auxclick", "contextmenu"].forEach((ev) => $("#mtPad").addEventListener(ev, (e) => e.preventDefault()));

// ---------------------------------------------------------------- live state
let state = null;
function onState(s) {
  const prev = state;
  state = s;
  renderSide();
  renderOverview();
  applyModels();
  $("#headsetChips").replaceChildren(...(s.headset.summary || []).map((c) => h("span", { class: "chip" }, c)));
  $("#mouseOffline").classList.toggle("show", !s.mouse.connected);
  $("#headsetOffline").classList.toggle("show", !s.headset.on);
  $("#mouseBatteryBig").replaceChildren(...battery(s.mouse.battery));
  $("#headsetBatteryBig").replaceChildren(...battery(s.headset.battery));
  if (prev && !prev.mouse.connected && s.mouse.connected && isVisible("mouse")) loadMouse();
  if (prev && !prev.headset.on && s.headset.on && isVisible("headset")) loadHeadset();
  if (prev && JSON.stringify(prev.stability) !== JSON.stringify(s.stability) && isVisible("system")) renderEvents();
}
/** Image for a model: its render in the chosen colour (first colour if that one doesn't exist), else a drawing. */
function renderSrc(model, color, kind) {
  if (!model.render || state.devicePictures === "drawing") return drawingSrc(kind);
  return `${model.render}-${model.colors.includes(color) ? color : model.colors[0]}.png`;
}

/** Line drawings of a mouse and a headset in the current theme's colours (an SVG data URL). */
function drawingSrc(kind) {
  const ink = token("--text"), fill = token("--panel-2"), accent = token("--accent");
  const svg = kind === "mouse"
    ? `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 154 307">
        <path d="M77 4C33 4 8 40 8 96v118c0 52 30 89 69 89s69-37 69-89V96C146 40 121 4 77 4z" fill="${fill}" stroke="${ink}" stroke-width="2.5"/>
        <path d="M77 6v120M10 126h134" stroke="${ink}" stroke-width="2" fill="none"/>
        <rect x="69" y="40" width="16" height="40" rx="3" fill="${accent}"/></svg>`
    : `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 628 800">
        <path d="M110 430V330C110 170 200 60 314 60s204 110 204 270v100" fill="none" stroke="${ink}" stroke-width="10" stroke-linecap="round"/>
        <rect x="40" y="380" width="160" height="250" rx="40" fill="${fill}" stroke="${ink}" stroke-width="8"/>
        <rect x="428" y="380" width="160" height="250" rx="40" fill="${fill}" stroke="${ink}" stroke-width="8"/>
        <rect x="88" y="440" width="64" height="130" rx="10" fill="${accent}"/><rect x="476" y="440" width="64" height="130" rx="10" fill="${accent}"/>
        <path d="M120 600c0 90 50 140 150 150" fill="none" stroke="${ink}" stroke-width="10" stroke-linecap="round"/>
        <circle cx="282" cy="752" r="20" fill="${accent}"/></svg>`;
  return "data:image/svg+xml," + encodeURIComponent(svg);
}

/**
 * Names, renders and visible sections follow the connected models. Caps are null until a device has been
 * probed (or while it is away): then everything stays visible, as for the user's own devices.
 */
function applyModels() {
  const mm = state.mouse.model, hm = state.headset.model;
  const drawing = state.devicePictures === "drawing";
  $$(".mouse-img").forEach((img) => { img.src = renderSrc(mm, state.mouseColor, "mouse"); img.classList.toggle("drawing", drawing || !mm.render); });
  $$(".headset-img").forEach((img) => { img.src = renderSrc(hm, state.headsetColor, "headset"); img.classList.toggle("drawing", drawing || !hm.render); });
  $("#ovMouseName").textContent = mm.name; $("#mouseTitle").textContent = mm.fullName;
  $("#ovHeadsetName").textContent = hm.name; $("#headsetTitle").textContent = hm.fullName;
  $("#picturesBlock").hidden = !mm.render && !hm.render;
  $("#mouseColorRow").hidden = drawing || mm.colors.length < 2;
  $("#headsetColorRow").hidden = drawing || hm.colors.length < 2;

  if (mouse) renderCallouts(); // the button set comes with the model
  const mc = mm.caps;
  $$("#page-mouse [data-cap]").forEach((el) => (el.hidden = mc != null &&
    (el.dataset.cap === "buttons" ? mm.buttons.length === 0 : !mc[el.dataset.cap])));
  let firstVisible = null;
  $$("#mouseTabs button").forEach((b) => {
    const tab = $(`#page-mouse .tab[data-tab="${b.dataset.tab}"]`);
    const cards = $$("[data-cap]", tab);
    b.hidden = cards.length > 0 && cards.every((c) => c.hidden);
    if (!b.hidden && !firstVisible) firstVisible = b;
  });
  if ($("#mouseTabs button.active")?.hidden) firstVisible?.click();

  const hc = hm.caps;
  $$("#page-headset [data-hcap]").forEach((el) => (el.hidden = hc != null && !hc[el.dataset.hcap]));
  $("#eqUnsupported").hidden = hc == null || hc.eq;
}
const isVisible = (page) => $("#page-" + page).classList.contains("active");

function battery(b) {
  if (!b) return [h("span", { class: "muted" }, "Unknown — device asleep or off")];
  const fill = h("div", { class: "fill" + (b.percent <= state.alertPercent ? " low" : "") });
  fill.style.width = Math.max(4, b.percent) + "%";
  return [h("div", { class: "cell" }, fill), h("div", {}, h("div", { class: "val" }, b.percent + "%"),
    h("div", { class: "muted" }, b.charging ? "Charging" : "On battery"))];
}

function renderSide() {
  const row = (name, on, b) => h("div", { class: "dev" }, h("i", { class: "dot" + (on ? " on" : "") }), name,
    h("span", { class: "pct" }, b ? b.percent + "%" : ""));
  $("#sideStatus").replaceChildren(row("Mouse", state.mouse.connected, state.mouse.battery),
    row("Headset", state.headset.on, state.headset.battery));
}

function ring(el, b, label) {
  const color = !b ? "var(--line)" : b.percent <= state.alertPercent ? "var(--bad)" : b.percent <= 40 ? "var(--warn)" : "var(--good)";
  el.style.setProperty("--p", b ? b.percent : 0);
  el.style.setProperty("--c", color);
  el.replaceChildren(h("span", {}, b ? b.percent + "%" : "—", h("small", {}, label)));
}

function renderOverview() {
  const m = state.mouse, hs = state.headset;
  const ovm = $("#ovMouse"), ovh = $("#ovHeadset");
  ring($("[data-ring]", ovm), m.battery, m.battery?.charging ? "charging" : "battery");
  ring($("[data-ring]", ovh), hs.battery, hs.battery?.charging ? "charging" : "battery");
  $("[data-conn]", ovm).textContent = !m.connected ? "Not connected" : !m.model.known ? "Connected" : m.viaDongle ? m.model.wireless : "Wired";
  $("[data-conn]", ovh).textContent = !hs.dongle ? "Dongle not found" : hs.on ? hs.model.wireless : "Headset off";
  $("[data-chips]", ovm).replaceChildren(...(m.summary || []).map((c) => h("span", { class: "chip" }, c)));
  $("[data-chips]", ovh).replaceChildren(...(hs.summary || []).map((c) => h("span", { class: "chip" }, c)));
  $("#ovAudioBody").textContent = state.audioSummary || "";
  renderOverviewSystem();
  const w = $("#vendorWarning");
  w.textContent = state.vendors.length ? `${state.vendors.join(", ")} still running — quit them so they don't override Hub.` : "All devices are managed by Hub.";
  w.classList.toggle("warn", state.vendors.length > 0);
}

// ---------------------------------------------------------------- mouse
let mouse = null;
const DPI_ANCHORS = [[0, 100], [0.18, 500], [0.36, 1500], [0.6, 10000], [0.78, 15000], [1, 35000]];
function dpiToPos(d) {
  for (let i = 1; i < DPI_ANCHORS.length; i++) {
    const [p0, d0] = DPI_ANCHORS[i - 1], [p1, d1] = DPI_ANCHORS[i];
    if (d <= d1) return p0 + ((Math.log(d) - Math.log(d0)) / (Math.log(d1) - Math.log(d0))) * (p1 - p0);
  }
  return 1;
}
function posToDpi(p) {
  for (let i = 1; i < DPI_ANCHORS.length; i++) {
    const [p0, d0] = DPI_ANCHORS[i - 1], [p1, d1] = DPI_ANCHORS[i];
    if (p <= p1) return Math.round(Math.exp(Math.log(d0) + ((p - p0) / (p1 - p0)) * (Math.log(d1) - Math.log(d0))) / 50) * 50;
  }
  return 35000;
}

async function loadMouse() {
  if (!state?.mouse.connected) return;
  try {
    mouse = await call("mouse.load");
  } catch (err) { toast("Reading mouse: " + err.message, true); return; }
  renderCallouts();
  renderStages();
  segmented($("#polling"), mouse.pollingRates.map((r) => ({ value: r, label: String(r) })), mouse.polling,
    (hz) => act("Polling rate", () => call("mouse.setPolling", { hz }), `Polling rate set to ${hz} Hz`));
  const known = state.mouse.model.known;
  $("#pollingNote").textContent = !known ? "" : mouse.viaDongle ? `HyperPolling over the ${state.mouse.model.wireless.replace(" wireless", "")} dongle.` : "Over the cable the mouse supports up to 1000 Hz.";
  setRange($("#idle"), mouse.idleMinutes); setRange($("#lowBattery"), mouse.lowBattery); updatePowerLabels();
  if (mouse.lift) {
    setLiftMode(mouse.lift.asymmetric ? "asymmetric" : "symmetric");
    setRange($("#tracking"), mouse.lift.tracking); setRange($("#liftOff"), mouse.lift.liftOff); setRange($("#landing"), mouse.lift.landing);
    updateLiftLabels();
  }
  if (mouse.dyn) {
    $("#dynEnabled").checked = mouse.dyn.enabled;
    curveMode = mouse.dyn.mode;
    customPoints = mouse.dyn.customPoints.map((p) => ({ ...p }));
    selectDynMode(curveMode);
  }
  if (mouse.rotation) {
    $("#rotEnabled").checked = mouse.rotation.enabled;
    setRange($("#rotAngle"), mouse.rotation.angle); updateRotation();
  }
}

// ----- customize
const ACTIONS = [
  { group: "Mouse", items: [["Left click", 1, [1]], ["Right click", 1, [2]], ["Middle click", 1, [3]], ["Back (button 4)", 1, [4]],
    ["Forward (button 5)", 1, [5]], ["Scroll up", 1, [9]], ["Scroll down", 1, [10]], ["Double click", 11, [1]]] },
  { group: "Sensitivity", items: [["Cycle up sensitivity stages", 6, [6]], ["Cycle down sensitivity stages", 6, [7]],
    ["Sensitivity stage up", 6, [1]], ["Sensitivity stage down", 6, [2]]] },
  { group: "Keyboard (for PC macros)", items: Array.from({ length: 12 }, (_, i) => [`F${13 + i}`, 2, [0, 0x68 + i]]) },
  { group: "Other", items: [["Disable", 0, []]] },
];
function describe(a) {
  for (const g of ACTIONS) for (const [label, fn, data] of g.items)
    if (fn === a.fn && data.length === a.data.length && data.every((v, i) => v === a.data[i])) return label;
  return `Function ${a.fn} [${a.data.join(", ")}]`;
}

function renderCallouts() {
  for (const side of ["left", "right"]) {
    const col = $(side === "left" ? "#calloutsLeft" : "#calloutsRight");
    // positions come from the device catalog (Hub.Core/Devices/DeviceCatalog.cs)
    col.replaceChildren(...state.mouse.model.buttons.filter((c) => c.left === (side === "left")).map((c) => {
      const a = mouse.buttons[c.id];
      const isDefault = a && a.isDefault;
      const el = h("div", { class: "callout" + (c.id === 1 ? " locked" : "") + (isDefault ? "" : " changed") },
        h("div", { class: "label" }, h("b", {}, a ? describe(a) : c.name), h("small", {}, c.name)),
        h("div", { class: "line" }));
      el.style.top = c.top + "px";
      if (c.id !== 1) el.addEventListener("click", (e) => openActions(e.currentTarget, c));
      return el;
    }));
  }
}

function openActions(anchor, c) {
  const pop = $("#popover");
  const current = mouse.buttons[c.id];
  pop.replaceChildren(
    h("button", { onclick: () => assign(c, null) }, "Restore default"),
    ...ACTIONS.flatMap((g) => [h("div", { class: "group" }, g.group),
      ...g.items.map(([label, fn, data]) => h("button", {
        class: current && describe(current) === label ? "active" : "",
        onclick: () => assign(c, { fn, data }),
      }, label))]));
  const r = anchor.getBoundingClientRect();
  pop.classList.add("show");
  const pr = pop.getBoundingClientRect();
  pop.style.left = Math.min(window.innerWidth - pr.width - 12, Math.max(12, r.left)) + "px";
  pop.style.top = Math.min(window.innerHeight - pr.height - 12, r.bottom + 6) + "px";
}
document.addEventListener("mousedown", (e) => { if (!e.target.closest("#popover")) $("#popover").classList.remove("show"); });

async function assign(c, action) {
  $("#popover").classList.remove("show");
  const res = await act(c.name, () => call("mouse.setButton", { id: c.id, action }), `${c.name} updated`);
  mouse.buttons[c.id] = res;
  renderCallouts();
}

// ----- stages
function renderStages() {
  $("#stageCount").textContent = mouse.stages.length;
  $("#stages").replaceChildren(...mouse.stages.map((s, i) => {
    const slider = h("input", { type: "range", min: 0, max: 1000, step: 1 });
    const num = h("input", { type: "number", min: 100, max: 35000, step: 50, value: s });
    setRange(slider, Math.round(dpiToPos(s) * 1000));
    slider.addEventListener("input", () => { mouse.stages[i] = posToDpi(slider.value / 1000); num.value = mouse.stages[i]; });
    num.addEventListener("change", () => {
      mouse.stages[i] = Math.min(35000, Math.max(100, Math.round(+num.value / 50) * 50));
      num.value = mouse.stages[i];
      setRange(slider, Math.round(dpiToPos(mouse.stages[i]) * 1000));
    });
    const badge = h("div", { class: "badge", title: "Make active", onclick: () => { mouse.activeStage = i + 1; renderStages(); } }, String(i + 1));
    return h("div", { class: "stage" + (mouse.activeStage === i + 1 ? " active" : "") }, badge, num,
      h("div", {}, slider, h("div", { class: "ticks" }, ...["100", "500", "1500", "10000", "15000", "35000"].map((t) => h("span", {}, t)))));
  }));
}
$("[data-stage-inc]").addEventListener("click", () => {
  if (mouse && mouse.stages.length < 5) { mouse.stages.push(mouse.stages[mouse.stages.length - 1] * 2 > 35000 ? 35000 : mouse.stages[mouse.stages.length - 1] * 2); renderStages(); }
});
$("[data-stage-dec]").addEventListener("click", () => {
  if (mouse && mouse.stages.length > 1) { mouse.stages.pop(); mouse.activeStage = Math.min(mouse.activeStage, mouse.stages.length); renderStages(); }
});
$("#applyStages").addEventListener("click", () =>
  act("DPI stages", () => call("mouse.setStages", { active: mouse.activeStage, stages: mouse.stages })));

// ----- power
function updatePowerLabels() {
  $("#idleVal").textContent = $("#idle").value + " min";
  $("#lowVal").textContent = $("#lowBattery").value + "%";
}
$("#idle").addEventListener("input", updatePowerLabels);
$("#lowBattery").addEventListener("input", updatePowerLabels);
$("#applyPower").addEventListener("click", () =>
  act("Power settings", () => call("mouse.setPower", { idleMinutes: +$("#idle").value, lowBattery: +$("#lowBattery").value })));

// ----- calibration
let liftMode = "asymmetric";
function setLiftMode(m) {
  liftMode = m;
  $$("#liftMode button").forEach((b) => b.classList.toggle("active", b.dataset.mode === m));
  $("#liftSym").style.display = m === "symmetric" ? "" : "none";
  $("#liftAsym").style.display = m === "asymmetric" ? "" : "none";
}
$$("#liftMode button").forEach((b) => b.addEventListener("click", () => setLiftMode(b.dataset.mode)));
function updateLiftLabels() {
  $("#trackVal").textContent = ["", "Low", "Mid", "High"][+$("#tracking").value];
  $("#liftVal").textContent = $("#liftOff").value;
  $("#landVal").textContent = $("#landing").value;
}
["#tracking", "#liftOff", "#landing"].forEach((s) => $(s).addEventListener("input", () => {
  if (+$("#landing").value >= +$("#liftOff").value) setRange($("#landing"), +$("#liftOff").value - 1);
  updateLiftLabels();
}));
$("#applyLift").addEventListener("click", () => act("Lift-off", () => call("mouse.setLift", {
  asymmetric: liftMode === "asymmetric", tracking: +$("#tracking").value, liftOff: +$("#liftOff").value, landing: +$("#landing").value,
})));

// ----- dynamic sensitivity (PCHIP spline identical to Hub.Core SensitivityCurve)
const TEMPLATES = {
  0: [[0, 1], [20, 1.1], [40, 1.2], [70, 1.35], [100, 1.5]],
  1: [[0, 1], [11, 1.45], [20, 1.5], [35, 1.5], [100, 1.5]],
  2: [[0, 1], [9, 1], [21, 1.5], [24, 1.5], [100, 1.5]],
};
let curveMode = 3, customPoints = [], dragIndex = -1;

function spline(pts) {
  const x = pts.map((p) => p.x), y = pts.map((p) => p.y), n = x.length;
  const m = []; for (let i = 0; i < n - 1; i++) m.push((y[i + 1] - y[i]) / (x[i + 1] - x[i]));
  const t = new Array(n).fill(0);
  let hp = x[1] - x[0], zp = Math.abs(m[0]) < Number.EPSILON;
  for (let i = 1; i < n - 1; i++) {
    const hh = x[i + 1] - x[i], z = Math.abs(m[i]) < Number.EPSILON;
    if (z || zp || Math.sign(m[i]) !== Math.sign(m[i - 1])) t[i] = 0;
    else { const w1 = 2 * hh + hp, w2 = hh + 2 * hp; t[i] = (w1 + w2) / (w1 / m[i - 1] + w2 / m[i]); }
    hp = hh; zp = z;
  }
  const end = (h0, h1, m0, m1) => {
    const s = ((2 * h0 + h1) * m0 - h0 * m1) / (h0 + h1);
    if (Math.sign(s) !== Math.sign(m0)) return 0;
    if (Math.sign(m0) !== Math.sign(m1) && Math.abs(s) > 3 * Math.abs(m0)) return 3 * m0;
    return s;
  };
  t[0] = end(x[1] - x[0], x[2] - x[1], m[0], m[1]);
  t[n - 1] = end(x[n - 1] - x[n - 2], x[n - 2] - x[n - 3], m[n - 2], m[n - 3]);
  return (v) => {
    let lo = 0, hi = n - 1;
    while (hi - lo > 1) { const mid = (lo + hi) >> 1; if (x[mid] <= v) lo = mid; else hi = mid; }
    const hh = x[lo + 1] - x[lo], u = v - x[lo];
    const c2 = (3 * (y[lo + 1] - y[lo]) / hh - 2 * t[lo] - t[lo + 1]) / hh, c3 = (2 * (y[lo] - y[lo + 1]) / hh + t[lo] + t[lo + 1]) / (hh * hh);
    return y[lo] + u * (t[lo] + u * (c2 + u * c3));
  };
}

function curvePoints() {
  return curveMode === 3 ? customPoints : TEMPLATES[curveMode].map(([x, y]) => ({ x, y }));
}

const CV = { x0: 70, x1: 1080, y0: 290, y1: 20, xMax: 105, yMax: 2 };
const cx = (v) => CV.x0 + (v / CV.xMax) * (CV.x1 - CV.x0);
const cy = (v) => CV.y0 - (v / CV.yMax) * (CV.y0 - CV.y1);

function drawCurve() {
  const canvas = $("#curve"), g = canvas.getContext("2d");
  g.clearRect(0, 0, canvas.width, canvas.height);
  g.font = `13px ${token("--label-font")}`; g.fillStyle = token("--muted"); g.strokeStyle = token("--line"); g.lineWidth = 1;
  for (let v = 0; v <= 105; v += 15) { g.beginPath(); g.moveTo(cx(v), CV.y1); g.lineTo(cx(v), CV.y0); g.stroke(); g.fillText(v, cx(v) - 6, CV.y0 + 20); }
  for (let v = 0; v <= 2; v += 0.25) { g.beginPath(); g.moveTo(CV.x0, cy(v)); g.lineTo(CV.x1, cy(v)); g.stroke(); g.fillText(v.toFixed(2), 20, cy(v) + 4); }
  g.fillText("ratio", 20, CV.y1 - 4);
  g.textAlign = "right"; g.fillText("input speed (counts/ms)", CV.x1, CV.y1 - 4); g.textAlign = "left";

  const pts = curvePoints(), f = spline(pts), maxX = pts[pts.length - 1].x;
  const grad = token("--accent");
  g.beginPath();
  for (let v = 0; v <= CV.xMax; v += 0.25) {
    const yv = v <= maxX ? f(v) : pts[pts.length - 1].y;
    v === 0 ? g.moveTo(cx(v), cy(yv)) : g.lineTo(cx(v), cy(yv));
  }
  g.strokeStyle = grad; g.lineWidth = 3; g.stroke();
  g.lineTo(cx(CV.xMax), cy(0)); g.lineTo(cx(0), cy(0)); g.closePath();
  g.globalAlpha = 0.12; g.fillStyle = grad; g.fill(); g.globalAlpha = 1;

  if (curveMode === 3) for (const p of pts) {
    g.beginPath(); g.arc(cx(p.x), cy(p.y), 7, 0, Math.PI * 2); g.fillStyle = token("--panel"); g.fill();
    g.lineWidth = 3; g.strokeStyle = token("--accent"); g.stroke();
  }
}

function canvasPos(e) {
  const c = $("#curve"), r = c.getBoundingClientRect();
  return { x: ((e.clientX - r.left) / r.width) * c.width, y: ((e.clientY - r.top) / r.height) * c.height };
}
$("#curve").addEventListener("mousedown", (e) => {
  if (curveMode !== 3) return;
  const p = canvasPos(e);
  dragIndex = customPoints.findIndex((q) => Math.hypot(cx(q.x) - p.x, cy(q.y) - p.y) < 14);
});
window.addEventListener("mousemove", (e) => {
  if (dragIndex < 0) return;
  const p = canvasPos(e), n = customPoints.length;
  let x = ((p.x - CV.x0) / (CV.x1 - CV.x0)) * CV.xMax, y = ((CV.y0 - p.y) / (CV.y0 - CV.y1)) * CV.yMax;
  if (dragIndex === 0) x = 0;
  else if (dragIndex === n - 1) x = 100;
  else x = Math.min(customPoints[dragIndex + 1].x - 1, Math.max(customPoints[dragIndex - 1].x + 1, x));
  customPoints[dragIndex] = { x: Math.round(x * 10) / 10, y: Math.round(Math.min(2, Math.max(0.1, y)) * 100) / 100 };
  drawCurve();
});
window.addEventListener("mouseup", () => (dragIndex = -1));

function selectDynMode(mode) {
  curveMode = mode;
  $$("#dynMode button").forEach((b) => b.classList.toggle("active", +b.dataset.mode === mode));
  $("#curveHint").textContent = mode === 3 ? "Drag the points to shape your curve." : "Preset curve. Switch to Custom to edit.";
  drawCurve();
}
$$("#dynMode button").forEach((b) => b.addEventListener("click", () => {
  const m = +b.dataset.mode;
  if (m === 3 && customPoints.length === 0) customPoints = TEMPLATES[curveMode === 3 ? 0 : curveMode].map(([x, y]) => ({ x, y }));
  selectDynMode(m);
}));
$("#applyDyn").addEventListener("click", () => act("Dynamic sensitivity", () => call("mouse.setDyn", {
  enabled: $("#dynEnabled").checked, mode: curveMode, points: curveMode === 3 ? customPoints : null,
})));

// ----- rotation
function updateRotation() {
  const a = +$("#rotAngle").value;
  $("#rotVal").textContent = a + "°";
  $("#rotImg").style.transform = `rotate(${$("#rotEnabled").checked ? a : 0}deg)`;
}
$("#rotAngle").addEventListener("input", updateRotation);
$("#rotEnabled").addEventListener("change", updateRotation);
$("#applyRot").addEventListener("click", () => act("Rotation", () =>
  call("mouse.setRotation", { enabled: $("#rotEnabled").checked, angle: +$("#rotAngle").value })));

// ---------------------------------------------------------------- headset
let headset = null;
const EQ_FREQS = ["80 Hz", "240 Hz", "750 Hz", "2.2 kHz", "6.6 kHz"];

async function loadHeadset() {
  if (!state?.headset.on) return;
  try { headset = await call("headset.load"); } catch (err) { toast("Reading headset: " + err.message, true); return; }
  $("#headsetSub").textContent = `Firmware ${headset.firmware}`;
  if (headset.eq) { renderEq(headset.eq); renderEqPresets(); }
  setRange($("#sidetone"), headset.sidetone); $("#sideVal").textContent = headset.sidetone;
  $("#nr").checked = headset.noiseReduction;
  const opts = [...new Set([0, 5, 15, 30, 60, headset.sleep])].sort((a, b) => a - b);
  segmented($("#sleep"), opts.map((m) => ({ value: m, label: m === 0 ? "Never" : `${m} min` })), headset.sleep,
    (minutes) => act("Auto power-off", () => call("headset.setSleep", { minutes })));
}

function renderEq(gains) {
  $("#eqBands").replaceChildren(...gains.map((g, i) => {
    const db = h("div", { class: "db" }, fmtDb(g));
    const slider = h("input", { type: "range", min: -12, max: 12, step: 1, value: g });
    slider.addEventListener("input", () => { db.textContent = fmtDb(+slider.value); highlightPreset(); });
    return h("div", { class: "band" }, db, slider, h("div", { class: "hz" }, EQ_FREQS[i]));
  }));
}
const fmtDb = (g) => (g > 0 ? "+" : "") + g + " dB";
const eqGains = () => $$("#eqBands input").map((i) => +i.value);

function renderEqPresets() {
  $("#eqPresets").replaceChildren(...headset.presets.map((p) => h("span", {
    class: "chip", "data-name": p.name, onclick: () => { renderEq(p.gains); highlightPreset(); },
  }, p.name)));
  highlightPreset();
}
function highlightPreset() {
  const g = eqGains().join();
  $$("#eqPresets .chip").forEach((c) => c.classList.toggle("active",
    headset.presets.find((p) => p.name === c.dataset.name)?.gains.join() === g));
}
$("#eqTry").addEventListener("click", () => act("EQ", () => call("headset.setEq", { gains: eqGains(), save: false }), "EQ applied (until the headset restarts)"));
$("#eqSave").addEventListener("click", () => act("EQ", () => call("headset.setEq", { gains: eqGains(), save: true }), "EQ saved to the headset"));
$("#eqSaveAs").addEventListener("click", async () => {
  const name = prompt("Preset name", $("#eqPresets .chip.active")?.dataset.name || "My preset");
  if (!name) return;
  headset.presets = await act("Preset", () => call("headset.savePreset", { name, gains: eqGains() }), `Saved “${name}”`);
  renderEqPresets();
});

let sidetoneTimer;
$("#sidetone").addEventListener("input", () => {
  $("#sideVal").textContent = $("#sidetone").value;
  clearTimeout(sidetoneTimer);
  sidetoneTimer = setTimeout(() => act("Sidetone", () => call("headset.setSidetone", { level: +$("#sidetone").value }), null), 200);
});
$("#nr").addEventListener("change", () => act("Noise reduction", () => call("headset.setNr", { enabled: $("#nr").checked })));

// ---------------------------------------------------------------- audio
let audio = null, audioError = null;
/** A channel's colour in the current theme. */
const chColor = (id) => token(`--ch-${id}`);

async function loadAudio() {
  try { audio = await call("audio.load"); } catch (err) { toast("Audio: " + err.message, true); return; }
  renderAudioSetup();
  setRange($("#chatMix"), Math.round(audio.chatMix * 100)); updateChatMixLabel();
  $("#channels").replaceChildren(...audio.channels.map(renderChannel));
  audio.channels.forEach((c) => drawResponse(c));
  const running = audio.status.running;
  $("#appsCard").classList.toggle("disabled", !running);
  if (running) loadApps();
  loadIo();
}

// ----- output & mic (default: Windows' playback device at setup; the Windows default mic)
async function loadIo() {
  let io;
  try { io = await call("audio.io"); } catch (err) { return; }
  const fill = (select, items, current, chosen, autoLabel) => {
    select.replaceChildren(
      h("option", { value: "", ...(!chosen ? { selected: "" } : {}) }, autoLabel + (current && !chosen ? ` — ${current.name}` : "")),
      ...items.map((it) => h("option", { value: it.id, ...(chosen && it.id === current?.id ? { selected: "" } : {}) }, it.name)));
  };
  fill($("#ioOutput"), io.outputs, io.outputs.find((o) => o.id === io.output) || null, io.outputChosen, "Automatic");
  fill($("#ioMic"), io.mics, io.mics.find((m) => m.id === io.mic) || null, io.micChosen, "Windows default");
}
$("#ioOutput").addEventListener("change", (e) => act("Output", async () => {
  await call("audio.setOutput", { id: e.target.value || null }); await loadAudio();
}, e.target.value ? `Output: ${e.target.selectedOptions[0].textContent}` : "Output: automatic"));
$("#ioMic").addEventListener("change", (e) => act("Microphone", async () => {
  await call("audio.setMic", { id: e.target.value || null }); await loadIo();
}, "Default microphone set"));

// ----- per-app routing
let appsTimer;
async function loadApps() {
  clearTimeout(appsTimer);
  if (!isVisible("audio")) return;
  try {
    const apps = await call("audio.apps");
    const list = $("#apps");
    if (!apps.length) list.replaceChildren(h("p", { class: "hint" }, "Nothing is playing sound right now. Start a game, Discord or music and it shows up here."));
    else list.replaceChildren(...apps.map((a) => {
      const seg = h("div", { class: "segmented" });
      segmented(seg, [{ value: "game", label: "Game" }, { value: "chat", label: "Chat" }, { value: "media", label: "Media" }], a.channel,
        (channel) => act(a.name, () => call("audio.assignApp", { name: a.name, channel }), `${a.name} → ${channel[0].toUpperCase() + channel.slice(1)}`));
      return h("div", { class: "app-row" },
        h("span", { class: "name" }, h("i", { class: a.playing ? "on" : "" }), a.name, a.channel ? null : h("small", { class: "muted" }, " (plays to another device)")),
        seg);
    }));
  } catch (err) { /* transient: sessions come and go */ }
  appsTimer = setTimeout(loadApps, 4000);
}

// ----- on/off and health
function renderAudioSetup() {
  const s = audio.status;
  const card = $("#audioSetup");
  card.classList.add("show");
  const state = s.running ? "On" : s.on ? "Waiting" : "Off";
  const detail = s.running
    ? `Apps play to ${s.sink} (silent), Hub mixes them and plays to ${s.output}.`
    : s.on ? (s.blocker || "Starting…")
    : "Windows plays straight to your output device. Turn on to get Game, Chat and Media channels with EQ and ChatMix.";
  card.replaceChildren(...[
    h("div", { class: "row between" },
      h("div", { class: "card-title" }, "Hub audio ", h("span", { class: "tag" }, state)),
      s.running || s.on
        ? h("button", { class: "btn ghost", onclick: () => act("Hub audio", async () => { await call("audio.stop"); await loadAudio(); }, "Hub audio off") }, "Turn off")
        : h("button", { class: "btn primary", ...(s.blocker ? { disabled: "" } : {}), onclick: async (e) => {
            e.currentTarget.disabled = true;
            try { await call("audio.start"); audioError = null; toast("Hub audio on"); }
            catch (err) { audioError = err.message; }
            await loadAudio();
          } }, "Turn on")),
    h("p", { class: "muted" }, detail),
    !s.running && !s.on && s.blocker ? h("p", { class: "setup-error" }, s.blocker) : null,
    s.problem ? h("p", { class: "setup-error" }, s.problem) : null,
    audioError ? h("p", { class: "setup-error" }, audioError) : null,
  ].filter(Boolean));
}

function renderChannel(c) {
  const opt = (name, label = name) => h("option", { value: name, ...(name === c.active ? { selected: "" } : {}) }, label);
  const group = (label, items) => items.length ? h("optgroup", { label }, ...items) : null;
  const custom = c.presets.filter((p) => p.category === "Custom");
  const builtIn = c.presets.filter((p) => p.category !== "Custom");
  const select = h("select", {},
    group("Your custom profiles", custom.map((p) => opt(p.name, `✎ ${p.name}`))),
    group("Built-in", builtIn.map((p) => opt(p.name, p.name === c.defaultChoice ? `${p.name} (default)` : p.name))));
  const search = h("input", { type: "search", placeholder: `Search ${c.presets.length} presets…` });
  search.addEventListener("input", () => {
    const q = search.value.toLowerCase();
    $$("option", select).forEach((o) => (o.hidden = q && !o.value.toLowerCase().includes(q)));
  });
  select.addEventListener("change", async () => {
    const r = await act(c.name, () => call("audio.setPreset", { channel: c.id, name: select.value }), `${c.name}: ${select.value}`);
    replaceChannel(r);
  });
  const vol = h("input", { type: "range", min: 0, max: 100, step: 1 });
  setRange(vol, Math.round(c.volume * 100));
  const volVal = h("b", {}, Math.round(c.volume * 100) + "%");
  let t;
  vol.addEventListener("input", () => {
    volVal.textContent = vol.value + "%";
    clearTimeout(t);
    t = setTimeout(() => call("audio.setVolume", { channel: c.id, value: vol.value / 100 }).catch((e) => toast(e.message, true)), 60);
  });
  const tame = h("input", { type: "range", min: 0, max: c.maxTame, step: 1 });
  setRange(tame, c.tame);
  const tameText = (db) => (db > 0 ? `−${db} dB` : "Off");
  const tameVal = h("b", {}, tameText(c.tame));
  let tt;
  tame.addEventListener("input", () => {
    tameVal.textContent = tameText(+tame.value);
    clearTimeout(tt);
    tt = setTimeout(() => call("audio.setTame", { channel: c.id, value: +tame.value }).then(() => (c.tame = +tame.value)).catch((e) => toast(e.message, true)), 150);
  });
  const dot = h("i"); dot.style.background = `var(--ch-${c.id})`;
  const active = c.filters.filter((f) => f.enabled).length;
  return h("div", { class: "card channel", "data-ch": c.id },
    h("h2", {}, dot, c.name, c.custom ? h("span", { class: "badge-custom" }, "Custom") : null),
    h("label", { class: "field" }, "Volume ", volVal, vol),
    h("label", { class: "field", title: "Turns down sudden loud sounds such as gunshots and explosions; quieter sounds like footsteps stay as they are" },
      "Tame loud sounds ", tameVal, tame),
    c.presets.length > 12 ? search : null,
    h("div", { class: "preset-row" }, select,
      h("button", { class: "btn small", title: "EQ filters, gain, limiter and sample rate", onclick: () => openAdvanced(c.id) }, "Advanced")),
    h("canvas", { width: 600, height: 240 }),
    h("p", { class: "hint" }, `${active} filters · gain ${c.preamp.toFixed(1)} dB` + (c.limiter ? " · limiter" : "") + ` · ${c.rate / 1000} kHz`));
}

/** Swaps one channel card for fresh data from the host (after a preset change or a save). */
function replaceChannel(r) {
  const i = audio.channels.findIndex((x) => x.id === r.id);
  audio.channels[i] = r;
  $(`.channel[data-ch="${r.id}"]`).replaceWith(renderChannel(r));
  drawResponse(r);
}

function biquadDb(f, freq, fs = 48000) {
  const w0 = 2 * Math.PI * f.freq / fs, c = Math.cos(w0), s = Math.sin(w0), A = Math.pow(10, f.gain / 40), al = s / (2 * f.q), sa = 2 * Math.sqrt(A) * al;
  let b0, b1, b2, a0, a1, a2;
  switch (f.type) {
    case "Peaking": [b0, b1, b2, a0, a1, a2] = [1 + al * A, -2 * c, 1 - al * A, 1 + al / A, -2 * c, 1 - al / A]; break;
    case "LowShelf": [b0, b1, b2, a0, a1, a2] = [A * ((A + 1) - (A - 1) * c + sa), 2 * A * ((A - 1) - (A + 1) * c), A * ((A + 1) - (A - 1) * c - sa), (A + 1) + (A - 1) * c + sa, -2 * ((A - 1) + (A + 1) * c), (A + 1) + (A - 1) * c - sa]; break;
    case "HighShelf": [b0, b1, b2, a0, a1, a2] = [A * ((A + 1) + (A - 1) * c + sa), -2 * A * ((A - 1) + (A + 1) * c), A * ((A + 1) + (A - 1) * c - sa), (A + 1) - (A - 1) * c + sa, 2 * ((A - 1) - (A + 1) * c), (A + 1) - (A - 1) * c - sa]; break;
    case "LowPass": [b0, b1, b2, a0, a1, a2] = [(1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al]; break;
    case "HighPass": [b0, b1, b2, a0, a1, a2] = [(1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al]; break;
    default: [b0, b1, b2, a0, a1, a2] = [1, -2 * c, 1, 1 + al, -2 * c, 1 - al];
  }
  const w = 2 * Math.PI * freq / fs, cr = Math.cos(w), ci = -Math.sin(w), c2r = Math.cos(2 * w), c2i = -Math.sin(2 * w);
  const nr = b0 + b1 * cr + b2 * c2r, ni = b1 * ci + b2 * c2i, dr = a0 + a1 * cr + a2 * c2r, di = a1 * ci + a2 * c2i;
  return 10 * Math.log10((nr * nr + ni * ni) / (dr * dr + di * di));
}

const usable = (f, fs) => f.enabled !== false && f.freq > 0 && f.freq < fs / 2 && f.q > 0;

/** Frequency response on a log axis; `detail` adds each filter's own curve and dB labels. */
function plotEq(canvas, filters, { fs = 48000, gain = 0, color, range = 15, detail = false } = {}) {
  const g = canvas.getContext("2d"), W = canvas.width, H = canvas.height, font = detail ? 15 : 18;
  g.clearRect(0, 0, W, H);
  const fx = (f) => (Math.log10(f / 20) / 3) * W, dy = (db) => H / 2 - (db / range) * (H / 2 - 12);
  g.strokeStyle = token("--line"); g.fillStyle = token("--muted"); g.font = `${font}px ${token("--label-font")}`; g.lineWidth = 1;
  const marks = detail ? [50, 100, 200, 500, 1000, 2000, 5000, 10000] : [100, 1000, 10000];
  for (const f of marks) { g.beginPath(); g.moveTo(fx(f), 0); g.lineTo(fx(f), H); g.stroke(); g.fillText(f >= 1000 ? f / 1000 + "k" : f, fx(f) + 4, H - 8); }
  for (const db of detail ? [-12, -6, 0, 6, 12] : [-12, -6, 0, 6, 12]) {
    g.beginPath(); g.moveTo(0, dy(db)); g.lineTo(W, dy(db)); g.stroke();
    if (detail) g.fillText((db > 0 ? "+" : "") + db + " dB", 6, dy(db) - 4);
  }
  const live = filters.filter((f) => usable(f, fs));
  const N = detail ? 500 : 300, curve = (fn) => { g.beginPath(); for (let i = 0; i <= N; i++) { const f = 20 * Math.pow(1000, i / N), y = dy(fn(f)); i ? g.lineTo(fx(f), y) : g.moveTo(fx(f), y); } };
  if (detail) {
    g.lineWidth = 1.5; g.strokeStyle = token("--muted"); g.globalAlpha = 0.4;
    for (const flt of live) { curve((f) => biquadDb(flt, f, fs)); g.stroke(); }
    g.globalAlpha = 1;
  }
  curve((f) => gain + live.reduce((s, flt) => s + biquadDb(flt, f, fs), 0));
  g.strokeStyle = color; g.lineWidth = detail ? 3 : 3.5; g.stroke();
}

function drawResponse(c) {
  const canvas = $(`.channel[data-ch="${c.id}"] canvas`);
  if (canvas) plotEq(canvas, c.filters, { fs: c.rate, gain: c.preamp, color: chColor(c.id) });
}

// ----- advanced editor: built-in presets are read-only, edits always go to a custom profile
const FILTER_TYPES = [["Peaking", "Peak"], ["LowShelf", "Low shelf"], ["HighShelf", "High shelf"], ["LowPass", "Low pass"], ["HighPass", "High pass"], ["Notch", "Notch"]];
const hasGain = (type) => type === "Peaking" || type === "LowShelf" || type === "HighShelf";
let adv = null; // { ch, draft, original, readonly }

function openAdvanced(id) {
  const c = audio.channels.find((x) => x.id === id);
  adv = {
    ch: c, original: c.custom ? c.active : null, readonly: !c.custom,
    draft: { name: c.active, gain: c.gain, limiter: c.limiter, autoHeadroom: c.autoHeadroom, rate: c.rate, filters: c.filters.map((f) => ({ ...f })) },
  };
  renderAdvanced();
  $("#adv").showModal();
}

function renderAdvanced() {
  const { ch, draft: d, readonly } = adv;
  $("#advError").textContent = "";
  $("#adv").classList.toggle("readonly", readonly);
  $("#advFields").disabled = readonly; // built-in presets: nothing in the editor can be focused or changed
  $("#advChannel").textContent = `${ch.name} · Advanced`;
  $("#advTitle").textContent = readonly ? d.name : adv.original ? `Edit “${adv.original}”` : "New custom profile";
  $("#advLock").style.display = readonly ? "" : "none";
  $("#advNameRow").style.display = readonly ? "none" : "";
  $("#advName").value = d.name;
  $("#advDelete").style.visibility = adv.original ? "visible" : "hidden";
  $("#advSave").disabled = readonly;
  setRange($("#advGain"), d.gain);
  $("#advLimiter").checked = d.limiter;
  $("#advHeadroom").checked = d.autoHeadroom;
  segmented($("#advRate"), ch.rates.map((r) => ({ value: r, label: (r / 1000).toString().replace(/\.0$/, "") + " kHz" })), d.rate,
    (r) => { d.rate = r; advChanged(); });
  renderFilters();
  advChanged();
}

function renderFilters() {
  const d = adv.draft, box = $("#advFilters");
  if (!d.filters.length) { box.replaceChildren(h("div", { class: "empty" }, "No filters — the EQ is flat.")); return; }
  box.replaceChildren(...d.filters.map((f, i) => {
    const num = (key, label, attrs) => {
      const input = h("input", { type: "number", value: +f[key].toFixed(key === "q" ? 3 : 2), ...attrs });
      if (key === "gain" && !hasGain(f.type)) input.disabled = true;
      input.addEventListener("input", () => { if (input.value !== "" && isFinite(+input.value)) { f[key] = +input.value; advChanged(); } });
      return h("label", {}, label, input);
    };
    const on = h("input", { type: "checkbox" }); on.checked = f.enabled;
    on.addEventListener("change", () => { f.enabled = on.checked; row.classList.toggle("off", !f.enabled); advChanged(); });
    const type = h("select", {}, ...FILTER_TYPES.map(([v, l]) => h("option", { value: v, ...(v === f.type ? { selected: "" } : {}) }, l)));
    type.addEventListener("change", () => { f.type = type.value; renderFilters(); advChanged(); });
    const row = h("div", { class: "filter-row" + (f.enabled ? "" : " off") },
      h("span", { class: "n" }, i + 1), on, type,
      num("freq", "Freq (Hz)", { min: 10, max: 96000, step: "any" }),
      num("gain", "Gain (dB)", { min: -24, max: 24, step: "any" }),
      num("q", "Q", { min: 0.05, max: 50, step: "any" }),
      h("button", { type: "button", class: "del", title: "Remove filter", onclick: () => { d.filters.splice(i, 1); renderFilters(); advChanged(); } }, "✕"));
    return row;
  }));
}

function advChanged() {
  const { ch, draft: d } = adv;
  $("#advGainVal").textContent = (d.gain > 0 ? "+" : "") + d.gain.toFixed(1) + " dB";
  $("#advHeadroom").disabled = d.limiter;
  $("#advHeadroom").closest(".row").classList.toggle("off", d.limiter);
  plotEq($("#advCurve"), d.filters, { fs: d.rate, gain: d.gain, color: chColor(ch.id), range: 18, detail: true });
  const def = ch.defaults, notes = [];
  notes.push(d.limiter ? "EQ runs at the gain above; the limiter catches peaks above 0 dBFS."
    : d.autoHeadroom ? "No limiter: the gain is lowered by the EQ's peak boost so it can't clip." : "No limiter and no headroom: loud boosted sounds can clip.");
  if (d.rate !== ch.rate) notes.push("Changing the rate briefly interrupts this channel's audio.");
  if (d.limiter !== def.limiter || d.rate !== def.rate) notes.push(`Hub default for ${ch.name}: ${def.limiter ? "limiter on" : "no limiter"}, ${def.rate / 1000} kHz.`);
  $("#advChainHint").textContent = notes.join(" ");
}

// Enter in a field must not submit the dialog form (its first button is Close).
$$("#adv [data-close]").forEach((b) => b.addEventListener("click", () => $("#adv").close()));
$("#advForm").addEventListener("keydown", (e) => { if (e.key === "Enter" && e.target.tagName === "INPUT") e.preventDefault(); });
$("#advGain").addEventListener("input", () => { adv.draft.gain = +$("#advGain").value; advChanged(); });
$("#advLimiter").addEventListener("change", () => { adv.draft.limiter = $("#advLimiter").checked; advChanged(); });
$("#advHeadroom").addEventListener("change", () => { adv.draft.autoHeadroom = $("#advHeadroom").checked; advChanged(); });
$("#advName").addEventListener("input", () => (adv.draft.name = $("#advName").value));
$("#advAdd").addEventListener("click", () => {
  adv.draft.filters.push({ type: "Peaking", freq: 1000, gain: 0, q: 1, enabled: true });
  renderFilters(); advChanged();
  $("#advFilters").lastElementChild.scrollIntoView({ block: "nearest" });
});
$("#advCopy").addEventListener("click", () => {
  const names = new Set(adv.ch.presets.map((p) => p.name.toLowerCase()));
  let name = `${adv.draft.name} (custom)`;
  for (let n = 2; names.has(name.toLowerCase()); n++) name = `${adv.draft.name} (custom ${n})`;
  Object.assign(adv, { readonly: false, original: null });
  adv.draft.name = name;
  renderAdvanced();
  $("#advName").focus(); $("#advName").select();
});
/** Errors show inside the dialog: the toast sits underneath the modal layer. */
async function advCall(cmd, args) {
  $("#advError").textContent = "";
  try { return await call(cmd, args); } catch (err) { $("#advError").textContent = err.message; return null; }
}
$("#advSave").addEventListener("click", async () => {
  const { ch, draft: d, original } = adv;
  const r = await advCall("audio.saveCustom", { channel: ch.id, original, preset: d });
  if (!r) return;
  toast(`${ch.name}: saved “${d.name.trim()}”`);
  replaceChannel(r);
  $("#adv").close();
});
$("#advDelete").addEventListener("click", async () => {
  const { ch, original } = adv;
  if (!confirm(`Delete the custom profile “${original}”?`)) return;
  const r = await advCall("audio.deleteCustom", { channel: ch.id, name: original });
  if (!r) return;
  toast(`Deleted “${original}”`);
  replaceChannel(r);
  $("#adv").close();
});

function updateChatMixLabel() {
  const v = +$("#chatMix").value;
  $("#chatMixVal").textContent = v === 0 ? "Balanced" : v < 0 ? `Game ${-v}%` : `Chat ${v}%`;
}
let chatTimer;
$("#chatMix").addEventListener("input", () => {
  updateChatMixLabel();
  clearTimeout(chatTimer);
  chatTimer = setTimeout(() => call("audio.setChatMix", { value: +$("#chatMix").value / 100 }).catch((e) => toast(e.message, true)), 40);
});
$("#chatMix").addEventListener("dblclick", () => { setRange($("#chatMix"), 0); updateChatMixLabel(); call("audio.setChatMix", { value: 0 }); });

// ---------------------------------------------------------------- settings
let appearance = null; // { theme, customTheme, followWindows, devicePictures }

async function loadSettings() {
  const s = await call("settings.load");
  appearance = { theme: s.theme, customTheme: s.customTheme, followWindows: s.followWindows, devicePictures: s.devicePictures };
  renderAppearance();
  $("#autostart").checked = s.autostart;
  setRange($("#alertPct"), s.alertPercent); $("#alertVal").textContent = s.alertPercent + "%";
  $("#normalMode").checked = s.restoreNormalMode;
  $("#about").textContent = `Hub ${s.version} · GPL-3.0`;
  const colors = (model) => model.colors.map((c) => ({ value: c, label: c[0].toUpperCase() + c.slice(1) }));
  segmented($("#mouseColor"), colors(state.mouse.model), s.mouseColor, (v) => { state.mouseColor = v; applyModels(); saveSettings(); });
  segmented($("#headsetColor"), colors(state.headset.model), s.headsetColor, (v) => { state.headsetColor = v; applyModels(); saveSettings(); });
}
const saveSettings = (message = "Settings saved") => act("Settings", () => call("settings.save", {
  autostart: $("#autostart").checked, alertPercent: +$("#alertPct").value, restoreNormalMode: $("#normalMode").checked,
  mouseColor: $("#mouseColor .active")?.textContent.toLowerCase() || "black",
  headsetColor: $("#headsetColor .active")?.textContent.toLowerCase() || "black",
  ...(appearance || {}), windowBackground: token("--bg"),
}), message);

// ----- appearance: theme presets, my theme, device pictures
const THEME_EDIT = [["bg", "background"], ["side", "sidebar"], ["panel", "panels"], ["text", "text"],
  ["muted", "secondary text"], ["accent", "accent"], ["line", "lines"]];

/** The colours "my theme" stores: only the editable ones, so derived colours (button text etc.) stay readable. */
const editable = (t) => Object.fromEntries([...THEME_EDIT.map(([k]) => k), "labelFont"].map((k) => [k, t[k]]));

function renderThemeSwatches() {
  const a = appearance;
  $("#themes").replaceChildren(...Object.keys(THEME_NAMES).map((id) => {
    const t = id === "custom" ? resolveTheme({ theme: "custom", customTheme: a.customTheme }) : THEMES[id];
    const sw = h("div", { class: "sw" }, ...[t.bg, t.panel, t.accent].map((c) => { const i = h("i"); i.style.background = c; return i; }));
    return h("button", { class: "theme-swatch" + (a.theme === id ? " active" : ""), onclick: () => setAppearance({ theme: id }) }, sw, THEME_NAMES[id]);
  }));
}

function renderAppearance() {
  const a = appearance, current = resolveTheme(a);
  renderThemeSwatches();
  const follow = canFollowWindows(a.theme);
  $("#followWindows").checked = a.followWindows; $("#followWindows").disabled = !follow;
  $("#followHint").textContent = follow ? "" : "instrument only";
  $("#themeEdit").replaceChildren(...THEME_EDIT.map(([key, label]) => {
    const input = h("input", { type: "color", value: current[key] });
    input.addEventListener("input", () => editTheme(key, input.value));
    return h("label", {}, label, input);
  }), (() => {
    const select = h("select", {}, ...[["mono", "mono"], ["sans", "sans"]].map(([v, l]) => h("option", { value: v, ...(current.labelFont === v ? { selected: "" } : {}) }, l)));
    select.addEventListener("change", () => editTheme("labelFont", select.value));
    return h("label", {}, "labels", select);
  })());
  segmented($("#devicePictures"), [{ value: "photo", label: "photo" }, { value: "drawing", label: "drawing" }], a.devicePictures,
    (v) => setAppearance({ devicePictures: v }));
}

let appearanceTimer;
/** Applies a change at once and saves it shortly after (colour pickers fire many events). */
function setAppearance(change, { rerender = true } = {}) {
  appearance = { ...appearance, ...change };
  state.devicePictures = appearance.devicePictures;
  applyTheme(appearance);
  if (rerender) renderAppearance();
  clearTimeout(appearanceTimer);
  appearanceTimer = setTimeout(() => saveSettings(null), 300);
}

/** Editing a colour turns the current look into "my theme" and changes it there. */
function editTheme(key, value) {
  if (appearance.theme !== "custom" && Object.keys(appearance.customTheme || {}).length
      && !confirm(`Start "my theme" over from ${THEME_NAMES[appearance.theme]}? Your current "my theme" will be replaced.`)) {
    renderAppearance(); // put the picker back to the preset's colour
    return;
  }
  const base = appearance.theme === "custom" ? appearance.customTheme : editable(resolveTheme(appearance));
  // the editor stays in place (re-rendering it would close the open colour picker); only the swatches change
  setAppearance({ theme: "custom", followWindows: false, customTheme: { ...base, [key]: value } }, { rerender: false });
  renderThemeSwatches();
  $("#followWindows").checked = false; $("#followWindows").disabled = true; $("#followHint").textContent = "instrument only";
}

$("#followWindows").addEventListener("change", () => setAppearance({ followWindows: $("#followWindows").checked }));
$("#themeReset").addEventListener("click", () => {
  const preset = appearance.theme === "custom" ? "instrument-light" : appearance.theme;
  // what is on screen (with Follow Windows that can be the dark version of the chosen preset)
  const from = appearance.theme === "custom" ? THEMES[preset] : resolveTheme(appearance);
  setAppearance({ customTheme: editable(from) });
  toast("My theme reset to " + THEME_NAMES[preset]);
});
$("#autostart").addEventListener("change", () => saveSettings());
$("#normalMode").addEventListener("change", () => saveSettings());
$("#alertPct").addEventListener("input", () => ($("#alertVal").textContent = $("#alertPct").value + "%"));
$("#alertPct").addEventListener("change", () => saveSettings());

// ---------------------------------------------------------------- system
let specsText = "", lastLive = null, liveTimer = null;

async function loadSystem() {
  renderEvents();
  renderDevices();
  try {
    const s = await call("system.specs");
    specsText = s.text;
    $("#sysSpecs").replaceChildren(...s.sections.map((sec) => h("div", { class: "card spec" },
      h("div", { class: "card-title" }, sec.title),
      ...sec.rows.map((r) => h("div", { class: "spec-row" }, h("span", {}, r.label), h("b", {}, r.value))))));
  } catch (err) { $("#sysSpecs").replaceChildren(h("p", { class: "setup-error" }, "Couldn't read the specs: " + err.message)); }
}

$("#copySpecs").addEventListener("click", async () => {
  if (!specsText) return;
  try { await navigator.clipboard.writeText(specsText); toast("Specs copied"); }
  catch { toast("Couldn't copy to the clipboard", true); }
});

async function renderDevices() {
  let d;
  try { d = await call("system.devices"); } catch (err) { $("#sysConnected").textContent = err.message; return; }
  $("#sysConnected").replaceChildren(...(d.connected.length ? d.connected.map((x) => h("div", { class: "dev-row" },
    h("span", { class: "dev-kind" }, x.kind), h("span", {}, x.name))) : [h("p", { class: "muted" }, "No USB devices found.")]));
  const time = (t) => {
    const d = new Date(t), today = new Date().toDateString() === d.toDateString();
    return (today ? "" : d.toLocaleDateString(undefined, { day: "numeric", month: "short" }) + " ")
      + d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
  };
  $("#sysActivity").replaceChildren(...(d.activity.length ? d.activity.slice(0, 50).map((e) => h("div", { class: "dev-row" },
    h("span", { class: "ev-time" }, time(e.time)), h("span", { class: "dev-action " + e.action }, e.action), h("span", {}, e.name)))
    : [h("p", { class: "muted" }, "Nothing plugged in or out since Hub started recording.")]));
}

const KIND_LABEL = { "gpu-reset": "gpu reset", "hardware-error": "hardware", bluescreen: "blue screen", "unexpected-restart": "restart" };

async function renderEvents() {
  let events;
  try { events = await call("system.events"); } catch (err) { $("#sysEvents").textContent = err.message; return; }
  $("#sysEventsSummary").textContent = stabilitySummary(state?.stability);
  if (!events.length) {
    $("#sysEvents").replaceChildren(h("p", { class: "muted" }, "No crashes, GPU resets or hardware errors in the last 7 days."));
    return;
  }
  const day = (d) => {
    const today = new Date(); today.setHours(0, 0, 0, 0);
    const diff = Math.round((today - new Date(d.getFullYear(), d.getMonth(), d.getDate())) / 864e5);
    return diff === 0 ? "today" : diff === 1 ? "yesterday" : d.toLocaleDateString(undefined, { weekday: "long", day: "numeric", month: "short" }).toLowerCase();
  };
  const groups = new Map();
  for (const e of events) {
    const d = new Date(e.time), k = day(d);
    if (!groups.has(k)) groups.set(k, []);
    groups.get(k).push({ ...e, d });
  }
  $("#sysEvents").replaceChildren(...[...groups].map(([k, list]) => h("div", { class: "ev-day" },
    h("div", { class: "ev-date" }, k),
    ...list.map((e) => h("div", { class: "ev-row" },
      h("span", { class: "ev-time" }, e.d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", hourCycle: "h23" })),
      h("span", { class: "ev-kind " + e.kind }, KIND_LABEL[e.kind] || e.kind),
      h("span", { class: "ev-text" }, h("b", {}, e.title), e.detail ? h("small", {}, e.detail) : null),
      e.recordId ? h("button", { class: "btn ghost small", title: "Open this record in Event Viewer",
        onclick: () => call("system.openEvent", { log: e.log, recordId: e.recordId }).catch((err) => toast(err.message, true)) }, "event viewer") : null)))));
}

function stabilitySummary(s) {
  if (!s) return "";
  const parts = [];
  if (s.gpuResets) parts.push(`${s.gpuResets} gpu reset${s.gpuResets > 1 ? "s" : ""}`);
  if (s.hardwareErrors) parts.push(`${s.hardwareErrors} hardware error${s.hardwareErrors > 1 ? "s" : ""}`);
  if (s.bluescreens) parts.push(`${s.bluescreens} blue screen${s.bluescreens > 1 ? "s" : ""}`);
  if (s.restarts) parts.push(`${s.restarts} unexpected restart${s.restarts > 1 ? "s" : ""}`);
  return parts.length ? parts.join(" · ") + " this week" : "no crashes or gpu resets this week";
}

/** Live CPU/GPU numbers every 2 s, only while System or Overview is on screen in a visible window. */
function setLive(on) {
  on = on && !document.hidden;
  if (!on) { clearTimeout(liveTimer); liveTimer = null; return; }
  if (liveTimer) return;
  const tick = async () => {
    try { lastLive = await call("system.live"); renderLive(); } catch { /* next tick */ }
    if (liveTimer) liveTimer = setTimeout(tick, 2000);
  };
  liveTimer = setTimeout(tick, 0);
}
document.addEventListener("visibilitychange", () => setLive(isVisible("system") || isVisible("overview")));

function renderLive() {
  const l = lastLive || {};
  const tile = (label, value, unit) => h("div", { class: "card live" }, h("div", { class: "card-title" }, label),
    h("div", { class: "live-val" }, value == null ? "—" : value, h("small", {}, value == null ? "" : unit)));
  $("#sysLive").replaceChildren(
    tile("cpu load", l.cpuLoad == null ? null : Math.round(l.cpuLoad), "%"),
    tile("cpu clock", l.cpuMhz == null ? null : (l.cpuMhz / 1000).toFixed(2), " GHz"),
    tile("gpu temp", l.gpuTempC == null ? null : Math.round(l.gpuTempC), " °C"),
    tile("gpu load", l.gpuLoad == null ? null : Math.round(l.gpuLoad), "%"));
  renderOverviewSystem();
}

function renderOverviewSystem() {
  if (!state) return;
  const parts = [stabilitySummary(state.stability)];
  if (lastLive?.gpuTempC != null) parts.push(`gpu ${Math.round(lastLive.gpuTempC)} °C`);
  if (lastLive?.cpuLoad != null) parts.push(`cpu ${Math.round(lastLive.cpuLoad)}%`);
  $("#ovSystemBody").textContent = parts.filter(Boolean).join(" · ");
}

// ---------------------------------------------------------------- theme
/** Canvases are drawn with the theme's colours, so they are redrawn when it changes. */
window.addEventListener("hub:theme", () => {
  if (state) applyModels();
  if ($("#curve").offsetParent) drawCurve();
  if (audio) audio.channels.forEach((c) => drawResponse(c));
  if (adv && $("#adv").open) advChanged();
});

// ---------------------------------------------------------------- boot
call("settings.load").then((s) => {
  appearance = { theme: s.theme, customTheme: s.customTheme, followWindows: s.followWindows, devicePictures: s.devicePictures };
  applyTheme(appearance);
}).catch(() => applyTheme({}));
$$("input[type=range]").forEach(paintRange);
setLive(isVisible("overview"));
call("state").then(onState).catch(() => {});
