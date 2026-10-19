// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import { createMockHost } from "../mock-host.mjs";

let host;
afterEach(() => host?.dispose());

test("enforces tool and HTTP scopes", async () => {
  host = await createMockHost({
    manifest: {
      permissions: [
        { capability: "tools.invoke", scopes: ["allowed"] },
        { capability: "http", scopes: ["api.example.org", "proxy.example.org"] },
      ],
    },
    tools: { allowed: input => ({ echoed: input.value }) },
    http: request => ({ status: 200, url: request.url, headers: {}, body: "ok" }),
  });

  assert.deepEqual(await host.api.tools.invoke("allowed", { value: 42 }), { echoed: 42 });
  await assert.rejects(host.api.tools.invoke("blocked"), error => error.code === "permission_denied");
  assert.equal((await host.api.http.request({ url: "https://api.example.org/value" })).body, "ok");
  assert.equal((await host.api.http.request({
    url: "https://api.example.org/value",
    proxy: "http://proxy.example.org:7890",
  })).body, "ok");
  await assert.rejects(
    host.api.http.request({ url: "https://attacker.example/value" }),
    error => error.code === "permission_denied",
  );
  await assert.rejects(
    host.api.http.request({
      url: "https://api.example.org/value",
      proxy: "http://127.0.0.1:7890",
    }),
    error => error.code === "permission_denied",
  );
});

test("a global HTTP scope authorizes arbitrary destination and proxy hosts", async () => {
  host = await createMockHost({
    manifest: { permissions: [{ capability: "http", scopes: ["*"] }] },
    http: request => ({ status: 200, url: request.url, headers: {}, body: "ok" }),
  });

  const response = await host.api.http.request({
    url: "https://unrelated.example/value",
    proxy: "http://192.168.1.10:7890",
  });
  assert.equal(response.body, "ok");
});

test("a default HTTP proxy applies to requests and can be cleared", async () => {
  const requests = [];
  host = await createMockHost({
    manifest: { permissions: [{ capability: "http", scopes: ["*"] }] },
    http: request => {
      requests.push(request);
      return { status: 200, url: request.url, headers: {}, body: "ok" };
    },
  });

  await host.api.http.setDefaultProxy("http://127.0.0.1:7890");
  await host.api.http.request({ url: "https://api.bgm.tv/calendar" });
  assert.equal(requests[0].proxy, "http://127.0.0.1:7890");
  await host.api.http.request({ url: "https://api.bgm.tv/calendar", proxy: "" });
  assert.equal(requests[1].proxy, "");
  await host.api.http.setDefaultProxy(null);
  await host.api.http.request({ url: "https://api.bgm.tv/calendar" });
  assert.equal(requests[2].proxy, undefined);
});

test("delegates bounded HTML extraction to the mock handler", async () => {
  host = await createMockHost({
    html: request => ({ rows: [{ title: request.selector }] }),
  });

  const result = await host.api.html.extract({
    html: "<a>Anime</a>",
    selector: "a",
    fields: { title: { value: "text" } },
  });

  assert.deepEqual(result, { rows: [{ title: "a" }] });
  assert.equal(host.calls.at(-1).area, "html");
});

