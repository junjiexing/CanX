// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { createMockHost } from "@canx/plugin-api/mock-host";

test("normalizes Knaben results", async () => {
  let sent;
  const host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: async request => {
      sent = request;
      return response({ hits: [{
        id: "one", title: "Ubuntu", hash: "0123456789abcdef0123456789abcdef01234567",
        magnetUrl: "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
        bytes: 1024, seeders: 12, peers: 3, category: "PC / Unix", tracker: "Example",
      }], total: { value: 1 } });
    },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
  await host.invokeConfiguration({ type: "change", key: "httpProxy", value: "http://127.0.0.1:7890" });
  await host.invokeConfiguration({ type: "action", action: "save" });
  const page = await host.invokeService("org.canx.search.knaben/main", { query: "ubuntu", limit: 10, options: {} });
  assert.equal(sent.proxy, "http://127.0.0.1:7890");
  assert.equal(page.items[0].infoHash, "0123456789ABCDEF0123456789ABCDEF01234567");
  assert.equal(page.items[0].seeders, 12);
  assert.equal(JSON.parse(sent.body).query, "ubuntu");
  host.dispose();
});

function response(value) { return { status: 200, url: "https://api.knaben.org/v1", headers: {}, body: JSON.stringify(value), bodyEncoding: "text" }; }
