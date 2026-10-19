// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { createMockHost } from "@canx/plugin-api/mock-host";

test("normalizes Prowlarr torrent releases", async () => {
  let sent;
  const host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: async request => {
      sent = request;
      return { status: 200, url: request.url, headers: {}, body: JSON.stringify([{
        guid: "release-1", title: "Ubuntu ISO", protocol: "torrent", size: 8192,
        seeders: 30, leechers: 5, downloadUrl: "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
        indexer: "Linux Tracker", categories: [{ id: 4000, name: "PC" }],
      }, { guid: "nzb", title: "Not a torrent", protocol: "usenet" }]), bodyEncoding: "text" };
    },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
  assert.match(host.configuration.content.children.find(item => item.type === "link").props.url, /servarr\.com/);
  await host.invokeConfiguration({ type: "change", key: "httpProxy", value: "http://127.0.0.1:7890" });
  await host.invokeConfiguration({ type: "change", key: "apiToken", value: "secret" });
  await host.invokeConfiguration({ type: "action", action: "save" });
  const page = await host.invokeService("org.canx.search.prowlarr/main", {
    query: "ubuntu", limit: 10, options: { indexerIds: [1] },
  });
  assert.equal(sent.headers["x-api-key"], "secret");
  assert.equal(sent.proxy, "http://127.0.0.1:7890");
  assert.match(sent.url, /indexerIds=1/);
  assert.equal(page.items.length, 1);
  assert.equal(page.items[0].source, "Linux Tracker");
  host.dispose();
});