test("registers, discovers, and invokes generic services", async () => {
  host = await createMockHost({
    manifest: {
      id: "org.canx.services",
      permissions: [
        { capability: "services.provide", scopes: ["org.canx.test.echo"] },
        { capability: "services.invoke", scopes: ["org.canx.test.echo"] },
      ],
    },
    services: {
      "org.canx.external/echo": {
        descriptor: {
          id: "org.canx.external/echo",
          pluginId: "org.canx.external",
          localId: "echo",
          contract: "org.canx.test.echo",
          contractVersion: "1.0",
          title: "External echo",
          risk: "readOnly",
          audiences: ["plugin", "ai"],
          inputSchema: { type: "object" },
          outputSchema: { type: "object" },
        },
        invoke: input => ({ input, hasNestedInvoke: true }),
      },
    },
  });
  const registration = await host.api.services.register({
    id: "echo",
    contract: "org.canx.test.echo",
    contractVersion: "1.0",
    title: "Echo",
    audiences: ["plugin", "ai"],
    inputSchema: { type: "object" },
    outputSchema: { type: "object" },
    invoke: (input, context) => ({ input, hasNestedInvoke: typeof context.invoke === "function" }),
  });

  const services = await host.api.services.list({ contract: "org.canx.test.echo", majorVersion: 1 });
  assert.equal(services.length, 2);
  assert.ok(services.some(service => service.id === registration.id));
  assert.equal((await host.api.services.describe(registration.id)).contractVersion, "1.0");
  assert.deepEqual(await host.api.services.invoke("org.canx.external/echo", { value: 42 }), {
    input: { value: 42 },
    hasNestedInvoke: true,
  });
  assert.deepEqual(await host.invokeService(registration.id, { value: 7 }), {
    input: { value: 7 },
    hasNestedInvoke: true,
  });
});

test("mounts, updates, and invokes a manifest plugin page", async () => {
  host = await createMockHost({
    manifest: {
      ui: { title: "Tools", icon: { builtin: "tools" } },
      permissions: [{ capability: "ui.page" }],
    },
  });
  const events = [];
  const page = await host.api.ui.page.mount({
    content: { type: "text", props: { text: "Loading" } },
    onEvent: event => events.push(event.type),
    onActivate: () => events.push("activate"),
    onDeactivate: () => events.push("deactivate"),
  });

  await page.render({ type: "text", props: { text: "Ready" } });
  await page.render({
    id: "status",
    type: "text",
    props: { text: "Loading", tone: "muted" },
  });
  await page.update([{ target: "status", props: { text: "Ready", tone: null } }]);
  await page.setBadge({ text: "2", tone: "accent" });
  await host.invokePage({ type: "activate" });
  await host.invokePage({ type: "action", action: "refresh" });
  await host.invokePage({ type: "deactivate" });

  assert.deepEqual(host.page.content.props, { text: "Ready" });
  assert.deepEqual(host.page.badge, { text: "2", tone: "accent" });
  assert.deepEqual(events, ["activate", "action", "deactivate"]);
  await page.clearBadge();
  assert.equal(host.page.badge, null);
});

test("allows remote page images only within ui.remoteImages scopes", async () => {
  host = await createMockHost({
    manifest: {
      ui: { title: "Covers", icon: { builtin: "image" } },
      permissions: [
        { capability: "ui.page" },
        { capability: "ui.remoteImages", scopes: ["*.example.org"] },
      ],
    },
  });

  const page = await host.api.ui.page.mount({
    content: {
      id: "covers",
      type: "gallery",
      props: {
        items: [{ id: "one", title: "One", image: "https://img.example.org/one.webp" }],
      },
    },
  });
  assert.equal(host.page.content.props.items[0].image, "https://img.example.org/one.webp");
  await assert.rejects(
    page.render({ type: "image", props: { source: "https://example.org/root.png" } }),
    error => error.code === "permission_denied",
  );
});

test("allows arbitrary public image origins when ui.remoteImages uses star scope", async () => {
  host = await createMockHost({
    manifest: {
      ui: { title: "Covers", icon: { builtin: "image" } },
      permissions: [
        { capability: "ui.page" },
        { capability: "ui.remoteImages", scopes: ["*"] },
      ],
    },
  });

  await host.api.ui.page.mount({
    content: { type: "image", props: { source: "https://unrelated.example/cover.jpg" } },
  });
  assert.equal(host.page.content.props.source, "https://unrelated.example/cover.jpg");
});

