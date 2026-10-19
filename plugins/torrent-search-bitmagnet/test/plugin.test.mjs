// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { createMockHost } from "@canx/plugin-api/mock-host";

test("queries and normalizes bitmagnet GraphQL data", async () => {
  let sent;
  let sentRequest;
  const host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: async request => {
      sentRequest = request;
      sent = JSON.parse(request.body);
      return { status: 200, url: request.url, headers: {}, body: JSON.stringify({ data: { torrentContent: { search: {
        totalCount: 1, hasNextPage: false, items: [{ id: "one", title: "Ubuntu ISO",
          infoHash: "0123456789abcdef0123456789abcdef01234567", contentType: "software",
          seeders: 9, leechers: 2, torrent: { size: 16384,
            magnetUri: "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
            sources: [{ name: "DHT" }] } }],
      } } } }), bodyEncoding: "text" };
    },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
  await host.invokeConfiguration({ type: "change", key: "httpProxy", value: "http://127.0.0.1:7890" });
  await host.invokeConfiguration({ type: "action", action: "save" });
  const page = await host.invokeService("org.canx.search.bitmagnet/main", { query: "ubuntu", limit: 10, options: {} });
  assert.equal(sentRequest.proxy, "http://127.0.0.1:7890");
  assert.equal(sent.variables.input.queryString, "ubuntu");
  assert.equal(page.items[0].sizeBytes, 16384);
  assert.equal(page.items[0].source, "DHT");
  host.dispose();
});
