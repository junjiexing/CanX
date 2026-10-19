// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { createMockHost } from "@canx/plugin-api/mock-host";

test("normalizes apibay results and ignores its no-results sentinel", async () => {
  let sent;
  const host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: async request => {
      sent = request;
      return {
        status: 200, url: request.url, headers: {}, body: JSON.stringify([
          { id: "42", name: "Ubuntu", info_hash: "0123456789abcdef0123456789abcdef01234567", size: "2048", seeders: "8", leechers: "2", added: "1700000000" },
          { id: "0", name: "No results returned", info_hash: "0000000000000000000000000000000000000000" },
        ]), bodyEncoding: "text",
      };
    },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
  await host.invokeConfiguration({ type: "change", key: "httpProxy", value: "http://127.0.0.1:7890" });
  await host.invokeConfiguration({ type: "action", action: "save" });
  const page = await host.invokeService("org.canx.search.apibay/main", { query: "ubuntu", limit: 20, options: {} });
  assert.equal(sent.proxy, "http://127.0.0.1:7890");
  assert.equal(host.storage.get("httpProxy"), "http://127.0.0.1:7890");
  assert.equal(page.items.length, 1);
  assert.equal(page.items[0].sizeBytes, 2048);
  assert.match(page.items[0].magnetUri, /^magnet:\?xt=urn:btih:/);
  host.dispose();
});
