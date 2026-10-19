// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import { fileURLToPath } from "node:url";
import { createMockHost } from "@canx/plugin-api/mock-host";

const workspace = fileURLToPath(new URL("..", import.meta.url));
const entry = fileURLToPath(new URL("../dist/main.js", import.meta.url));
const searchService = "org.canx.subtitle.assrt/search";
const resolveService = "org.canx.subtitle.assrt/resolve";
let host;

afterEach(() => host?.dispose());

function response(request, payload, status = 200) {
  return { status, url: request.url, headers: {}, body: JSON.stringify(payload), bodyEncoding: "text" };
}

test("searches with a Bearer token and resolves fresh links inside a subtitle archive", async () => {
  let detailCalls = 0;
  host = await createMockHost({
    workspace,
    secrets: { token: "secret-token" },
    http: request => {
      const url = new URL(request.url);
      assert.equal(url.hostname, "api.assrt.net");
      assert.equal(request.headers.Authorization, "Bearer secret-token");
      assert.equal(url.searchParams.has("token"), false);
      if (url.pathname.endsWith("/search")) {
        assert.equal(url.searchParams.get("q"), "Example Movie");
        assert.equal(url.searchParams.get("pos"), "0");
        return response(request, { status: 0, sub: { subs: [{
          id: 602333,
          native_name: "示例电影",
          videoname: "Example.Movie.1080p",
          subtype: "Subrip(srt)",
          lang: { langlist: { langchs: true, langeng: true }, desc: "简英双语" },
        }] } });
      }
      assert.equal(url.searchParams.get("id"), "602333");
      detailCalls += 1;
      return response(request, { status: 0, sub: { subs: [{
        id: 602333,
        filename: "Example.Movie.zip",
        url: "http://file0.assrt.net/download/602333/archive.zip",
        filelist: [
          { f: "notes.txt", url: "http://file0.assrt.net/onthefly/602333/notes.txt" },
          { f: "Example.Movie.zh.srt", url: `http://file0.assrt.net/onthefly/602333/movie.srt?signature=${detailCalls}` },
          { f: "Example.Movie.en.ass", url: "https://file1.assrt.net/onthefly/602333/movie.ass" },
        ],
      }] } });
    },
  });
  await host.loadPlugin(entry);

  const page = await host.invokeService(searchService, { query: "Example Movie", limit: 10 });
  assert.equal(page.items.length, 1);
  assert.equal(page.items[0].id, "602333");
  assert.equal(page.items[0].language, "zh");
  assert.equal(page.items[0].format, "srt");
  assert.match(page.items[0].title, /Example\.Movie/);

  const first = await host.invokeService(resolveService, { id: page.items[0].id });
  const second = await host.invokeService(resolveService, { id: page.items[0].id });
  assert.equal(first.sources.length, 2);
  assert.equal(first.sources[0].mediaType, "subtitle");
  assert.equal(first.sources[0].mimeType, "application/x-subrip");
  assert.equal(first.sources[1].mimeType, "text/x-ssa");
  assert.match(first.sources[0].uri, /signature=1$/);
  assert.match(second.sources[0].uri, /signature=2$/);
  assert.equal(detailCalls, 2);
});

test("uses ASSRT's 15-result page size and advances the offset cursor", async () => {
  host = await createMockHost({
    workspace,
    secrets: { token: "secret-token" },
    http: request => {
      const url = new URL(request.url);
      assert.equal(url.searchParams.get("cnt"), "15");
      const pos = Number(url.searchParams.get("pos"));
      const count = pos === 0 ? 15 : 1;
      return response(request, { status: 0, sub: { subs: Array.from({ length: count }, (_, index) => ({
        id: 600000 + pos + index,
        native_name: `Movie ${pos + index}`,
      })) } });
    },
  });
  await host.loadPlugin(entry);

  const first = await host.invokeService(searchService, { query: "Movie", limit: 50 });
  const second = await host.invokeService(searchService, { query: "Movie", limit: 50, cursor: first.nextCursor });
  assert.equal(first.items.length, 15);
  assert.equal(first.nextCursor, "15");
  assert.equal(second.items[0].id, "600015");
  assert.equal(second.nextCursor, undefined);
});

