// Gavel demo page. The estimate, its range and the per-field effects come from the F# code in
// src/Core, compiled to JavaScript by Fable (fable-out/Exports.js) and bundled into docs/gavel.js.
// This file only builds the form and draws the plate.
import { load, estimate } from "../fable-out/Exports.js";

const $ = (id) => document.getElementById(id);
const catalog = JSON.parse($("g-catalog").textContent);
const examples = JSON.parse($("g-examples").textContent);
const SVGNS = "http://www.w3.org/2000/svg";
const money = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 0 });
const count = new Intl.NumberFormat("en-US");
const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

let ready = null;
let pulled = null; // the example sale currently on the tag

// ---------- small helpers ----------
function hash(s) {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); }
  return h >>> 0;
}
function el(tag, attrs = {}, text) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
  if (text !== undefined) e.textContent = text;
  return e;
}
function svg(tag, attrs = {}, text) {
  const e = document.createElementNS(SVGNS, tag);
  for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
  if (text !== undefined) e.textContent = text;
  return e;
}
function option(select, value, label) {
  const o = el("option", { value }, label);
  select.append(o);
  return o;
}
function ensureOption(select, value, label) {
  if (![...select.options].some((o) => o.value === value)) option(select, value, label ?? value);
  select.value = value;
}
const pct = (e) => Math.exp(e) - 1;
function pctText(e) {
  const p = pct(e) * 100;
  const a = Math.abs(p);
  const s = a >= 10 ? a.toFixed(0) : a.toFixed(1);
  return (p >= 0 ? "+" : "−") + s + "%";
}
/** "Track Type Tractor, Dozer - 105.0 to 130.0 Horsepower" -> "105–130 HP" */
function shortClass(desc) {
  if (!desc) return "—";
  const tail = (desc.split(" - ")[1] || desc).trim();
  return tail
    .replace(/(\d+)\.0\b/g, "$1")
    .replace(/(\d)(\d{3})\b/g, "$1,$2")
    .replace(/ to /g, "–")
    .replace(/ ?\+ ?/g, "+ ")
    .replace("Horsepower", "HP")
    .replace("Metric Tons", "t")
    .replace("Lb Operating Capacity", "lb cap.")
    .replace("Ft Standard Digging Depth", "ft dig")
    .replace("Unidentified", "unlisted");
}

// ---------- DYMO label tape: letters pressed slightly unevenly ----------
function emboss(node) {
  const text = node.textContent.trim();
  const h = hash(text);
  if (!node.style.getPropertyValue("--tilt")) node.style.setProperty("--tilt", (((h % 21) - 10) / 10 * 1.1).toFixed(2) + "deg");
  node.textContent = "";
  node.append(el("span", { class: "sr-only" }, text));
  const shown = el("span", { "aria-hidden": "true" });
  [...text.toUpperCase()].forEach((c, i) => {
    const s = el("span", { class: "ch" }, c === " " ? " " : c);
    const dy = ((hash(text + i) % 5) - 2) * 0.35;
    if (dy) s.style.setProperty("--dy", dy + "px");
    shown.append(s);
  });
  node.append(shown);
}
document.querySelectorAll("[data-dymo]").forEach(emboss);

// ---------- theme switch ----------
const themeBtn = $("theme");
function isDark() {
  const t = document.documentElement.dataset.theme;
  return t ? t === "dark" : window.matchMedia("(prefers-color-scheme: dark)").matches;
}
function syncTheme() { themeBtn.setAttribute("aria-pressed", String(isDark())); }
themeBtn.addEventListener("click", () => {
  const next = isDark() ? "light" : "dark";
  document.documentElement.dataset.theme = next;
  try { localStorage.setItem("gavel-theme", next); } catch (e) { /* storage unavailable */ }
  syncTheme();
});
window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", syncTheme);
syncTheme();

// ---------- the form ----------
const groupBox = $("groups");
const f = {
  model: $("f-model"), series: $("f-series"), cls: $("f-class"), year: $("f-year"), hours: $("f-hours"),
  state: $("f-state"), cab: $("f-cab"), month: $("f-month"), saleYear: $("f-saleyear"),
};
const optBox = $("opts");
const byCode = Object.fromEntries(catalog.groups.map((g) => [g.code, g]));
const GROUP_WORDS = { TTT: "Dozer", TEX: "Excavator", BL: "Backhoe loader", WL: "Wheel loader", SSL: "Skid steer", MG: "Motor grader" };

