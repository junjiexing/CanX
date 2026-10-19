// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { createMockHost } from "@canx/plugin-api/mock-host";

test("normalizes a Torznab response", async () => {
  const rss = `<rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel><item>
    <title>Ubuntu ISO</title><guid>jackett-1</guid><link>http://127.0.0.1:9117/dl/one</link>
    <pubDate>Sun, 01 Sep 2024 12:00:00 +0000</pubDate><size>4096</size><category>PC</category>
    <torznab:attr name="infohash" value="0123456789abcdef0123456789abcdef01234567" />
    <torznab:attr name="seeders" value="15" /><torznab:attr name="peers" value="20" />
    <torznab:attr name="jackettindexer" value="Linux Tracker" />
  </item></channel></rss>`;
  let requestedUrl;
  let requestedProxy;
  const host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: async request => {
      requestedUrl = request.url;
      requestedProxy = request.proxy;
      return { status: 200, url: request.url, headers: {}, body: rss, bodyEncoding: "text" };
    },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
  assert.equal(host.configuration.content.children.find(item => item.type === "link").props.url,
    "https://github.com/Jackett/Jackett#web-ui");
  assert.equal(host.configuration.content.children.find(item => item.id === "api-token").props.password, true);
  await host.invokeConfiguration({ type: "change", key: "httpProxy", value: "http://127.0.0.1:7890" });
  await host.invokeConfiguration({ type: "change", key: "apiToken", value: "secret" });
  await host.invokeConfiguration({ type: "action", action: "save" });
  const page = await host.invokeService("org.canx.search.jackett/main", {
    query: "ubuntu", limit: 10, options: {},
  });
  assert.match(requestedUrl, /apikey=secret/);
  assert.equal(requestedProxy, "http://127.0.0.1:7890");
  assert.equal(host.secrets.get("api-key"), "secret");
  assert.equal(page.items[0].seeders, 15);
  assert.equal(page.items[0].leechers, 20);
  assert.equal(page.items[0].torrentUrl, "http://127.0.0.1:9117/dl/one");
  host.dispose();
});
