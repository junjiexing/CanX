// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { createMockHost } from "@canx/plugin-api/mock-host";

test("returns Internet Archive torrent and details URLs", async () => {
  let sent;
  const host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: async request => {
      sent = request;
      return {
        status: 200, url: request.url, headers: {}, body: JSON.stringify({ response: { numFound: 1, docs: [{
          identifier: "opensource_movie", title: "Open Movie", description: "Public domain",
          item_size: 4096, downloads: 100, publicdate: "2024-01-01T00:00:00Z", mediatype: "movies",
        }] } }), bodyEncoding: "text",
      };
    },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
  await host.invokeConfiguration({ type: "change", key: "httpProxy", value: "http://127.0.0.1:7890" });
  await host.invokeConfiguration({ type: "action", action: "save" });
  const page = await host.invokeService("org.canx.search.internet-archive/main", { query: "open movie", limit: 10, options: {} });
  assert.equal(sent.proxy, "http://127.0.0.1:7890");
  assert.equal(page.items[0].title, "Open Movie");
  assert.equal(page.items[0].torrentUrl, "https://archive.org/download/opensource_movie/opensource_movie_archive.torrent");
  assert.equal(page.total, 1);
  host.dispose();
});