test("mounts plugin configuration independently from a navigation page", async () => {
  host = await createMockHost({
    manifest: {
      configuration: { title: "Source settings" },
      permissions: [{ capability: "ui.configuration" }],
    },
  });
  const events = [];
  const configuration = await host.api.ui.configuration.mount({
    content: { id: "endpoint", type: "textInput", props: { label: "Endpoint", key: "endpoint" } },
    onOpen: () => events.push("open"),
    onClose: () => events.push("close"),
    onEvent: event => events.push(event.type),
  });

  await host.invokeConfiguration({ type: "open" });
  await host.invokeConfiguration({ type: "change", key: "endpoint", value: "http://localhost" });
  await configuration.update([{ target: "endpoint", props: { default: "http://localhost" } }]);
  await configuration.close();
  await host.invokeConfiguration({ type: "close" });

  assert.equal(host.configuration.content.props.default, "http://localhost");
  assert.equal(host.configuration.closeRequested, true);
  assert.deepEqual(events, ["open", "change", "close"]);
});

test("rejects declarative UI that the real host schema rejects", async () => {
  host = await createMockHost({
    manifest: {
      ui: { title: "Tools", icon: { builtin: "tools" } },
      permissions: [{ capability: "ui.page" }],
    },
  });

  await assert.rejects(
    host.api.ui.page.mount({
      content: {
        type: "numberInput",
        props: { label: "Limit", key: "limit", default: 10, minimum: 1, maximum: 50 },
      },
    }),
    error => error.code === "invalid_ui_contribution" && /minimum/.test(error.message),
  );

  const page = await host.api.ui.page.mount({
    content: { id: "status", type: "text", props: { text: "Ready" } },
  });
  await assert.rejects(
    page.render({ type: "text", props: { text: "x".repeat(1025) } }),
    error => error.code === "invalid_ui_contribution" && /1024/.test(error.message),
  );
  await assert.rejects(
    page.update([{ target: "status", props: { unsupported: "value" } }]),
    error => error.code === "invalid_ui_contribution" && /unsupported/.test(error.message),
  );
});

test("requires both ui.page permission and a manifest page declaration", async () => {
  host = await createMockHost({ manifest: { permissions: [] } });
  await assert.rejects(
    host.api.ui.page.mount({ content: { type: "text", props: { text: "No" } } }),
    error => error.code === "permission_denied",
  );
});

test("allows download confirmation only during a plugin page user event", async () => {
  host = await createMockHost({
    manifest: {
      ui: { title: "Search", icon: { builtin: "search" } },
      permissions: [{ capability: "ui.page" }, { capability: "downloads.prompt" }],
    },
  });
  const source = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";
  await host.api.ui.page.mount({
    content: { type: "button", props: { label: "Add", action: "add" } },
    onEvent: event => event.type === "action"
      ? host.api.downloads.requestAdd({ kind: "magnet", source })
      : undefined,
  });

  await assert.rejects(
    host.api.downloads.requestAdd({ kind: "magnet", source }),
    error => error.code === "user_gesture_required",
  );
  await host.invokePage({ type: "action", action: "add" });

  assert.deepEqual(host.downloadPrompts, [{ kind: "magnet", source }]);
});

test("creates managed streams and allows playback only from a page gesture", async () => {
  const actions = [];
  host = await createMockHost({
    manifest: {
      ui: { title: "Search", icon: { builtin: "search" } },
      permissions: [
        { capability: "ui.page" },
        { capability: "media.stream" },
        { capability: "http", scopes: ["media.example.org", "cdn.example.org"] },
      ],
    },
    mediaStream: action => actions.push(action),
  });
  const recipe = {
    url: "https://media.example.org/master.m3u8",
    format: "hls",
    allowedHosts: ["cdn.example.org"],
    headers: { Referer: "https://media.example.org/" },
  };
  const created = await host.api.media.stream.create(recipe);
  assert.match(created.source, /^canx:\/\/stream\//);
  assert.equal(await host.api.media.stream.release(created.id), true);
  await host.api.ui.page.mount({
    content: { type: "button", props: { label: "Play", action: "play" } },
    onEvent: event => event.type === "action" ? host.api.media.stream.play(recipe) : undefined,
  });

  await assert.rejects(
    host.api.media.stream.play(recipe),
    error => error.code === "user_gesture_required",
  );
  await host.invokePage({ type: "action", action: "play" });

  assert.deepEqual(actions, ["create", "play"]);
});
