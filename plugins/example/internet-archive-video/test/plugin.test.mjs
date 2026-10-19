// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { afterEach, test } from "node:test";
import { createMockHost } from "@canx/plugin-api/mock-host";

let host;
afterEach(() => host?.dispose());

test("searches metadata and resolves the largest MP4 first", async () => {
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: request => {
      if (request.url.includes("advancedsearch.php")) {
        return {
          status: 200,
          url: request.url,
          headers: { "content-type": ["application/json"] },
          body: JSON.stringify({
            response: {
              numFound: 2,
              docs: [{ identifier: "movie-1", title: "A Public Movie", description: "Public domain" }],
            },
          }),
        };
      }
      return {
        status: 200,
        url: request.url,
        headers: { "content-type": ["application/json"] },
        body: JSON.stringify({
          files: [
            { name: "small.mp4", format: "MPEG4", size: "100" },
            { name: "large file.mp4", format: "h.264", size: "200" },
            { name: "metadata.xml", format: "Metadata" },
          ],
        }),
      };
    },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));

  const page = await host.invokeService("org.canx.examples.internet-archive-video/search", {
    query: "public domain",
    limit: 1,
  });
  const resolution = await host.invokeService("org.canx.examples.internet-archive-video/resolve", {
    id: page.items[0].id,
    data: page.items[0].resolveData,
  });

  assert.equal(page.items[0].title, "A Public Movie");
  assert.equal(page.nextCursor, "2");
  assert.equal(resolution.sources[0].uri, "https://archive.org/download/movie-1/large%20file.mp4");
  assert.equal(resolution.sources[0].mediaType, "video");
});

test("reports items without an MP4 as unresolvable", async () => {
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: request => ({ status: 200, url: request.url, headers: {}, body: "{\"files\":[]}" }),
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));

  await assert.rejects(
    host.invokeService("org.canx.examples.internet-archive-video/resolve", { id: "missing", data: {} }),
    /no playable MP4/,
  );
});
