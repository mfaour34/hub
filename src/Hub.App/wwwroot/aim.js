"use strict";

// errors are kept for the `--aim --shots` dev run, which writes them next to the screenshots
window.__aimErrors = [];
addEventListener("error", (e) => window.__aimErrors.push(e.message));
addEventListener("unhandledrejection", (e) => window.__aimErrors.push(String(e.reason?.message || e.reason)));

// ---------------------------------------------------------------- bridge (same message shape as the main window)
const host = window.chrome && window.chrome.webview;
const pending = new Map();
let seq = 0;
function call(cmd, args = {}) {
  return new Promise((resolve, reject) => {
    if (!host) return reject(new Error("Not running inside Hub"));
    const id = ++seq;
    pending.set(id, { resolve, reject });
    host.postMessage({ id, cmd, args });
  });
}
if (host) host.addEventListener("message", (e) => {
  const m = e.data, p = pending.get(m.id);
  if (!p) return;
  pending.delete(m.id);
  m.ok ? p.resolve(m.data) : p.reject(new Error(m.error));
});

const $ = (s, root = document) => root.querySelector(s);
function h(tag, attrs = {}, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) k.startsWith("on") ? el.addEventListener(k.slice(2), v) : el.setAttribute(k, v);
  el.append(...kids.flat().filter((k) => k != null && k !== false));
  return el;
}
let toastTimer;
function toast(text, error = false) {
  const t = $("#toast");
  t.textContent = text; t.className = "toast show" + (error ? " error" : "");
  clearTimeout(toastTimer); toastTimer = setTimeout(() => (t.className = "toast"), 2600);
}

// ---------------------------------------------------------------- setup
let init = null, games = [], turn = 0; // turn = degrees per mouse count with the current game and setting
const game = () => games.find((g) => g.id === $("#game").value) || games[0];
const fmt = (v, digits = 3) => (v == null || !Number.isFinite(v) ? "—" : (+v.toFixed(digits)).toString());

async function boot() {
  init = await call("aim.init");
  games = init.games;
  $("#game").replaceChildren(...games.map((g) => h("option", { value: g.id }, g.name + (g.verified ? "" : " (≈)"))));
  $("#game").value = init.settings.game;
  $("#sens").value = init.settings.sens ?? game().defaultSens;
  $("#dpi").value = init.dpi;
  $("#accelWarn").hidden = !init.accel;
  await setupChanged(false);
  if (init.settings.lastCm360) showFinderResult(init.settings.lastCm360, false);
}

let saveTimer, dpiTyped = false;
$("#dpi").addEventListener("input", () => (dpiTyped = true));
async function setupChanged(save = true) {
  const sens = +$("#sens").value, dpi = +$("#dpi").value, g = game();
  $("#gameNote").textContent = g.notes ? g.name + ": " + g.notes + (g.verified ? "" : ". Values marked ≈ are approximate.") : "";
  turn = sens > 0 ? (g.linear ? g.yaw * sens : await call("aim.degPerCount", { game: g.id, sens })) : 0;
  const cm = turn > 0 && dpi > 0 ? (360 / (turn * dpi)) * 2.54 : null;
  $("#cm360").textContent = fmt(cm, 1);
  $("#edpi").textContent = g.linear && sens > 0 && dpi > 0 ? fmt(sens * dpi, 0) : "—";
  renderConvert();
  if (finderResult && !$("#resultsSection").hidden && $("#results .finder-result")) showFinderResult(finderResult.cm360, finderResult.fresh);
  if (save) { clearTimeout(saveTimer); saveTimer = setTimeout(() => call("aim.save", { game: g.id, sens: sens || null, dpi: dpiTyped ? dpi || null : null }).catch(() => {}), 400); }
}
["#game", "#sens", "#dpi"].forEach((s) => $(s).addEventListener("input", () => setupChanged()));