// dozers first: this is the bulldozer project
const DEFAULT_GROUP = catalog.groups.some((g) => g.code === "TTT") ? "TTT" : catalog.groups[0].code;
catalog.groups.forEach((g) => {
  const id = "g-" + g.code;
  const input = el("input", { type: "radio", name: "group", id, value: g.code });
  if (g.code === DEFAULT_GROUP) input.checked = true;
  const label = el("label", { for: id });
  label.append(el("span", { class: "code" }, g.code), el("span", { class: "what" }, GROUP_WORDS[g.code] || g.name));
  groupBox.append(input, label);
  input.addEventListener("change", () => { fillGroup(g.code); update(); });
});
catalog.states.forEach((s) => option(f.state, s, s));
f.state.value = catalog.states.includes("Florida") ? "Florida" : catalog.states[0];
MONTHS.forEach((m, i) => option(f.month, String(i + 1), m));
for (let y = 2012; y >= 1989; y--) option(f.saleYear, String(y), String(y));
f.month.value = "4";
f.saleYear.value = "2012";

const currentGroup = () => groupBox.querySelector("input:checked").value;

function fillModels(g) {
  f.model.textContent = "";
  g.models.forEach((m) => option(f.model, m.name, `${m.name} \u00b7 ${count.format(m.n)}`));
  option(f.model, "", "Other / not listed");
}
function fillSeries(g) {
  const m = g.models.find((x) => x.name === f.model.value);
  f.series.textContent = "";
  option(f.series, "", "none / unknown");
  (m ? m.series : []).forEach((s) => option(f.series, s, s));
}
function fillGroup(code, keep = false) {
  const g = byCode[code];
  fillModels(g);
  f.cls.textContent = "";
  g.sizeClasses.forEach((c) => option(f.cls, c.value, shortClass(c.value)));
  option(f.cls, "", "unknown");
  f.cab.textContent = "";
  g.cabs.forEach((c) => option(f.cab, c.value, c.value));
  option(f.cab, "", "unknown");
  optBox.textContent = "";
  g.options.forEach((o) => {
    const wrap = el("div");
    const id = "o-" + o.column;
    const lab = el("label", { class: "dymo", for: id }, o.label);
    const sel = el("select", { id, "data-column": o.column });
    option(sel, "", "unknown");
    o.values.forEach((v) => option(sel, v.value, v.value));
    sel.addEventListener("change", update);
    wrap.append(lab, sel);
    optBox.append(wrap);
    emboss(lab);
  });
  if (!keep) {
    const m = g.models[0];
    f.model.value = m.name;
    fillSeries(g);
    modelDefaults(g);
  }
}
function modelDefaults(g) {
  const m = g.models.find((x) => x.name === f.model.value);
  if (!m) return;
  f.series.value = m.series[0] ?? "";
  if ([...f.cls.options].some((o) => o.value === m.sizeClass)) f.cls.value = m.sizeClass;
  f.year.value = String(m.year);
}
f.model.addEventListener("change", () => { const g = byCode[currentGroup()]; fillSeries(g); modelDefaults(g); update(); });
for (const k of ["series", "cls", "state", "cab", "month", "saleYear"]) f[k].addEventListener("change", update);
for (const k of ["year", "hours"]) f[k].addEventListener("input", update);

fillGroup(DEFAULT_GROUP);
f.hours.value = "6500";
const firstCab = byCode[DEFAULT_GROUP].cabs[0];
if (firstCab) f.cab.value = firstCab.value;

/** The raw sale record the model reads, using the dataset's own column names and codes. */
function record() {
  const r = {
    ProductGroup: currentGroup(),
    fiBaseModel: f.model.value,
    fiSecondaryDesc: f.series.value,
    fiProductClassDesc: f.cls.value,
    YearMade: f.year.value.trim() || "1000",
    MachineHoursCurrentMeter: f.hours.value.trim(),
    state: f.state.value,
    Enclosure: f.cab.value,
    saledate: `${f.month.value}/15/${f.saleYear.value} 0:00`,
  };
  optBox.querySelectorAll("select").forEach((s) => { r[s.dataset.column] = s.value; });
  return r;
}

