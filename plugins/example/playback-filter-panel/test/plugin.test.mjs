// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { afterEach, test } from "node:test";
import { createMockHost } from "@canx/plugin-api/mock-host";

let host;
afterEach(() => host?.dispose());

async function load() {
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    playback: (action, input) => ({ action, input }),
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
}

test("registers sidebar, setting and command contributions", async () => {
  await load();

  assert.deepEqual([...host.contributions.keys()].sort(), ["clear-filters", "filter-panel", "filter-setting"]);
  assert.equal(host.contributions.get("filter-panel").slot, "playback.sidebar");
  assert.equal(host.contributions.get("filter-setting").slot, "settings.plugins");
});

test("maps declared actions to bounded playback filter operations", async () => {
  await load();

  await host.invokeContribution("filter-panel", { action: "sharpen" });
  await host.invokeContribution("clear-filters", {});

  assert.deepEqual(host.calls.map(call => [call.action, call.input]), [
    ["filter.video.set", { filters: "unsharp=5:5:0.8:3:3:0.4" }],
    ["filter.clear", { type: "video" }],
  ]);
});

test("persists a valid setting and rejects an unknown preset", async () => {
  await load();

  await host.invokeContribution("filter-setting", { key: "preset", value: "denoise" });

  assert.equal(host.storage.get("preset"), "denoise");
  await assert.rejects(
    host.invokeContribution("filter-setting", { key: "preset", value: "arbitrary-mpv-command" }),
    /Unknown filter preset/,
  );
});
