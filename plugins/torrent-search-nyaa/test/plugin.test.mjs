// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { createMockHost } from "@canx/plugin-api/mock-host";

test("normalizes Nyaa RSS items", async () => {
  const rss = `<?xml version="1.0"?><rss xmlns:nyaa="https://nyaa.si/xmlns/nyaa"><channel><item>
    <title><![CDATA[Example &amp; Release]]></title>
    <link>https://nyaa.si/download/123.torrent</link><guid>https://nyaa.si/view/123</guid>
    <pubDate>Sun, 01 Sep 2024 12:00:00 +0000</pubDate><description><![CDATA[<b>Trusted</b> result]]></description>
    <nyaa:seeders>20</nyaa:seeders><nyaa:leechers>4</nyaa:leechers><nyaa:downloads>99</nyaa:downloads>
    <nyaa:infoHash>0123456789abcdef0123456789abcdef01234567</nyaa:infoHash>
    <nyaa:category>Anime - English-translated</nyaa:category><nyaa:size>1.5 GiB</nyaa:size>
  </item></channel></rss>`;
  let sent;
  const host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: async request => {
      sent = request;
      return { status: 200, url: request.url, headers: {}, body: rss, bodyEncoding: "text" };
    },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
  await host.invokeConfiguration({ type: "change", key: "httpProxy", value: "http://127.0.0.1:7890" });
  await host.invokeConfiguration({ type: "action", action: "save" });
  const page = await host.invokeService("org.canx.search.nyaa/main", { query: "example", limit: 20, options: {} });
  assert.equal(sent.proxy, "http://127.0.0.1:7890");
  assert.equal(page.items[0].title, "Example & Release");
  assert.equal(page.items[0].sizeBytes, 1610612736);
  assert.equal(page.items[0].seeders, 20);
  host.dispose();
});