async function renderConvert() {
  const sens = +$("#sens").value, dpi = +$("#dpi").value;
  let rows = [];
  if (sens > 0) try { rows = await call("aim.convert", { from: game().id, sens, dpi }); } catch { rows = []; }
  $("#convert").replaceChildren(...rows.map((r) => h("div", { class: "conv-row" },
    h("span", {}, r.name),
    h("b", {}, (r.verified ? "" : "≈ ") + fmt(r.sens, r.sens >= 10 ? 1 : 3)),
    h("small", {}, r.cm360 == null ? "" : fmt(r.cm360, 1) + " cm"),
    h("button", { class: "btn small", onclick: () => navigator.clipboard.writeText(fmt(r.sens, 4)).then(() => toast(`${r.name}: ${fmt(r.sens, 4)} copied`)) }, "copy"))));
}

$("#close").addEventListener("click", () => call("aim.close"));

// ---------------------------------------------------------------- view
const canvas = $("#view"), g2 = canvas.getContext("2d");
let W = 0, H = 0, dpr = 1;
function resize() {
  dpr = window.devicePixelRatio || 1;
  W = canvas.width = Math.round(innerWidth * dpr);
  H = canvas.height = Math.round(innerHeight * dpr);
}
addEventListener("resize", resize);
resize();

const rad = (d) => (d * Math.PI) / 180, deg = (r) => (r * 180) / Math.PI;
/** Horizontal FOV at this window's aspect ratio (Hor+ from the game's 16:9 value). */
function hfov() {
  const v = 2 * Math.atan(Math.tan(rad(game()?.hfov ?? 106.26) / 2) / (16 / 9)); // CS2's until the games arrive
  return deg(2 * Math.atan(Math.tan(v / 2) * (W / H)));
}
const dir = (az, el) => [Math.cos(rad(el)) * Math.sin(rad(az)), Math.sin(rad(el)), Math.cos(rad(el)) * Math.cos(rad(az))];

let yaw = 0, pitch = 0, dx = 0, dy = 0;

/** World direction → screen point (null behind the camera); f is the focal length in device pixels. */
function project([x, y, z], f) {
  const cy = Math.cos(rad(yaw)), sy = Math.sin(rad(yaw)), cp = Math.cos(rad(pitch)), sp = Math.sin(rad(pitch));
  const x1 = x * cy - z * sy, z1 = x * sy + z * cy;
  const y2 = y * cp - z1 * sp, z2 = y * sp + z1 * cp;
  if (z2 < 0.05) return null;
  return { x: W / 2 + (x1 / z2) * f, y: H / 2 - (y2 / z2) * f, z: z2 };
}

const TARGET_RADIUS = Math.atan(0.22 / 10) * (180 / Math.PI); // 0.22 m at 10 m, about 1.26°
const angleTo = (az, el) => {
  const a = dir(az, el), b = dir(yaw, pitch);
  return deg(Math.acos(Math.max(-1, Math.min(1, a[0] * b[0] + a[1] * b[1] + a[2] * b[2]))));
};
const wrap = (d) => ((((d + 180) % 360) + 360) % 360) - 180;

