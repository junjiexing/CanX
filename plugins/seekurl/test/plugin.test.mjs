// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import { fileURLToPath } from "node:url";
import { createMockHost } from "@canx/plugin-api/mock-host";

const workspace = fileURLToPath(new URL("..", import.meta.url));
const entryPoint = fileURLToPath(new URL("../dist/main.js", import.meta.url));
const searchUrl = "https://xiuyun.org/index/seek/get";
const resolveUrl = "https://xiuyun.org/index/Seek/getm3u8";
let host;
let calls;
let mediaActions;
afterEach(() => host?.dispose());

const results = [
  { id: 1, title: "测试剧集", year: "2026", resolution: "1920x1080", source: 5, urls: "第一集$token-one#第二集$token-two" },
  { id: 2, title: "测试电影", year: "2025", resolution: "1280x720", source: 3, urls: "高清$file-token" },
];

async function load(overrides = {}) {
  calls = [];
  mediaActions = [];
  host = await createMockHost({
    workspace,
    http: async request => {
      calls.push(request);
      const custom = overrides[request.url];
      if (custom) return typeof custom === "function" ? custom(request) : custom;
      if (request.url === searchUrl) return json(request.url, { code: 1, data: results });
      if (request.url === resolveUrl) {
        const { token } = JSON.parse(request.body);
        return json(request.url, { code: 1, data: { url: token === "file-token"
          ? "https://files.example/movie.mp4?signature=temporary"
          : "https://playlist.example/show/master.m3u8?signature=temporary" } });
      }
      if (request.url.startsWith("https://playlist.example/show/master.m3u8")) {
        return playlist(request.url, '#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",URI="https://audio.example/a/index.m3u8"\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nvideo/index.m3u8');
      }
      if (request.url === "https://playlist.example/show/video/index.m3u8") {
        return playlist(request.url, '#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI="https://keys.example/key.bin"\n#EXTINF:6,\nhttps://cdn.example/seg-1.ts\n#EXTINF:6,\n../seg-2.ts\n#EXT-X-ENDLIST');
      }
      if (request.url === "https://audio.example/a/index.m3u8") {
        return playlist(request.url, "#EXTM3U\n#EXTINF:6,\nhttps://audio-cdn.example/seg-1.aac\n#EXT-X-ENDLIST");
      }
      throw new Error(`Unexpected request: ${request.url}`);
    },
    mediaStream: (action, recipe) => mediaActions.push({ action, recipe }),
  });
  await host.loadPlugin(entryPoint);
}

async function searchPage() {
  await host.invokePage({ type: "change", key: "seek-query", value: "测试剧集 2026" });
  await host.invokePage({ type: "action", action: "search" });
  return find(host.page.content, "seek-results").props.items;
}

async function openFirstResource() {
  const items = await searchPage();
  await host.invokePage({ type: "action", action: "open-resource", itemId: items[0].id });
  return find(host.page.content, "seek-episodes").props.items;
}

test("native page searches and plays the selected episode without exposing tokens", async () => {
  await load();
  assert.ok(host.page);
  const episodes = await openFirstResource();
  assert.deepEqual(episodes.map(episode => episode.title), ["第一集", "第二集"]);
  const search = calls.find(call => call.url === searchUrl);
  assert.equal(search.method, "POST");
  assert.deepEqual(JSON.parse(search.body), { kw: "测试剧集 2026" });
  assert.equal(search.headers["Content-Type"], "application/json");
  assert.equal(search.headers["X-Requested-With"], "XMLHttpRequest");
  assert.equal(JSON.stringify(host.page.content).includes("token-one"), false);

  await host.invokePage({ type: "action", action: "play", itemId: episodes[1].id });

  assert.deepEqual(JSON.parse(calls.find(call => call.url === resolveUrl).body), { token: "token-two" });
  const played = mediaActions.find(action => action.action === "play");
  assert.equal(played.recipe.format, "hls");
  assert.equal(played.recipe.url, "https://playlist.example/show/master.m3u8?signature=temporary");
  assert.equal(played.recipe.displayName, "测试剧集 · 第二集.m3u8");
  assert.equal(played.recipe.headers.Referer, "https://xiuyun.org/");
  assert.match(find(host.page.content, "playback-status").props.text, /已交给 CanX 播放/);
});