// ---------- drawing ----------
function niceStep(span, target) {
  const raw = span / target;
  const p = Math.pow(10, Math.floor(Math.log10(raw)));
  const m = raw / p;
  const n = m < 1.5 ? 1 : m < 2.25 ? 2 : m < 3.5 ? 2.5 : m < 7.5 ? 5 : 10;
  return { step: n * p, minor: n === 2 ? 4 : 5 };
}
function kText(v) {
  if (v >= 1000) { const k = v / 1000; return "$" + (Number.isInteger(k) ? k : k.toFixed(1)) + "k"; }
  return "$" + v;
}
function groove(parent, x1, y1, x2, y2, cls = "tick") {
  parent.append(svg("line", { x1, y1: y1 + 1, x2: x2 + (x1 === x2 ? 1 : 0), y2: y2 + 1, class: "tick-hi" }));
  parent.append(svg("line", { x1, y1, x2, y2, class: cls }));
}

function drawRuler(est) {
  const r = $("ruler");
  r.textContent = "";
  const W = Math.round(r.parentElement.clientWidth) || 600, top = 26, body = 40;
  r.setAttribute("viewBox", `0 0 ${W} 92`);
  r.setAttribute("width", W);
  r.setAttribute("height", 92);
  const lo = Math.max(0, est.low), hi = est.high, span0 = hi - lo;
  const d0 = Math.max(0, lo - span0 * 0.32), d1 = hi + span0 * 0.32;
  const { step, minor } = niceStep(d1 - d0, W < 420 ? 4 : 6);
  const x = (v) => ((v - d0) / (d1 - d0)) * W;
  r.append(svg("title", {}, `Range ${money.format(lo)} to ${money.format(hi)}, estimate ${money.format(est.price)}`));
  r.append(svg("rect", { x: 0, y: top, width: W, height: body, rx: 3, class: "ruler-body" }));
  // band
  r.append(svg("rect", { x: x(lo), y: top, width: x(hi) - x(lo), height: body, class: "band" }));
  const sub = step / minor;
  const first = Math.ceil(d0 / sub) * sub;
  for (let v = first; v <= d1 + 1e-6; v += sub) {
    const k = Math.round(v / sub);
    const major = k % minor === 0;
    const mid = !major && minor % 2 === 0 && k % (minor / 2) === 0;
    const len = major ? 20 : mid ? 13 : 8;
    const xx = Math.round(x(v)) + 0.5;
    groove(r, xx, top, xx, top + len);
    if (major) r.append(svg("text", { x: xx + 3, y: top + body - 6, class: v >= lo && v <= hi ? "tick-label on-band" : "tick-label" }, kText(Math.round(v))));
  }
  // range ends + estimate marker
  const xe = x(est.price);
  r.append(svg("line", { x1: xe, y1: top - 4, x2: xe, y2: top + body, class: "marker-line" }));
  r.append(svg("path", { d: `M${xe - 7} ${top - 16} L${xe + 7} ${top - 16} L${xe} ${top - 4} Z`, class: "marker" }));
  const r100 = (v) => money.format(Math.round(v / 100) * 100);
  const loLabel = svg("text", { x: x(lo), y: top + body + 20, class: "band-label", "text-anchor": x(lo) < 60 ? "start" : "middle" }, r100(lo));
  const hiLabel = svg("text", { x: x(hi), y: top + body + 20, class: "band-label", "text-anchor": x(hi) > W - 60 ? "end" : "middle" }, r100(hi));
  r.append(loLabel, hiLabel);
  const estAnchor = xe < 90 ? "start" : xe > W - 90 ? "end" : "middle";
  r.append(svg("text", { x: estAnchor === "start" ? xe + 10 : estAnchor === "end" ? xe - 10 : xe, y: top - 20, class: "est-label", "text-anchor": estAnchor === "middle" ? "middle" : estAnchor }, "EST. " + r100(est.price)));
}

