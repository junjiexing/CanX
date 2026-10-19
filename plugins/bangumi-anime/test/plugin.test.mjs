// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { afterEach, test } from "node:test";
import { createMockHost } from "@canx/plugin-api/mock-host";

let host;

afterEach(() => {
  host?.dispose();
  host = undefined;
});

test("loads calendar, searches subjects, shows details, and persists a local favorite", async () => {
  const requests = [];
  let resourceSearches = 0;
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    services: {
      "org.canx.search.alternate/main": {
        descriptor: {
          id: "org.canx.search.alternate/main",
          pluginId: "org.canx.search.alternate",
          localId: "main",
          contract: "org.canx.bittorrent.search",
          contractVersion: "1.0",
          title: "Alternate Torrent Search",
          risk: "network",
          audiences: ["plugin", "ai"],
          inputSchema: { type: "object" },
          outputSchema: { type: "object" },
        },
        invoke: () => {
          throw new Error("The unselected provider must not be called.");
        },
      },
      "org.canx.search.test/main": {
        descriptor: {
          id: "org.canx.search.test/main",
          pluginId: "org.canx.search.test",
          localId: "main",
          contract: "org.canx.bittorrent.search",
          contractVersion: "1.0",
          title: "Test Torrent Search",
          description: "Test provider",
          risk: "network",
          audiences: ["plugin", "ai"],
          inputSchema: { type: "object" },
          outputSchema: { type: "object" },
        },
        invoke: input => {
          resourceSearches++;
          assert.equal(input.query, "葬送的芙莉莲");
          return {
            items: [{
              id: "torrent-1",
              title: "葬送的芙莉莲 1080p",
              description: "Test release",
              magnetUri: "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
              sizeBytes: 1073741824,
              seeders: 42,
              leechers: 3,
              source: "Test Index",
            }],
          };
        },
      },
    },
    http: request => {
      requests.push(request);
      if (request.url === "https://api.bgm.tv/calendar") {
        return jsonResponse(request.url, calendarFixture());
      }
      if (request.url.startsWith("https://api.bgm.tv/v0/search/subjects")) {
        const body = JSON.parse(request.body);
        assert.equal(body.keyword, "芙莉莲");
        assert.deepEqual(body.filter.type, [2]);
        return jsonResponse(request.url, {
          total: 1,
          data: [subjectFixture(201, "葬送的芙莉莲")],
        });
      }
      if (request.url === "https://api.bgm.tv/v0/subjects/201") {
        return jsonResponse(request.url, subjectFixture(201, "葬送的芙莉莲", true));
      }
      throw new Error(`Unexpected URL ${request.url}`);
    },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));

  assert.equal(host.page.content.type, "viewStack");
  assert.equal(host.page.content.props.active, "home-view");
  assert.equal(findNode(host.page.content, "calendar-gallery").type, "gallery");
  await host.invokePage({ type: "activate" });
  assert.equal(findNode(host.page.content, "calendar-gallery").props.items.length, 1);

  await host.invokePage({ type: "change", key: "weekday", value: "星期二", id: "weekday" });
  assert.equal(findNode(host.page.content, "weekday").props.default, "星期二");
  assert.equal(findNode(host.page.content, "calendar-gallery").props.items[0].id, "102");

  await host.invokePage({ type: "change", key: "query", value: "芙莉莲", id: "query" });
  await host.invokePage({ type: "action", action: "search", id: "search" });
  assert.equal(findNode(host.page.content, "query").props.default, "芙莉莲");
  const searchItem = findNode(host.page.content, "search-gallery").props.items[0];
  assert.equal(searchItem.title, "葬送的芙莉莲");
  assert.equal(searchItem.image, "https://lain.bgm.tv/small/201.png");

  await host.invokePage({ type: "action", action: "open-subject", id: "search-gallery", itemId: "201" });
  assert.equal(host.page.content.props.active, "detail-view");
  assert.equal(findNode(host.page.content, "detail-view").props.active, "detail-content");
  assert.match(textValues(host.page.content).join(" "), /加入收藏/);
  assert.match(textValues(host.page.content).join(" "), /旅途的新篇章/);
  assert.equal(findNode(host.page.content, "resource-query").props.default, "葬送的芙莉莲");
  assert.deepEqual(findNode(host.page.content, "resource-service").props.options, [
    "1. Alternate Torrent Search · org.canx.search.alternate",
    "2. Test Torrent Search · org.canx.search.test",
  ]);
  assert.equal(resourceSearches, 0);

  await host.invokePage({
    type: "change",
    key: "resourceService",
    value: "2. Test Torrent Search · org.canx.search.test",
    id: "resource-service",
  });
  await host.invokePage({ type: "action", action: "search-resources", id: "resource-search" });
  assert.equal(resourceSearches, 1);
  const resourceItem = findNode(host.page.content, "resource-search-results").props.items[0];
  assert.equal(resourceItem.title, "葬送的芙莉莲 1080p");
  assert.match(resourceItem.badge, /1\.0 GiB.*42.*3.*Test Index/);
  await host.invokePage({
    type: "action",
    action: "download-resource",
    id: "resource-search-results",
    itemId: resourceItem.id,
  });
  assert.equal(host.downloadPrompts[0].kind, "magnet");

  await host.invokePage({ type: "action", action: "toggle-favorite" });
  assert.equal(host.storage.get("favorites.v1")[0].id, 201);
  assert.match(textValues(host.page.content).join(" "), /取消收藏/);

  await host.invokePage({ type: "action", action: "back-home" });
  assert.equal(host.page.content.props.active, "home-view");
  assert.equal(findNode(host.page.content, "query").props.default, "芙莉莲");
  assert.equal(findNode(host.page.content, "search-gallery").props.items[0].id, "201");
  assert.equal(findNode(host.page.content, "favorites-gallery").props.items[0].id, "201");
  assert.ok(requests.every(request => request.headers["user-agent"].includes("CanX-Bangumi")));
  assert.ok(requests.every(request => !request.url.startsWith("https://lain.bgm.tv/")));
});