test("HLS includes hosts for variants, separate audio, keys and segments without downloading media", async () => {
  await load();
  const found = await host.invokeService("org.canx.seekurl/search", { query: "测试", limit: 2 });
  const resolved = await host.invokeService("org.canx.seekurl/resolve", { id: found.items[0].id });

  assert.match(resolved.sources[0].uri, /^canx:\/\/stream\//);
  const created = mediaActions.find(action => action.action === "create");
  assert.deepEqual([...created.recipe.allowedHosts].sort(), ["playlist.example", "audio.example", "keys.example", "cdn.example", "audio-cdn.example"].sort());
  assert.equal(calls.some(call => /\.ts$|\.aac$|\.bin$/.test(call.url)), false);
  assert.equal(JSON.stringify(found).includes("token-"), false);
  assert.equal(JSON.stringify(resolved).includes("signature=temporary"), false);
});

test("AI search supports pagination and direct file playback", async () => {
  await load();
  assert.deepEqual([...host.services.values()].map(service => service.contract).sort(), ["org.canx.media.resolve", "org.canx.media.search"]);
  const first = await host.invokeService("org.canx.seekurl/search", { query: "测试", limit: 2 });
  assert.equal(first.total, 3);
  assert.equal(first.nextCursor, "2");
  const second = await host.invokeService("org.canx.seekurl/search", { query: "测试", limit: 2, cursor: first.nextCursor });
  assert.equal(second.items.length, 1);
  assert.equal(second.nextCursor, undefined);
  await host.invokeService("org.canx.seekurl/resolve", { id: second.items[0].id });
  assert.equal(mediaActions[0].recipe.format, "auto");
  assert.equal(mediaActions[0].recipe.url, "https://files.example/movie.mp4?signature=temporary");
});

test("empty search is handled without pretending service failures are empty results", async () => {
  await load({ [searchUrl]: json(searchUrl, { code: 0, msg: "没搜索到相关结果" }) });
  assert.deepEqual((await host.invokeService("org.canx.seekurl/search", { query: "missing" })).items, []);
  assert.equal((await searchPage()).length, 0);
  assert.match(JSON.stringify(host.page.content), /没有搜索到相关结果/);
});

test("malformed rows and duplicate episode tokens do not produce unusable entries", async () => {
  await load({ [searchUrl]: json(searchUrl, { code: 1, data: [null, { title: "empty", urls: "bad#name$" },
    { title: "good", urls: "One$opaque-one#Duplicate$opaque-one#Two$opaque-two", source: 2 }, { urls: "One$ignored" }] }) });
  const result = await host.invokeService("org.canx.seekurl/search", { query: "test" });
  assert.equal(result.items.length, 2);
  assert.deepEqual(result.items.map(item => item.title), ["good · One", "good · Two"]);
});

test("network failures are reported in the page", async () => {
  await load({ [searchUrl]: { ...json(searchUrl, {}), status: 503 } });
  await searchPage();
  assert.match(JSON.stringify(host.page.content), /HTTP 503/);
  await assert.rejects(host.invokeService("org.canx.seekurl/search", { query: "test" }), /HTTP 503/);
});

test("invalid JSON is rejected with a useful error", async () => {
  await load({ [searchUrl]: playlist(searchUrl, "<html>Server unavailable</html>") });
  await assert.rejects(host.invokeService("org.canx.seekurl/search", { query: "test" }), /有效 JSON/);
});

test("expired tokens do not launch media and each playback resolves its token again", async () => {
  let resolveCount = 0;
  await load({ [resolveUrl]: () => ++resolveCount === 1
    ? json(resolveUrl, { code: 1, data: { url: "https://files.example/movie.mp4" } })
    : json(resolveUrl, { code: 0, msg: "链接已过期，请重新搜索" }) });
  const episodes = await openFirstResource();
  await host.invokePage({ type: "action", action: "play", itemId: episodes[0].id });
  await host.invokePage({ type: "action", action: "play", itemId: episodes[0].id });
  assert.equal(resolveCount, 2);
  assert.equal(mediaActions.filter(action => action.action === "play").length, 1);
  assert.match(find(host.page.content, "playback-status").props.text, /链接已过期/);
});

test("unknown cached IDs cannot supply arbitrary playback URLs", async () => {
  await load();
  await assert.rejects(host.invokeService("org.canx.seekurl/resolve", { id: "https://attacker.example/video.mp4" }), /重新搜索/);
  assert.equal(calls.length, 0);
});

test("non-HTTP media and credential-bearing URLs are rejected", async () => {
  const badUrls = ["file:///secret.ts", "https://user:secret@media.example/master.m3u8", "https://media.example/clip.mpd"];
  await load({ [resolveUrl]: () => json(resolveUrl, { code: 1, data: { url: badUrls.shift() } }) });
  const result = await host.invokeService("org.canx.seekurl/search", { query: "test" });
  for (let index = 0; index < 3; index++) {
    await assert.rejects(host.invokeService("org.canx.seekurl/resolve", { id: result.items[0].id }), /协议|凭据|DASH/);
  }
  assert.equal(mediaActions.length, 0);
});

test("HLS cannot inject a local-file key reference", async () => {
  await load({ "https://playlist.example/show/master.m3u8?signature=temporary": playlist(
    "https://playlist.example/show/master.m3u8?signature=temporary", '#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI="file:///secret.key"\nsegment.ts') });
  const result = await host.invokeService("org.canx.seekurl/search", { query: "test" });
  await assert.rejects(host.invokeService("org.canx.seekurl/resolve", { id: result.items[0].id }), /不支持的媒体协议/);
  assert.equal(mediaActions.length, 0);
});

test("media HTTP errors are shown before starting playback", async () => {
  await load({ "https://playlist.example/show/master.m3u8?signature=temporary": {
    ...playlist("https://playlist.example/show/master.m3u8?signature=temporary", "Forbidden"), status: 403,
  } });
  const episodes = await openFirstResource();
  await host.invokePage({ type: "action", action: "play", itemId: episodes[0].id });
  assert.match(find(host.page.content, "playback-status").props.text, /HTTP 403/);
  assert.equal(mediaActions.length, 0);
});

test("episode pagination preserves the chosen episode", async () => {
  await load({ [searchUrl]: json(searchUrl, { code: 1, data: [{ title: "long series",
    urls: Array.from({ length: 51 }, (_, index) => `Episode ${index + 1}$token-${index + 1}`).join("#") }] }) });
  assert.equal((await openFirstResource()).length, 50);
  await host.invokePage({ type: "action", action: "episodes-next" });
  const episodes = find(host.page.content, "seek-episodes").props.items;
  assert.equal(episodes.length, 1);
  assert.equal(episodes[0].title, "Episode 51");
  await host.invokePage({ type: "action", action: "play", itemId: episodes[0].id });
  assert.deepEqual(JSON.parse(calls.find(call => call.url === resolveUrl).body), { token: "token-51" });
});

test("managed stream handles stay below the native host limit", async () => {
  await load();
  const result = await host.invokeService("org.canx.seekurl/search", { query: "test" });
  for (let index = 0; index < 12; index++) {
    await host.invokeService("org.canx.seekurl/resolve", { id: result.items[2].id });
  }
  assert.equal(mediaActions.filter(action => action.action === "create").length, 12);
  assert.equal(await host.api.media.stream.release("stream-1"), false);
  assert.equal(await host.api.media.stream.release("stream-2"), false);
  assert.equal(await host.api.media.stream.release("stream-3"), true);
});

function json(url, data) {
  return { status: 200, url, headers: { "content-type": ["application/json"] }, body: JSON.stringify(data), bodyEncoding: "text" };
}

function playlist(url, body) {
  return { status: 200, url, headers: { "content-type": ["application/vnd.apple.mpegurl"] }, body, bodyEncoding: "text" };
}

function find(node, id) {
  if (node?.id === id) return node;
  for (const child of node?.children ?? []) {
    const result = find(child, id);
    if (result) return result;
  }
}
