"use strict";

// ---------------------------------------------------------------- themes
// Token names match Hub.Core.Ui.ThemeTokens.Keys; applyTheme turns them into CSS variables on :root.

const THEMES = {
  "instrument-light": {
    bg: "#dedbd3", side: "#d2cec4", panel: "#ebe8e1", panel2: "#e3dfd7", line: "#cfcbc1", text: "#1b1b1b", muted: "#6f6c64",
    accent: "#ff5a1f", accentText: "#1b1b1b", inv: "#1b1b1b", invText: "#ebe8e1",
    good: "#2f8a57", warn: "#b7791f", bad: "#d23c2a", game: "#ff5a1f", chat: "#1f8a70", media: "#3b6fd1", labelFont: "mono",
  },
  "instrument-dark": {
    bg: "#161513", side: "#1d1c19", panel: "#232220", panel2: "#2a2926", line: "#34322e", text: "#ece9e2", muted: "#8d897f",
    accent: "#ff6a2b", accentText: "#161513", inv: "#ece9e2", invText: "#161513",
    good: "#4cc38a", warn: "#e3a83b", bad: "#ff5d4d", game: "#ff6a2b", chat: "#3fbf9f", media: "#6f9cff", labelFont: "mono",
  },
  fluent: {
    bg: "#1b1d22", side: "#1b1d22", panel: "#24272d", panel2: "#2c2f36", line: "#34373f", text: "#f3f3f3", muted: "#b9b9b9",
    accent: "#60cdff", accentText: "#0b1a24", inv: "#2f333b", invText: "#ffffff",
    good: "#6ccb5f", warn: "#fce100", bad: "#ff99a4", game: "#60cdff", chat: "#6ccb5f", media: "#c19bff", labelFont: "sans",
  },
  monochrome: {
    bg: "#0a0a0a", side: "#0a0a0a", panel: "#121212", panel2: "#1a1a1a", line: "#2a2a2a", text: "#fafafa", muted: "#8a8a8a",
    accent: "#ffffff", accentText: "#0a0a0a", inv: "#fafafa", invText: "#0a0a0a",
    good: "#d9d9d9", warn: "#bdbdbd", bad: "#ff4d4d", game: "#ffffff", chat: "#a3a3a3", media: "#6b6b6b", labelFont: "sans",
  },
  studio: {
    bg: "#17150f", side: "#1d1a14", panel: "#201d16", panel2: "#26221a", line: "#332e25", text: "#ece6da", muted: "#9a917f",
    accent: "#f2c46d", accentText: "#17150f", inv: "#ece6da", invText: "#17150f",
    good: "#9cc47a", warn: "#f2c46d", bad: "#e0735a", game: "#f2c46d", chat: "#9cc47a", media: "#c8a2d8", labelFont: "sans",
  },
};

const THEME_NAMES = {
  "instrument-light": "instrument · light", "instrument-dark": "instrument · dark",
  fluent: "fluent", monochrome: "monochrome", studio: "studio", custom: "my theme",
};

const CSS_VARS = {
  bg: "--bg", side: "--side", panel: "--panel", panel2: "--panel-2", line: "--line", text: "--text", muted: "--muted",
  accent: "--accent", accentText: "--accent-text", inv: "--inv", invText: "--inv-text",
  good: "--good", warn: "--warn", bad: "--bad", game: "--ch-game", chat: "--ch-chat", media: "--ch-media",
};

const darkQuery = window.matchMedia("(prefers-color-scheme: dark)");
let themeSettings = { theme: "instrument-light", customTheme: {}, followWindows: false };

/** Only Instrument has a light and a dark version, so only it can follow Windows. */
const canFollowWindows = (preset) => preset === "instrument-light" || preset === "instrument-dark";

/** The tokens a theme choice resolves to (unknown presets fall back to Instrument light). */
function resolveTheme({ theme, customTheme, followWindows }) {
  if (theme === "custom") return readable({ ...THEMES["instrument-light"], ...(customTheme || {}) }, customTheme || {});
  if (followWindows && canFollowWindows(theme)) return THEMES[darkQuery.matches ? "instrument-dark" : "instrument-light"];
  return THEMES[theme] || THEMES["instrument-light"];
}

/**
 * "My theme" only lets people pick a few colours; the ones derived from them (text on the background, text on the
 * accent, the inverted nav pill) are chosen here so nothing ends up unreadable. Colours set explicitly are kept.
 */
function readable(t, explicit) {
  const ink = (on) => (contrast(on, "#1b1b1b") >= contrast(on, "#f2efe8") ? "#1b1b1b" : "#f2efe8");
  const r = { ...t };
  if (!explicit.text && contrast(r.bg, r.text) < 4.5) r.text = ink(r.bg);
  if (!explicit.muted && contrast(r.bg, r.muted) < 3) r.muted = luminance(r.bg) < 0.4 ? "#9a968c" : "#6f6c64";
  if (!explicit.accentText) r.accentText = ink(r.accent);
  if (!explicit.inv) r.inv = r.text;
  if (!explicit.invText) r.invText = ink(r.inv);
  return r;
}

/** WCAG contrast ratio of two #rrggbb colours (1 … 21). */
function contrast(a, b) {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

/** Relative luminance of #rrggbb, 0 (black) … 1 (white). */
function luminance(hex) {
  const c = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255)
    .map((v) => (v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4)));
  return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
}

/** Sets the theme's CSS variables and tells canvases and drawings to redraw. */
function applyTheme(settings) {
  themeSettings = { ...themeSettings, ...settings };
  const t = resolveTheme(themeSettings), root = document.documentElement.style;
  for (const [key, name] of Object.entries(CSS_VARS)) root.setProperty(name, t[key]);
  root.setProperty("--label-font", t.labelFont === "sans"
    ? `"Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif` : `"Cascadia Mono", "Cascadia Code", Consolas, monospace`);
  root.setProperty("color-scheme", luminance(t.bg) < 0.4 ? "dark" : "light");
  window.dispatchEvent(new CustomEvent("hub:theme", { detail: t }));
  return t;
}

/** Current value of a CSS variable on :root, e.g. token("--accent"). */
const token = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

// Hub injects the saved appearance before the page loads, so the first paint is already in the right theme.
if (window.__hubAppearance) applyTheme(window.__hubAppearance);

darkQuery.addEventListener("change", () => {
  if (themeSettings.followWindows && canFollowWindows(themeSettings.theme)) applyTheme({});
});
