// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import { fileURLToPath } from "node:url";
import { createMockHost } from "@canx/plugin-api/mock-host";

let host;
afterEach(() => host?.dispose());

test("publishes native UI and AI-visible anime, torrent, and media services", async () => {
  const mediaActions = [];
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: mockHttp,
    html: mockHtml,
    mediaStream: (action, recipe) => mediaActions.push({ action, recipe }),
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));

  assert.ok(host.page);
  assert.equal(host.configuration?.content.type, "stack");
  const services = [...host.services.values()];
  assert.deepEqual(
    services.map(service => service.contract).sort(),
    [
      "org.canx.anime.detail",
      "org.canx.anime.search",
      "org.canx.bittorrent.search",
      "org.canx.media.resolve",
      "org.canx.media.search",
    ],
  );

  const anime = await host.invokeService("org.canx.anime-find/anime-search", { query: "无职转生", limit: 10 });
  assert.equal(anime.items.length, 1, JSON.stringify(anime));
  assert.equal(anime.items[0].title, "无职转生 第二季");
  assert.equal(anime.items[0].score, 8.6);

  const detail = await host.invokeService("org.canx.anime-find/anime-detail", { id: anime.items[0].id });
  assert.equal(detail.groups[0].items[0].downloadable, true);
  assert.equal(JSON.stringify(detail).includes("magnet:?"), false);

  const torrents = await host.invokeService("org.canx.anime-find/torrent-search", { query: "无职转生", limit: 20 });
  assert.equal(torrents.items.length, 1);
  assert.match(torrents.items[0].magnetUri, /^magnet:\?/);

  const media = await host.invokeService("org.canx.anime-find/media-search", { query: "无职转生", limit: 20 });
  assert.equal(media.items.length, 2);
  const resolved = await host.invokeService("org.canx.anime-find/media-resolve", { id: media.items[0].id });
  assert.match(resolved.sources[0].uri, /^canx:\/\/stream\//);
  assert.equal(mediaActions[0].action, "create");
  assert.equal(mediaActions[0].recipe.format, "hls");
  assert.deepEqual(mediaActions[0].recipe.allowedHosts, ["dm1.xfdm.pro", "xfvod.pro", "moedot.net", "pan.wo.cn"]);

  await host.invokeService("org.canx.anime-find/media-resolve", { id: media.items[1].id });
  assert.equal(mediaActions[1].recipe.url, "https://video.xfvod.pro/anime/episode-2.m3u8");
});

test("aggregates AniBT and AnimeGarden through the public anime service", async () => {
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: mockHttp,
    html: mockHtml,
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));

  const result = await host.invokeService("org.canx.anime-find/anime-search", {
    query: "无职转生",
    limit: 10,
    sources: ["anibt", "garden"],
  });

  assert.equal(result.items.length, 1, JSON.stringify(result));
  assert.deepEqual(result.items[0].sources.sort(), ["anibt", "garden"]);
  assert.equal(result.items[0].resourceCount, 5);
});

test("searches the current Mikan season", async () => {
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: mockHttp,
    html: mockHtml,
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));

  const result = await host.invokeService("org.canx.anime-find/anime-search", { query: "本季", limit: 10 });

  assert.equal(result.items.length, 1, JSON.stringify(result));
  assert.match(result.items[0].title, /无职转生/);
});

test("page searches, opens details, confirms downloads, and plays streams from gestures", async () => {
  const prompts = [];
  const mediaActions = [];
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: mockHttp,
    html: mockHtml,
    downloadPrompt: request => prompts.push(request),
    mediaStream: action => mediaActions.push(action),
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));

  await host.invokePage({ type: "change", key: "anime-query", value: "无职转生" });
  await host.invokePage({ type: "action", action: "search" });
  const gallery = findById(host.page.content, "anime-results", JSON.stringify(host.page.content));
  assert.equal(gallery.props.items.length, 1);
  assert.equal(
    gallery.props.items[0].image,
    "https://mikanani.me/images/Bangumi/202307/test.jpg?width=400&height=400&format=webp",
  );
  await host.invokePage({ type: "action", action: "open-anime", itemId: gallery.props.items[0].id });

  const resources = findById(host.page.content, "resource-results");
  assert.equal(resources.props.items.length, 1);
  await host.invokePage({ type: "action", action: "download", itemId: resources.props.items[0].id });
  assert.equal(prompts.length, 1);
  assert.match(prompts[0].source, /^magnet:\?/);

  await host.invokePage({ type: "action", action: "search-streams" });
  const streams = findById(host.page.content, "stream-results");
  assert.equal(streams.props.items.length, 2);
  await host.invokePage({ type: "action", action: "play-stream", itemId: streams.props.items[0].id });
  assert.ok(mediaActions.includes("play"));
});

test("configuration persists source and static-rule choices", async () => {
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: mockHttp,
    html: mockHtml,
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));

  await host.invokeConfiguration({ type: "open" });
  await host.invokeConfiguration({ type: "change", key: "source-anibt", value: true });
  await host.invokeConfiguration({ type: "change", key: "max-results", value: 24 });
  await host.invokeConfiguration({ type: "action", action: "save-settings" });

  const stored = host.storage.get("config.v1");
  assert.deepEqual(stored.sources, ["mikan", "anibt"]);
  assert.equal(stored.maxResults, 24);
});