const FIELD_NAMES = {
  "Year made": "YEAR MADE", "Hours": "HOURS", "Sale date": "SALE DATE", "Product group": "GROUP",
  "Model": "MODEL", "Size class": "SIZE CLASS", "State": "STATE", "Cab": "CAB", "Options": "OPTIONS",
};
function drawTornado(est) {
  const box = $("tornado");
  box.textContent = "";
  const pushes = [...est.pushes];
  const W = Math.round(box.clientWidth) || 600, narrow = W < 420;
  const labelW = narrow ? 92 : 112, valueW = narrow ? 52 : 60, rowH = 30, axisH = 30;
  const x0 = labelW, x1 = W - valueW, cx = (x0 + x1) / 2, half = (x1 - x0) / 2;
  const maxE = Math.max(...pushes.map((p) => Math.abs(p.effect)), 0.01);
  const D = [0.12, 0.25, 0.45, 0.75, 1.1, 1.6, 2.4, 3.5].find((d) => d >= maxE * 1.04) ?? maxE * 1.05;
  const sx = (e) => cx + (e / D) * half;
  const H = axisH + pushes.length * rowH + 6;
  const s = svg("svg", { viewBox: `0 0 ${W} ${H}`, width: W, height: H });
  // graduated axis (a log scale, so equal pushes up and down look equal)
  groove(s, x0, axisH - 2, x1, axisH - 2, "rule-dark");
  const marks = [-90, -75, -50, -25, -10, 10, 25, 50, 100, 200, 400, 900]
    .map((p) => ({ p, e: Math.log(1 + p / 100) }))
    .filter((m) => Math.abs(m.e) <= D && (D < 0.4 || Math.abs(m.p) !== 10) && (D < 1.0 || Math.abs(m.p) !== 25));
  let lastX = -1e9;
  for (const m of [...marks].sort((a, b) => Math.abs(a.p) - Math.abs(b.p))) m.keep = true;
  for (const m of marks) {
    const xx = Math.round(sx(m.e)) + 0.5;
    groove(s, xx, axisH - 9, xx, axisH - 2);
    if (xx - lastX < 40 || Math.abs(xx - cx) < 22) continue;
    lastX = xx;
    s.append(svg("text", { x: xx, y: axisH - 13, class: "tick-label", "text-anchor": "middle" }, (m.p > 0 ? "+" : "−") + Math.abs(m.p) + "%"));
  }
  // fine graduations between
  for (let i = -20; i <= 20; i++) {
    const e = (i / 20) * D;
    const xx = Math.round(sx(e)) + 0.5;
    groove(s, xx, axisH - 5, xx, axisH - 2);
  }
  pushes.forEach((p, i) => {
    const y = axisH + i * rowH;
    const yc = y + rowH / 2;
    s.append(svg("rect", { x: x0, y: y + 7, width: x1 - x0, height: rowH - 14, rx: 2, class: "bar-recess" }));
    const xe = sx(Math.max(-D, Math.min(D, p.effect)));
    const up = p.effect >= 0;
    const w = Math.max(1.5, Math.abs(xe - cx));
    s.append(svg("rect", { x: up ? cx : cx - w, y: y + 8, width: w, height: rowH - 16, class: up ? "bar-up" : "bar-down" }));
    s.append(svg("text", { x: 0, y: yc + 4.5, class: "t-label" }, FIELD_NAMES[p.field] || p.field.toUpperCase()));
    s.append(svg("text", { x: W, y: yc + 4.5, class: "t-value " + (up ? "up" : "down"), "text-anchor": "end" }, pctText(p.effect)));
    if (i < pushes.length - 1) groove(s, 0, y + rowH, W, y + rowH, "rule-dark");
  });
  s.append(svg("line", { x1: cx, y1: axisH - 2, x2: cx, y2: H - 4, class: "axis" }));
  box.append(s);
  // the same thing as text, for screen readers
  const list = $("pushes-list");
  list.textContent = "";
  pushes.forEach((p) => list.append(el("li", {}, `${p.field}: ${pct(p.effect) >= 0 ? "raises" : "lowers"} the price by ${pctText(p.effect).replace("−", "")}`)));
}

function stampPlate(r) {
  const model = [r.fiBaseModel, r.fiSecondaryDesc].filter(Boolean).join("") || "UNLISTED";
  $("pl-model").textContent = `${model}`;
  const yr = r.YearMade === "1000" ? "" : r.YearMade;
  $("pl-year").textContent = yr || "UNK.";
  $("pl-hours").textContent = r.MachineHoursCurrentMeter ? count.format(Number(r.MachineHoursCurrentMeter)) : "N/R";
  $("pl-class").textContent = shortClass(r.fiProductClassDesc).toUpperCase();
  const ref = (hash(JSON.stringify(r)) % 0xfffff).toString(16).toUpperCase().padStart(5, "0");
  $("pl-serial").textContent = `${r.ProductGroup}-${yr ? yr.slice(2) : "XX"}-${ref}`;
}

