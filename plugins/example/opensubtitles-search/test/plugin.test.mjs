// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { afterEach, test } from "node:test";
import { createMockHost } from "@canx/plugin-api/mock-host";

let host;
afterEach(() => host?.dispose());

test("searches and resolves a subtitle using the scoped secret", async () => {
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    secrets: { "api-key": "test-key" },
    http: request => request.url.endsWith("/download")
      ? {
          status: 200,
          url: request.url,
          headers: {},
          body: JSON.stringify({
            link: "https://dl.opensubtitles.com/download/subtitle.srt",
            file_name: "movie.en.srt",
          }),
        }
      : {
          status: 200,
          url: request.url,
          headers: {},
          body: JSON.stringify({
            total_pages: 2,
            data: [{
              id: "subtitle-1",
              attributes: {
                language: "en",
                release: "Example.Movie.1080p",
                hearing_impaired: false,
                files: [{ file_id: 42, file_name: "example.srt" }],
              },
            }],
          }),
        },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));

  const page = await host.invokeService("org.canx.examples.opensubtitles-search/search", { query: "Example Movie", limit: 10 });
  const resolution = await host.invokeService("org.canx.examples.opensubtitles-search/resolve", {
    id: page.items[0].id,
    data: page.items[0].resolveData,
  });

  assert.equal(page.items[0].language, "en");
  assert.equal(page.nextCursor, "2");
  assert.equal(resolution.sources[0].mediaType, "subtitle");
  assert.equal(resolution.sources[0].mimeType, "application/x-subrip");
  assert.ok(host.calls.every(call => call.area !== "http" || call.input.headers?.["api-key"] === "test-key"));
});

test("fails clearly when the API key is not configured", async () => {
  host = await createMockHost({ workspace: fileURLToPath(new URL("..", import.meta.url)) });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));

  const page = await host.invokeService("org.canx.examples.opensubtitles-search/search", { query: "Example", limit: 10 });
  assert.deepEqual(page.items, []);
  assert.match(page.error, /API Key/);
  assert.equal(host.calls.length, 0);
});

test("stores the API key through plugin settings and reports API failures safely", async () => {
  const workspace = fileURLToPath(new URL("..", import.meta.url));
  host = await createMockHost({
    workspace,
    http: request => ({
      status: 403, url: request.url, headers: {},
      body: JSON.stringify({ message: "private-key" }),
    }),
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));
  await host.invokeConfiguration({ type: "change", key: "api-key", value: " private-key " });
  await host.invokeConfiguration({ type: "action", action: "save" });
  assert.equal(host.secrets.get("api-key"), "private-key");
  const page = await host.invokeService("org.canx.examples.opensubtitles-search/search", { query: "Example", limit: 10 });
  assert.match(page.error, /API Key 无效/);
  assert.doesNotMatch(page.error, /private-key/);
  await host.invokeConfiguration({ type: "action", action: "clear-key" });
  assert.equal(host.secrets.has("api-key"), false);
});