function draw() {
  const f = W / 2 / Math.tan(rad(hfov()) / 2);
  g2.fillStyle = token("--bg"); g2.fillRect(0, 0, W, H);
  // a wall grid every 10° for a sense of motion
  g2.strokeStyle = token("--line"); g2.lineWidth = dpr;
  for (let az = -90; az <= 90; az += 10) line(f, (t) => [az, -40 + 80 * t]);
  for (let el = -40; el <= 40; el += 10) line(f, (t) => [-90 + 180 * t, el]);
  // targets
  if (round) for (const t of round.targets()) {
    const p = project(dir(t.az, t.el), f);
    if (!p) continue;
    const r = (Math.tan(rad(TARGET_RADIUS)) / p.z) * f;
    g2.fillStyle = token("--accent");
    g2.beginPath(); g2.arc(p.x, p.y, r, 0, Math.PI * 2); g2.fill();
  }
  // crosshair
  const c = 7 * dpr, gap = 3 * dpr;
  g2.strokeStyle = token("--text"); g2.lineWidth = 2 * dpr;
  g2.beginPath();
  g2.moveTo(W / 2 - c - gap, H / 2); g2.lineTo(W / 2 - gap, H / 2); g2.moveTo(W / 2 + gap, H / 2); g2.lineTo(W / 2 + c + gap, H / 2);
  g2.moveTo(W / 2, H / 2 - c - gap); g2.lineTo(W / 2, H / 2 - gap); g2.moveTo(W / 2, H / 2 + gap); g2.lineTo(W / 2, H / 2 + c + gap);
  g2.stroke();
}
function line(f, at) {
  g2.beginPath();
  let started = false;
  for (let i = 0; i <= 36; i++) {
    const [az, el] = at(i / 36), p = project(dir(az, el), f);
    if (!p) { started = false; continue; }
    started ? g2.lineTo(p.x, p.y) : g2.moveTo(p.x, p.y);
    started = true;
  }
  g2.stroke();
}

// ---------------------------------------------------------------- input: raw counts through pointer lock
const locked = () => document.pointerLockElement === canvas;
async function lock() {
  try { await canvas.requestPointerLock({ unadjustedMovement: true }); return; }
  catch { /* raw input refused: try plain pointer lock */ }
  try { await canvas.requestPointerLock(); toast("Raw input isn't available here; Windows pointer settings apply", true); }
  catch { $("#pausedText").textContent = "Windows didn't hand over the mouse. Click resume again in a moment."; if (round) $("#paused").hidden = false; }
}
// every event counts, not just the last per frame: a 2000–8000 Hz mouse sends many per frame
document.addEventListener("mousemove", (e) => { if (locked()) { dx += e.movementX; dy += e.movementY; } });
document.addEventListener("mousedown", (e) => { if (locked() && e.button === 0 && round && !round.paused) round.click(); });
document.addEventListener("pointerlockchange", () => {
  if (!round) return;
  if (locked()) { round.paused = false; $("#paused").hidden = true; }
  else { round.paused = true; $("#paused").hidden = false; }
});
$("#resume").addEventListener("click", lock);
// Esc while the mouse is free (the browser already used the first Esc to release it) leaves the round
document.addEventListener("keydown", (e) => { if (e.key === "Escape" && round && !locked()) endRound(true); });
$("#quitRound").addEventListener("click", () => endRound(true));

// ---------------------------------------------------------------- rounds
let round = null, lastFrame = 0;

function frame(now) {
  const dt = Math.min(0.1, (now - lastFrame) / 1000);
  lastFrame = now;
  yaw = wrap(yaw + dx * turn);
  pitch = Math.max(-89, Math.min(89, pitch - dy * turn));
  dx = dy = 0;
  if (round && !round.paused) round.tick(dt);
  draw();
  if (round) requestAnimationFrame(frame);
}

function start(r) {
  round = r;
  round.paused = true; // nothing counts until the pointer is locked (pointerlockchange unpauses)
  yaw = 0; pitch = 0; dx = dy = 0;
  $("#menu").hidden = true; $("#hud").hidden = false; $("#paused").hidden = true;
  $("#pausedText").textContent = "The round is on hold; time and clicks don't count until you're back.";
  $("#hudMode").textContent = r.label;
  lock();
  lastFrame = performance.now();
  requestAnimationFrame(frame);
}

/** Ends the round; quit = left early (no result). */
function endRound(quit = false) {
  const r = round;
  round = null;
  if (locked()) document.exitPointerLock();
  $("#hud").hidden = true; $("#paused").hidden = true;
  draw();
  r?.done?.(quit ? null : r.result());
}

