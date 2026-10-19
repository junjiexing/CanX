// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { afterEach, test } from "node:test";
import { createMockHost } from "@canx/plugin-api/mock-host";

let host;
let server;
let savedUrlSearchParams;

afterEach(() => {
  host?.dispose();
  host = undefined;
  delete globalThis.CANX_MAGNET_SEARCH_API;
  if (savedUrlSearchParams) globalThis.URLSearchParams = savedUrlSearchParams;
  savedUrlSearchParams = undefined;
  server?.kill();
  server = undefined;
});

test("searches the Python API and resolves a Magnet link", async () => {
  const started = await startServer();
  server = started.process;
  globalThis.CANX_MAGNET_SEARCH_API = started.url;
  savedUrlSearchParams = globalThis.URLSearchParams;
  globalThis.URLSearchParams = undefined;
  const httpRequests = [];
  host = await createMockHost({
    workspace: fileURLToPath(new URL("..", import.meta.url)),
    http: async request => {
      httpRequests.push(request);
      const response = await fetch(request.url, {
        method: request.method ?? "GET",
        headers: request.headers,
        body: request.body,
      });
      const body = request.responseType === "base64"
        ? Buffer.from(await response.arrayBuffer()).toString("base64")
        : await response.text();
      return {
        status: response.status,
        url: response.url,
        headers: Object.fromEntries(
          [...response.headers.entries()].map(([name, value]) => [name, [value]]),
        ),
        body,
        bodyEncoding: request.responseType ?? "text",
      };
    },
  });
  await host.loadPlugin(fileURLToPath(new URL("../dist/main.js", import.meta.url)));

  assert.equal(host.page.content.children[0].children[2].props.action, "options");
  await host.invokePage({ type: "action", action: "options", id: "options" });
  assert.equal(host.page.content.children[0].props.text, "Search options");
  await host.invokePage({
    type: "change",
    key: "httpProxy",
    value: "http://127.0.0.1:7890",
    id: "http-proxy",
  });
  await host.invokePage({ type: "action", action: "save-options", id: "save-options" });
  assert.equal(host.storage.get("httpProxy"), "http://127.0.0.1:7890");

  await host.invokePage({ type: "change", key: "query", value: "linux", id: "query" });
  await host.invokePage({ type: "action", action: "search", id: "search" });
  assert.equal(host.page.content.children[2].props.items.length, 8);
  assert.equal(host.page.content.children[2].props.actionLabel, "Download");
  const selected = host.page.content.children[2].props.items[0];
  assert.equal(selected.title, "linux mock resource 1");
  assert.equal(selected.description, "Local mock result returned over the plugin HTTP API.");
  assert.match(selected.image, /^data:image\/png;base64,/);
  assert.equal(host.page.content.children[2].props.items[3].image, undefined);
  assert.ok(httpRequests.length > 1);
  assert.ok(httpRequests.every(request => request.proxy === "http://127.0.0.1:7890"));
  await host.invokePage({ type: "action", action: "download", id: "results", itemId: selected.id });
  assert.equal(host.downloadPrompts.length, 1);
  assert.equal(host.downloadPrompts[0].kind, "magnet");
  assert.match(host.downloadPrompts[0].source, /^magnet:\?xt=urn:btih:/);
  assert.ok(httpRequests.every(request => request.proxy === "http://127.0.0.1:7890"));
});

function startServer() {
  const python = process.env.PYTHON ?? (process.platform === "win32" ? "python" : "python3");
  const script = fileURLToPath(new URL("../mock_server.py", import.meta.url));
  const child = spawn(python, [script, "--port", "0"], {
    stdio: ["ignore", "pipe", "pipe"],
  });
  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  return new Promise((resolve, reject) => {
    let output = "";
    let errors = "";
    const timeout = setTimeout(() => {
      child.kill();
      reject(new Error(`Python mock server did not start. ${errors}`));
    }, 10_000);
    child.stderr.on("data", chunk => { errors += chunk; });
    child.on("error", error => {
      clearTimeout(timeout);
      reject(error);
    });
    child.on("exit", code => {
      clearTimeout(timeout);
      reject(new Error(`Python mock server exited with code ${code}. ${errors}`));
    });
    child.stdout.on("data", chunk => {
      output += chunk;
      const match = output.match(/Listening on (http:\/\/127\.0\.0\.1:\d+)/);
      if (match) {
        clearTimeout(timeout);
        resolve({ process: child, url: match[1] });
      }
    });
  });
}