test("resolves a standalone subtitle and handles an empty search page", async () => {
  host = await createMockHost({
    workspace,
    secrets: { token: "secret-token" },
    http: request => request.url.includes("/detail?")
      ? response(request, { status: 0, sub: { subs: [{
          id: 700001,
          filename: "Movie.zh.ass",
          url: "http://file2.assrt.net/download/700001/Movie.zh.ass?api=1",
        }] } })
      : response(request, { status: 0, sub: { subs: [] } }),
  });
  await host.loadPlugin(entry);

  const page = await host.invokeService(searchService, { query: "Unknown Movie", limit: 10 });
  const resolved = await host.invokeService(resolveService, { id: "700001" });
  assert.deepEqual(page, { items: [] });
  assert.equal(resolved.sources.length, 1);
  assert.equal(resolved.sources[0].uri, "http://file2.assrt.net/download/700001/Movie.zh.ass?api=1");
  assert.equal(resolved.sources[0].mimeType, "text/x-ssa");
});

test("treats non-array empty search responses as no results and reads indexed rows", async () => {
  host = await createMockHost({
    workspace,
    secrets: { token: "secret-token" },
    http: request => {
      const url = new URL(request.url);
      if (url.pathname.endsWith("/detail"))
        return response(request, { status: 0, sub: { subs: "unavailable" } });
      const query = url.searchParams.get("q");
      const subs = query === "No Results" ? "" : query === "Empty Object" ? {} : {
        1: { id: 700002, native_name: "Second" },
        0: { id: 700001, native_name: "First" },
      };
      return response(request, { status: 0, sub: { subs } });
    },
  });
  await host.loadPlugin(entry);

  assert.deepEqual((await host.invokeService(searchService, { query: "No Results", limit: 15 })).items, []);
  assert.deepEqual((await host.invokeService(searchService, { query: "Empty Object", limit: 15 })).items, []);
  const indexed = await host.invokeService(searchService, { query: "Indexed", limit: 15 });
  assert.deepEqual(indexed.items.map(item => item.id), ["700001", "700002"]);
  const detail = await host.invokeService(resolveService, { id: "700001" });
  assert.match(detail.error, /字幕列表格式无效/);
});

test("stores and clears the token through plugin settings", async () => {
  host = await createMockHost({ workspace });
  await host.loadPlugin(entry);
  assert.match(host.configuration.content.children[0].props.placeholder, /粘贴/);
  await host.invokeConfiguration({ type: "change", key: "token", value: " new-token " });
  await host.invokeConfiguration({ type: "action", action: "save" });
  assert.equal(host.secrets.get("token"), "new-token");
  assert.equal(host.configuration.closeRequested, true);
  await host.invokeConfiguration({ type: "action", action: "clear-token" });
  assert.equal(host.secrets.has("token"), false);
  const page = await host.invokeService(searchService, { query: "Example", limit: 10 });
  assert.match(page.error, /API Token/);
  assert.equal(host.calls.filter(call => call.area === "http").length, 0);
});

test("reports invalid tokens and quota errors without exposing the token", async () => {
  for (const [status, expected] of [[20001, /Token 无效/], [30900, /配额上限/]]) {
    host = await createMockHost({
      workspace,
      secrets: { token: "private-token" },
      http: request => response(request, { status, errmsg: "server error" }, 400),
    });
    await host.loadPlugin(entry);
    const page = await host.invokeService(searchService, { query: "Example", limit: 10 });
    assert.match(page.error, expected);
    assert.doesNotMatch(page.error, /private-token/);
    host.dispose();
  }
});

test("rejects unsafe URLs and archive-only details", async () => {
  host = await createMockHost({
    workspace,
    secrets: { token: "secret-token" },
    http: request => response(request, { status: 0, sub: { subs: [{
      id: 602333,
      filename: "movie.zip",
      url: "http://file0.assrt.net/download/movie.zip",
      filelist: [
        { f: "movie.srt", url: "https://evil.example/movie.srt" },
        { f: "movie.ass", url: "javascript:alert(1)" },
        { f: "movie.vtt", url: "https://file0.assrt.net.evil.example/movie.vtt" },
      ],
    }] } }),
  });
  await host.loadPlugin(entry);
  const resolution = await host.invokeService(resolveService, { id: "602333" });
  assert.deepEqual(resolution.sources, []);
  assert.match(resolution.error, /没有可直接播放的字幕文件/);
});

test("does not call the API for a search term shorter than three characters", async () => {
  host = await createMockHost({ workspace, secrets: { token: "secret-token" } });
  await host.loadPlugin(entry);
  const page = await host.invokeService(searchService, { query: "ab", limit: 10 });
  assert.deepEqual(page, { items: [] });
  assert.equal(host.calls.length, 0);
});