/** Flick: targets one at a time on a wall at 10 m; time to hit and overshoots per target. */
function flickRound(count, label, done) {
  let target = null, hits = 0, clicks = 0, time = 0, spawned = 0, errors = [];
  const times = [], overshoots = [];
  const spawn = () => {
    let az, el;
    do { az = -35 + Math.random() * 70; el = -12 + Math.random() * 24; }
    while (target && Math.hypot(az - target.az, el - target.el) < 8);
    target = { az, el }; spawned = time; errors = [];
  };
  spawn();
  return {
    label, done, paused: false,
    targets: () => (target ? [target] : []),
    tick(dt) {
      time += dt;
      const e = wrap(target.az - yaw);
      errors.push(Math.abs(e) <= TARGET_RADIUS ? 0 : Math.sign(e)); // on target counts as neither side
      $("#hudInfo").textContent = `${hits} / ${count}`;
    },
    click() {
      clicks++;
      if (angleTo(target.az, target.el) > TARGET_RADIUS) return;
      hits++;
      times.push(time - spawned);
      overshoots.push(signChanges(errors));
      if (hits >= count) endRound(); else spawn();
    },
    result: () => ({ mode: "flick", hits, clicks, seconds: time,
      accuracy: clicks ? hits / clicks : 0, avgTime: times.length ? times.reduce((a, b) => a + b) / times.length : null,
      overshoots: overshoots.length ? overshoots.reduce((a, b) => a + b) / overshoots.length : null,
      score: clicks && time > 0 ? (hits / time) * Math.pow(hits / clicks, 2) : 0 }),
  };
}
/** Same rule as Hub.Core's Scoring.Overshoots: each crossing of the target on the way to it. */
function signChanges(s) {
  let n = 0, last = 0;
  for (const v of s) { if (v !== 0) { if (last !== 0 && v !== last) n++; last = v; } }
  return n;
}

/** Track: 30 s on a target strafing left and right at 4 m/s at 10 m, changing direction at random. */
function trackRound() {
  const speed = deg(Math.atan(4 / 10)); // °/s
  let az = 0, el = (Math.random() - 0.5) * 10, v = speed, flip = 0.6, time = 0, on = 0;
  return {
    label: "track", paused: false,
    targets: () => [{ az, el }],
    tick(dt) {
      time += dt;
      flip -= dt;
      if (flip <= 0) { v = -v; flip = 0.4 + Math.random() * 0.8; }
      az += v * dt;
      if (Math.abs(az) > 35) { az = Math.sign(az) * 35; v = -v; }
      if (angleTo(az, el) <= TARGET_RADIUS) on += dt;
      $("#hudInfo").textContent = `${Math.max(0, 30 - time).toFixed(1)} s · ${Math.round((on / Math.max(time, 1e-6)) * 100)} % on target`;
      if (time >= 30) endRound();
    },
    click() {},
    result: () => ({ mode: "track", onTarget: time > 0 ? on / time : 0 }),
    done: showResult,
  };
}

function showResult(r) {
  if (!r) { $("#menu").hidden = false; return; }
  const tile = (value, label) => h("div", { class: "readout" }, h("b", {}, value), h("small", {}, label));
  $("#results").replaceChildren(r.mode === "flick"
    ? h("div", { class: "result" },
      tile(`${r.hits}/${r.clicks}`, "hits / clicks"), tile(Math.round(r.accuracy * 100) + " %", "accuracy"),
      tile(r.avgTime == null ? "—" : Math.round(r.avgTime * 1000) + " ms", "time to target"),
      tile(r.overshoots == null ? "—" : r.overshoots.toFixed(2), "overshoots per target"))
    : h("div", { class: "result" }, tile(Math.round(r.onTarget * 100) + " %", "time on target")));
  $("#resultsSection").hidden = false;
  $("#menu").hidden = false;
}

