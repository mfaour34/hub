// node --test app/tests/ui — checks wwwroot/themes.js without a browser.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

function load() {
  const ctx = {
    window: { matchMedia: () => ({ matches: false, addEventListener() {} }), dispatchEvent() {} },
    document: { documentElement: { style: { setProperty() {} } } },
    getComputedStyle: () => ({ getPropertyValue: () => "" }),
    CustomEvent: class {},
  };
  vm.createContext(ctx);
  vm.runInContext(readFileSync(new URL("../../src/Hub.App/wwwroot/themes.js", import.meta.url), "utf8") +
    "\nthis.resolveTheme = resolveTheme; this.contrast = contrast;", ctx);
  return ctx;
}

test("a dark custom accent gets light button text", () => {
  const t = load().resolveTheme({ theme: "custom", customTheme: { accent: "#101020" } });
  assert.ok(load().contrast(t.accent, t.accentText) >= 4.5);
});

test("a black custom background keeps the text readable", () => {
  const { resolveTheme, contrast } = load();
  const t = resolveTheme({ theme: "custom", customTheme: { bg: "#000000", panel: "#111111", side: "#080808" } });
  assert.ok(contrast(t.bg, t.text) >= 4.5);
  assert.ok(contrast(t.inv, t.invText) >= 4.5);
});

test("colours the user set explicitly are kept", () => {
  const t = load().resolveTheme({ theme: "custom", customTheme: { text: "#ff0000", bg: "#ffffff" } });
  assert.equal(t.text, "#ff0000");
});

test("presets resolve unchanged and unknown ones fall back", () => {
  const { resolveTheme } = load();
  assert.equal(resolveTheme({ theme: "fluent" }).accent, "#60cdff");
  assert.equal(resolveTheme({ theme: "neon" }).bg, "#dedbd3");
});