function mockHttp(request) {
  const url = request.url;
  if (url.includes("/Home/Search")) {
    assert.equal(request.maximumResponseBytes, 512 * 1024);
    assert.equal(request.truncateResponse, true);
    return response(url, "mikan-search", "text/html");
  }
  if (url.includes("/Home/BangumiCoverFlowByDayOfWeek")) return response(url, "mikan-search", "text/html");
  if (url.includes("/Home/Bangumi/3060")) return response(url, "mikan-detail", "text/html");
  if (url.includes("anibt.net/api/seasons/anime")) return response(url, JSON.stringify({
    data: { byWeekday: [{ animes: [{
      animeId: "anibt-3060",
      bgmId: 478425,
      rating: 8.5,
      rssReleaseCount: 4,
      title: { chinese: "无职转生 第二季", primary: "Mushoku Tensei II" },
    }] }] },
  }));
  if (url.includes("api.animes.garden/resources")) return response(url, JSON.stringify({
    resources: [{
      id: 1,
      title: "[测试字幕组] 无职转生 第二季 [01][1080p]",
      magnet: "magnet:?xt=urn:btih:1123456789abcdef0123456789abcdef01234567",
      size: 734003200,
      createdAt: "2026-09-18T00:00:00Z",
      subjectId: 478425,
      fansub: { id: 1, name: "测试字幕组" },
    }],
  }));
  if (url.includes("/v0/search/subjects")) return response(url, JSON.stringify({
    data: [{
      id: 478425,
      name: "Mushoku Tensei II",
      name_cn: "无职转生 第二季",
      rating: { score: 8.6, total: 12345 },
      tags: [{ name: "奇幻" }],
    }],
  }));
  if (url.includes("/v0/subjects/478425")) return response(url, JSON.stringify({
    id: 478425,
    name: "Mushoku Tensei II",
    name_cn: "无职转生 第二季",
    summary: "转生后的异世界冒险。",
    rating: { score: 8.6, total: 12345 },
    tags: [{ name: "奇幻" }],
  }));
  if (url.includes("/p1/subjects/478425/comments")) return response(url, JSON.stringify({
    data: [{ comment: "制作很稳定。", rate: 8, user: { nickname: "Tester" } }],
  }));
  if (url.includes("/search.html")) return response(url, "stream-search", "text/html");
  if (url.endsWith("/voddetail/42.html")) return response(url, "stream-detail", "text/html");
  if (url.includes("/watch/42/1/2")) return response(url,
    `<script>var player_aaaa={"url":"${Buffer.from("https%3A%2F%2Fvideo.xfvod.pro%2Fanime%2Fepisode-2.m3u8").toString("base64")}","encrypt":2}</script>`,
    "text/html");
  if (url.includes("/watch/42/1/1")) return response(url,
    '<script>var player_aaaa={"url":"https:\\/\\/video.xfvod.pro\\/anime\\/episode.m3u8","encrypt":0}</script>',
    "text/html");
  throw new Error(`Unexpected HTTP request: ${url}`);
}

function response(url, body, contentType = "application/json") {
  return { status: 200, url, headers: { "content-type": [contentType] }, body, bodyEncoding: "text" };
}

function mockHtml(request) {
  if (request.html === "mikan-search" && request.selector === "ul.an-ul li") {
    return { rows: [{
      href: "/Home/Bangumi/3060",
      titleAttribute: "无职转生 第二季",
      linkTitle: null,
      titleText: "无职转生 第二季",
      rowText: "无职转生 第二季",
      cover: "/images/Bangumi/202307/test.jpg?width=400&height=400&format=webp",
    }] };
  }
  if (request.html === "mikan-detail" && request.selector === "body") {
    return { rows: [{
      title: "无职转生 第二季",
      poster: "",
      bangumi: "https://bgm.tv/subject/478425",
    }] };
  }
  if (request.html === "mikan-detail" && request.selector === ".leftbar-item") {
    return { rows: [{ label: "喵萌奶茶屋", anchor: "#1231", updateDay: "星期日" }] };
  }
  if (request.html === "mikan-detail" && request.selector.includes("episode-table")) {
    return { rows: [{
      magnet: "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
      clipboard: null,
      title: "[喵萌奶茶屋] 无职转生 第二季 [04][1080p][简中]",
      size: "412.3 MiB",
      createdAt: "2026-09-18",
      torrent: "/Download/episode.torrent",
    }] };
  }
  if (request.html === "mikan-detail" && request.selector === '[id="1231"]') {
    return { rows: [{ href: "/RSS/Bangumi?bangumiId=3060" }] };
  }
  if (request.html === "stream-search" && request.selector === "div.public-list-box.search-box") {
    return { rows: [{ name: "无职转生 第二季", rowText: "无职转生 第二季", href: "/voddetail/42.html", rowHref: null }] };
  }
  if (request.html === "stream-detail" && request.selector === "ul.anthology-list-play li") {
    return { rows: [
      { href: "/watch/42/1/1.html", rowHref: null, name: "第 1 集", rowText: "第 1 集" },
      { href: "/watch/42/1/2.html", rowHref: null, name: "第 2 集", rowText: "第 2 集" },
    ] };
  }
  throw new Error(`Unexpected HTML extraction: ${request.html} / ${request.selector}`);
}

function findById(node, id, context = "") {
  const found = findNode(node, id);
  if (found) return found;
  throw new Error(`UI node '${id}' not found. ${context}`);
}

function findNode(node, id) {
  if (node?.id === id) return node;
  for (const child of node?.children ?? []) {
    const found = findNode(child, id);
    if (found) return found;
  }
  return undefined;
}