// ---------------------------------------------------------------- sensitivity finder
async function runFinder() {
  const g = game(), dpi = +$("#dpi").value, sens = +$("#sens").value;
  if (!(dpi > 0 && sens > 0 && turn > 0)) { toast("Set a sensitivity and DPI first", true); return; }
  const original = sens, startCm = (360 / (turn * dpi)) * 2.54;
  let state = await call("aim.finder.start", { cm360: startCm }), roundNo = 0;
  while (state.pair) {
    const order = Math.random() < 0.5 ? [0, 1] : [1, 0], scores = [0, 0];
    for (let i = 0; i < 2; i++) {
      const which = order[i], cm = state.pair[which];
      $("#sens").value = await call("aim.sensFor", { game: g.id, cm360: cm, dpi });
      await setupChanged(false);
      roundNo++;
      const r = await playFlick(15, `finder · round ${roundNo} of ${state.steps * 2} · setting ${i === 0 ? "A" : "B"}`);
      if (!r) { $("#sens").value = original; await setupChanged(false); $("#menu").hidden = false; return; } // left early
      scores[which] = r.score;
    }
    state = await call("aim.finder.record", { low: scores[0], high: scores[1] });
  }
  $("#sens").value = original;
  await setupChanged(false);
  showFinderResult(state.result, true);
}
/**
 * One finder round. The pointer can only be locked from a click, so each round starts from a button; the round's
 * setting is only named A or B, so knowing which is faster doesn't bias the comparison.
 */
const playFlick = (count, label) => new Promise((resolve) => {
  $("#menu").hidden = true;
  $("#interludeTitle").textContent = label;
  $("#interludeText").textContent = `${count} targets. Go for speed and accuracy, the way you'd play.`;
  $("#interlude").hidden = false;
  $("#interludeGo").onclick = () => { $("#interlude").hidden = true; start(flickRound(count, label, resolve)); };
  $("#interludeQuit").onclick = () => { $("#interlude").hidden = true; resolve(null); };
});

let finderResult = null;
async function showFinderResult(cm360, fresh) {
  finderResult = { cm360, fresh };
  const g = game(), dpi = +$("#dpi").value;
  const sens = dpi > 0 ? await call("aim.sensFor", { game: g.id, cm360, dpi }) : null;
  $("#results").replaceChildren(h("div", { class: "finder-result" },
    h("span", { class: "big" }, fmt(cm360, 1) + " cm/360°"),
    h("span", {}, `${g.name}: ${fmt(sens, 3)} at ${dpi} dpi`),
    h("button", { class: "btn primary", onclick: async () => {
      // the game and DPI chosen now, not when the result was drawn
      const game_ = game(), dpiNow = +$("#dpi").value;
      if (!(dpiNow > 0)) { toast("Set your DPI first", true); return; }
      const s = +(await call("aim.sensFor", { game: game_.id, cm360, dpi: dpiNow })).toFixed(4);
      $("#sens").value = s;
      await setupChanged(false);
      await call("aim.save", { game: game_.id, sens: s, dpi: dpiTyped ? dpiNow : null, lastCm360: cm360 });
      toast(`Saved: ${game_.name} ${s}`);
    } }, "use this")),
    h("p", { class: "hint" }, fresh ? "Found by comparing pairs of settings; run it again on another day to confirm." : "Your last finder result."));
  $("#resultsSection").hidden = false;
  $("#menu").hidden = false;
}

document.querySelectorAll(".mode").forEach((b) => b.addEventListener("click", () => {
  if (!(turn > 0)) { toast("Set a sensitivity first", true); return; }
  if (b.dataset.mode === "flick") start(flickRound(30, "flick", showResult));
  if (b.dataset.mode === "track") start(trackRound());
  if (b.dataset.mode === "finder") runFinder();
}));

// ---------------------------------------------------------------- boot
draw();
boot().then(draw).catch((e) => { window.__aimErrors.push("boot: " + e.message); toast(e.message, true); });
