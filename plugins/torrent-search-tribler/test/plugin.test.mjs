// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { createMockHost } from "@canx/plugin-api/mock-host";

test("normalizes Tribler local metadata results", async () => {
  let sent;
  const host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: async request => {
      sent = request;
      return { status: 200, url: request.url, headers: {}, body: JSON.stringify({ total: 1, results: [{
        infohash: "0123456789abcdef0123456789abcdef01234567", name: "Ubuntu ISO", size: 32768,
        category: "software", num_seeders: 11, num_leechers: 1, created: 1700000000,
      }] }), bodyEncoding: "text" };
    },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
  assert.match(host.configuration.content.children.find(item => item.type === "link").props.url, /Tribler/);
  await host.invokeConfiguration({ type: "change", key: "httpProxy", value: "http://127.0.0.1:7890" });
  await host.invokeConfiguration({ type: "change", key: "apiToken", value: "secret" });
  await host.invokeConfiguration({ type: "action", action: "save" });
  const page = await host.invokeService("org.canx.search.tribler/main", {
    query: "ubuntu", limit: 10, options: {},
  });
  assert.equal(sent.headers["x-api-key"], "secret");
  assert.equal(sent.proxy, "http://127.0.0.1:7890");
  assert.match(sent.url, /\/api\/metadata\/search\/local/);
  assert.equal(page.items[0].seeders, 11);
  assert.match(page.items[0].magnetUri, /^magnet:\?xt=urn:btih:/);
  host.dispose();
});
