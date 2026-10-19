// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { createMockHost } from "@canx/plugin-api/mock-host";

test("registers the welcome status card", async () => {
  const host = await createMockHost({ workspace: "." });
  try {
    await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
    assert.equal(host.contributions.get("welcome")?.slot, "home.status");
  } finally {
    host.dispose();
  }
});