let lastPrice = null;
function update() {
  const r = record();
  stampPlate(r);
  if (!ready) return;
  const est = estimate(ready, r);
  const priceEl = $("price");
  // hammer prices are round numbers; show the estimate to the nearest $100
  const text = money.format(Math.round(est.price / 100) * 100);
  if (priceEl.textContent !== text) {
    priceEl.textContent = text;
    if (!reduceMotion && lastPrice !== null) { priceEl.classList.remove("pressed"); void priceEl.offsetWidth; priceEl.classList.add("pressed"); }
    lastPrice = est.price;
  }
  $("ticket").textContent = "EST. " + text;
  const r100 = (v) => money.format(Math.round(Math.max(0, v) / 100) * 100);
  $("range").textContent = `RANGE ${r100(est.low)} \u2013 ${r100(est.high)}`;
  lastEst = est;
  drawRuler(est);
  drawTornado(est);
  $("start-note").innerHTML = "";
  const note = $("start-note");
  note.append("Started from the average training sale, ");
  note.append(el("strong", {}, money.format(est.start)));
  note.append(". Each bar multiplies it, so the start times every change gives the estimate. Effects come from the trees' own splits for this machine, not from a separate explainer.");
}

// ---------- the lot tag ----------
function pull() {
  let ex;
  do { ex = examples[Math.floor(Math.random() * examples.length)]; } while (examples.length > 1 && pulled && ex.salesId === pulled.salesId);
  pulled = ex;
  const x = ex.fields;
  const g = byCode[x.ProductGroup];
  groupBox.querySelector(`#g-${x.ProductGroup}`).checked = true;
  fillGroup(x.ProductGroup, true);
  ensureOption(f.model, x.fiBaseModel);
  fillSeries(g);
  ensureOption(f.series, x.fiSecondaryDesc, x.fiSecondaryDesc || "none / unknown");
  ensureOption(f.cls, x.fiProductClassDesc, shortClass(x.fiProductClassDesc));
  f.year.value = x.YearMade === "1000" ? "" : x.YearMade;
  f.hours.value = Number(x.MachineHoursCurrentMeter) > 0 ? x.MachineHoursCurrentMeter : "";
  ensureOption(f.state, x.state);
  ensureOption(f.cab, x.Enclosure, x.Enclosure || "unknown");
  optBox.querySelectorAll("select").forEach((s) => ensureOption(s, x[s.dataset.column] ?? "", x[s.dataset.column] || "unknown"));
  const [m, d, y] = x.saledate.split(" ")[0].split("/");
  f.month.value = String(Number(m));
  f.saleYear.value = y;
  update();
  // the tag records the estimate for the sale as pulled, with the real sale date
  const est = ready ? estimate(ready, { ...record(), saledate: x.saledate }) : null;
  const body = $("tag-body");
  body.textContent = "";
  body.append(el("p", { class: "lot-no" }, "Lot " + ex.salesId));
  body.append(el("p", { class: "printed" }, `${x.fiModelDesc} · ${GROUP_WORDS[x.ProductGroup] || x.ProductGroup}`));
  body.append(el("hr", { class: "rule" }));
  const dl = el("dl");
  const row = (k, v, cls) => { const w = el("div"); w.append(el("dt", {}, k), el("dd", { class: cls }, v)); dl.append(w); };
  row("Sold", `${MONTHS[Number(m) - 1]} ${Number(d)}, ${y} · ${x.state}`, "typed");
  row("Hammer price (real)", money.format(ex.price), "hand");
  if (est) {
    row("Gavel estimate", money.format(Math.round(est.price / 100) * 100), "typed");
    const off = est.price / ex.price - 1;
    const inside = ex.price >= est.low && ex.price <= est.high;
    row("Off by", `${Math.abs(off * 100).toFixed(0)}% ${off >= 0 ? "high" : "low"} · ${inside ? "inside" : "outside"} the range`, "verdict");
  }
  body.append(dl);
  $("pull").querySelector(".sr-only").textContent = "Pull another 2012 sale";
}
$("pull").addEventListener("click", pull);

let lastEst = null;
// on narrow screens the estimate panel scrolls away under the form: keep a tape strip at the bottom
new IntersectionObserver(([e]) => $("ticket").classList.toggle("show", !e.isIntersecting && e.boundingClientRect.top > 0), { threshold: 0 })
  .observe(document.querySelector(".panel"));
let resizeQueued = false;
window.addEventListener("resize", () => {
  if (resizeQueued || !lastEst) return;
  resizeQueued = true;
  requestAnimationFrame(() => { resizeQueued = false; drawRuler(lastEst); drawTornado(lastEst); });
});

update();
fetch("model.json")
  .then((r) => { if (!r.ok) throw new Error(r.status); return r.json(); })
  .then((m) => { ready = load(m); update(); })
  .catch((e) => { $("price").textContent = "MODEL DID NOT LOAD"; console.error(e); });
