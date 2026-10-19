// SPDX-License-Identifier: GPL-3.0-or-later

import "../index.d.ts";

const manifest: CanX.PluginManifestV1 = {
  manifestVersion: 1,
  id: "org.canx.contract",
  name: "Contract",
  version: "1.0.0",
  apiVersion: "2.0",
  entry: "dist/main.mjs",
  ui: { title: "Contract", icon: { builtin: "plugin" } },
  configuration: { title: "Contract settings" },
  permissions: [{ capability: "ui.page" }, { capability: "ui.configuration" }],
};

const slots: readonly CanX.UiSlot[] = [
  "navigation.plugins",
  "home.panel",
  "downloads.details",
  "playback.sidebar",
  "command.palette",
  "menu.app",
  "menu.downloads",
  "menu.playback",
  "settings.plugins",
  "home.status",
  "downloads.status",
  "playback.status",
];

declare const api: CanX.PluginApiV2;
void manifest;
void slots;
void api.playback.invoke("diagnostics.get");
void api.playback.invoke("subtitle.style.set", {});
void api.downloads.invoke("connections");
void api.downloads.invoke("schedule.set", {});
void api.http.request({
  url: "https://api.example.org/value",
  proxy: "http://127.0.0.1:7890",
  responseType: "base64",
});
void api.services.register({
  id: "echo",
  contract: "org.canx.test.echo",
  contractVersion: "1.0",
  title: "Echo",
  audiences: ["plugin", "ai"],
  inputSchema: { type: "object" },
  outputSchema: { type: "object" },
  invoke: async (input, context) => context.invoke("org.canx.other/echo", input),
});
void api.services.list({ contract: "org.canx.test.echo", majorVersion: 1 });
void api.html.extract({
  html: "<a href='/video'>Video</a>",
  selector: "a",
  fields: {
    title: { value: "text" },
    href: { value: "attribute", attribute: "href" },
  },
});
void api.media.stream.create({
  url: "https://media.example/video.m3u8",
  format: "hls",
  allowedHosts: ["media.example", "cdn.example"],
  headers: { Referer: "https://media.example/" },
  headersByHost: { "cdn.example": { Referer: "" } },
});
void api.services.describe("org.canx.services/echo");
void api.services.invoke("org.canx.services/echo", { value: 1 });
void api.ui.page.mount({
  content: {
    type: "stack",
    props: { orientation: "vertical", spacing: "md" },
    children: [
      {
        id: "views",
        type: "viewStack",
        props: { active: "home-view" },
        children: [
          { id: "home-view", type: "text", props: { text: "Home" } },
          { id: "detail-view", type: "text", props: { text: "Details" } },
        ],
      },
      { type: "link", props: { label: "Documentation", url: "https://example.org/docs" } },
      { type: "textInput", props: { label: "Token", key: "token", password: true } },
      { id: "query", type: "searchInput", props: { label: "Query", key: "query" } },
      {
        id: "results",
        type: "dataTable",
        props: {
          columns: [{ key: "title", title: "Title", width: 240 }],
          rows: [{ id: "one", title: "CanX", available: true }],
          action: "open",
        },
      },
    ],
  },
  onEvent: event => {
    if (event.type === "change") void event.value;
    if (event.type === "action") {
      void api.downloads.requestAdd({
        kind: "magnet",
        source: "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
      });
    }
  },
  onActivate: () => undefined,
}).then(async page => {
  await page.render({ type: "text", props: { text: "Ready" } });
  await page.update([{ target: "results", props: { rows: [{ id: "two", title: "Updated" }] } }]);
  await page.setBadge({ text: "2", tone: "accent" });
  await page.clearBadge();
});
void api.ui.configuration.mount({
  content: { type: "text", props: { text: "Settings" } },
  onEvent: async event => event.type,
  onOpen: () => undefined,
  onClose: () => undefined,
}).then(configuration => configuration.close());

// @ts-expect-error Future API versions require a newer SDK and host.
const futureManifest: CanX.PluginManifestV1 = { ...manifest, apiVersion: "2.1" };
// @ts-expect-error Destructive download removal is not in the stable plugin API.
void api.downloads.invoke("remove", {});
// @ts-expect-error Arbitrary UI insertion points are not supported.
const unsafeSlot: CanX.UiSlot = "window.anywhere";
void futureManifest;
void unsafeSlot;