test("saves and applies the local HTTP proxy", async () => {
  const requests = [];
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: request => {
      requests.push(request);
      if (request.url === "https://api.bgm.tv/calendar") return jsonResponse(request.url, calendarFixture());
      throw new Error(`Unexpected URL ${request.url}`);
    },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
  await host.invokePage({ type: "action", action: "open-settings", id: "open-settings" });
  assert.equal(host.page.content.props.active, "settings-view");
  await host.invokePage({
    type: "change",
    key: "httpProxy",
    value: "http://127.0.0.1:7890",
    id: "http-proxy",
  });
  await host.invokePage({ type: "action", action: "save-settings" });

  assert.equal(host.page.content.props.active, "home-view");
  assert.equal(host.storage.get("httpProxy"), "http://127.0.0.1:7890");
  assert.ok(requests.length >= 1);
  assert.ok(requests.every(request => request.proxy === "http://127.0.0.1:7890"));
});

test("accepts a remote HTTP proxy with global network permission", async () => {
  const requests = [];
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: request => {
      requests.push(request);
      return jsonResponse(request.url, calendarFixture());
    },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
  await host.invokePage({ type: "action", action: "open-settings" });
  await host.invokePage({ type: "change", key: "httpProxy", value: "http://proxy.example.com:7890" });
  await host.invokePage({ type: "action", action: "save-settings" });

  assert.equal(host.storage.get("httpProxy"), "http://proxy.example.com:7890");
  assert.ok(requests.length >= 1);
  assert.ok(requests.every(request => request.proxy === "http://proxy.example.com:7890"));
});

test("keeps an invalid HTTP proxy visible after validation", async () => {
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
  await host.invokePage({ type: "action", action: "open-settings", id: "open-settings" });
  await host.invokePage({ type: "change", key: "httpProxy", value: "http://user:password@example.com:7890", id: "http-proxy" });
  await host.invokePage({ type: "action", action: "save-settings" });

  assert.equal(findNode(host.page.content, "http-proxy").props.default, "http://user:password@example.com:7890");
});

function calendarFixture() {
  return Array.from({ length: 7 }, (_, index) => ({
    weekday: { id: index + 1, cn: `星期${"一二三四五六日"[index]}` },
    items: [subjectFixture(101 + index, `日历动画 ${index + 1}`)],
  }));
}

function subjectFixture(id, name, detailed = false) {
  return {
    id,
    type: 2,
    name: `Original ${id}`,
    name_cn: name,
    summary: detailed ? "旅途的新篇章。" : "日历简介。",
    date: "2026-07-01",
    air_date: "2026-07-01",
    air_weekday: 1,
    platform: "TV",
    eps: 12,
    total_episodes: 12,
    images: {
      grid: `http://lain.bgm.tv/grid/${id}.png`,
      small: `https://lain.bgm.tv/small/${id}.png`,
      common: `https://lain.bgm.tv/common/${id}.png`,
    },
    rating: { rank: 42, total: 1000, score: 8.5 },
    collection: { doing: 200, collect: 800 },
    tags: [{ name: "奇幻", count: 100 }],
    infobox: [{ key: "导演", value: "测试导演" }],
  };
}

function jsonResponse(url, value) {
  return {
    status: 200,
    url,
    headers: { "content-type": ["application/json"] },
    body: JSON.stringify(value),
    bodyEncoding: "text",
  };
}

function findNode(root, id) {
  const result = tryFindNode(root, id);
  if (result) return result;
  throw new Error(`Node '${id}' was not found.`);
}

function tryFindNode(root, id) {
  if (root?.id === id) return root;
  for (const child of root?.children ?? []) {
    const result = tryFindNode(child, id);
    if (result) return result;
  }
  return undefined;
}

function textValues(root) {
  const values = [];
  if (typeof root?.props?.text === "string") values.push(root.props.text);
  if (typeof root?.props?.label === "string") values.push(root.props.label);
  for (const child of root?.children ?? []) values.push(...textValues(child));
  return values;
}
