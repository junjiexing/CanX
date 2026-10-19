# CanX Plugin API 2.0

This package contains the TypeScript declarations, strict manifest validator, mock host and packaging CLI for CanX plugins.

`contract.json` is the machine-readable API 2.0 compatibility surface. Host tests
compare it with every capability, stable playback/download action and declarative
UI slot; TypeScript tests also verify that newer API versions and undeclared
actions fail at compile time.

```sh
npx canx-plugin init my-plugin --id org.example.my-plugin --name "My Plugin"
cd my-plugin
npm install
npm test
npm run pack:canx
```

The generated package is written below `artifacts/`. Package entries use a fixed order and timestamp, so unsigned builds from identical input are byte-for-byte reproducible.

To sign a package, first generate a P-256 key and keep the private key outside the repository:

```sh
canx-plugin keygen ../keys/plugin-signing.pem
canx-plugin pack . --key ../keys/plugin-signing.pem
```

CanX verifies every packaged file before installation. The embedded public key identifies the signer; trust remains a separate host/user policy.

Unit tests can load a compiled plugin without CanX:

```js
import { createMockHost } from "@canx/plugin-api/mock-host";

const host = await createMockHost({ workspace: "." });
try {
  await host.loadPlugin("./dist/main.mjs");
  // Inspect host.services, host.contributions and host.calls.
} finally {
  host.dispose();
}
```

The mock host enforces manifest capabilities and HTTP/tool/secret scopes. It never performs network access unless the test provides an explicit `http` handler.

For HTML pages whose irrelevant tail may be very large, `http.request` accepts a host-bounded
`maximumResponseBytes` and `truncateResponse: true`. The response reports `truncated`; callers must
only use this mode when a valid prefix is sufficient and should still parse it through `canx.html.extract()`.

HTML returned by an approved request can be queried without bundling a DOM implementation:

```js
const { rows } = await canx.html.extract({
  html: response.body,
  selector: ".result",
  fields: {
    title: { selector: "a", value: "text" },
    href: { selector: "a", value: "attribute", attribute: "href" },
    tags: { selector: ".tag", value: "text", all: true }
  }
});
```

The extractor only processes supplied text and grants no network access. Documents, selectors, fields, rows and output are bounded by the host.

## Generic plugin services

Plugins publish JSON-only interfaces through the shared service registry. A stable
contract name and version provide deterministic discovery, while the schemas are
used to validate every call and expose the service to other plugins or AI tools.

```js
await canx.services.register({
  id: "search",
  contract: "org.canx.bittorrent.search",
  contractVersion: "1.0",
  title: "Example search",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: {
    type: "object",
    required: ["query"],
    properties: { query: { type: "string", minLength: 1 } }
  },
  outputSchema: {
    type: "object",
    required: ["items"],
    properties: { items: { type: "array" } }
  },
  async invoke(input, context) {
    return { items: [] };
  }
});

const matches = await canx.services.list({
  contract: "org.canx.bittorrent.search",
  majorVersion: 1
});
const descriptor = await canx.services.describe(matches[0].id);
const result = await canx.services.invoke(matches[0].id, { query: "ubuntu" });
```

Publishing requires `services.provide` scoped to the contract. Discovery and
invocation require `services.invoke`, scoped to a full service id, a contract, or
`*`. Calls are asynchronous, size-limited, schema-validated, cycle-checked and do
not transfer the callee's HTTP or storage permissions to the caller.

For binary HTTP responses such as cover art, set `responseType: "base64"`.
The returned `bodyEncoding` identifies whether `body` contains UTF-8 text or
base64 data; the same HTTP host allowlist and response-size limits still apply.
Set `proxy` to an absolute HTTP proxy URL when a request must use a plugin-configured
proxy. The proxy URL cannot contain credentials or a path, and both the destination
and proxy hosts must be present in the plugin's HTTP allowlist. The `*` HTTP scope
allows any HTTP(S) host after user approval. Direct connections to private or local
addresses remain blocked; with `http: ["*"]`, an explicitly configured HTTP
proxy may run there. Call `await canx.http.setDefaultProxy(proxyUrl)` to use the same
proxy for HTTP requests and declarative remote images. Pass `null` to restore direct
connections; an individual request may override the default with its `proxy` field.

Declarative page images should normally use a direct HTTP(S) URL plus the separate
`ui.remoteImages` capability. Its scopes accept an exact host, `*.example.org`, or
`*`. The last form means any public HTTP(S) origin after the user approves the
permission; private/local addresses remain blocked. CanX fetches, validates, and
caches PNG/JPEG/WebP images without granting the plugin general `http` access:

```json
{ "capability": "ui.remoteImages", "scopes": ["*.example.org"] }
```

```js
{ type: "image", props: { source: "https://img.example.org/cover.webp" } }
```

## Headless BitTorrent search services

A plugin can expose a searchable torrent catalog without declaring a UI page. All such
services use the `org.canx.bittorrent.search` contract at major version 1; API-specific
options remain discoverable inside each service's input schema:

```ts
await canx.services.register({
  id: "main",
  contract: "org.canx.bittorrent.search",
  contractVersion: "1.0",
  title: "Example index",
  description: "Searches an example BitTorrent index.",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: {
    type: "object",
    required: ["query"],
    properties: {
      query: { type: "string", minLength: 1 },
      limit: { type: "integer", minimum: 1, maximum: 100 },
      options: {
        type: "object",
        properties: { endpoint: { type: "string" } }
      }
    }
  },
  outputSchema: {
    type: "object",
    required: ["items"],
    properties: { items: { type: "array" } }
  },
  async invoke(request) {
    return {
      items: [{
        id: "result-1",
        title: request.query,
        infoHash: "0123456789abcdef0123456789abcdef01234567",
        magnetUri: "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
      }],
    };
  },
});
```

Consumers request `services.invoke` scoped to the contract, a full service ID, or `*`, then
discover and invoke every compatible implementation through the same JSON contract:

```ts
const services = await canx.services.list({
  contract: "org.canx.bittorrent.search",
  majorVersion: 1
});
const page = await canx.services.invoke(services[0].id, {
  query: "ubuntu",
  limit: 20,
  options: { category: "software" },
});
```

Network permissions are kept separate from consumer permissions. A consumer receives
schema-validated search results without inheriting the service plugin's HTTP access. A UI plugin
must still use `canx.downloads.requestAdd()` from a direct user gesture to open CanX's standard
download confirmation dialog.

## Plugin configuration dialog

A plugin can expose configuration without adding a navigation page. Declare a
`configuration` object and the `ui.configuration` capability, then mount a
declarative configuration surface:

```js
const configuration = await canx.ui.configuration.mount({
  content: {
    id: "endpoint",
    type: "textInput",
    props: { label: "Endpoint", key: "endpoint" }
  },
  async onOpen() {
    const endpoint = await canx.storage.get("endpoint");
    await configuration.update([{ target: "endpoint", props: { default: endpoint ?? "" } }]);
  },
  async onEvent(event) {
    if (event.type === "change" && event.key === "endpoint") {
      await canx.storage.set("endpoint", event.value);
    }
  }
});
```

CanX displays a configuration button beside enabled plugins and owns the modal
overlay, focus, close action and size. Plugins continue to store ordinary values
through `canx.storage` and credentials through `canx.secrets`.

## Manifest plugin pages

API 2.0 plugins may declare one top-level page with a built-in icon or a packaged
PNG/JPEG/WebP asset. Declare both a manifest `ui` object and the `ui.page`
capability, then mount a declarative component tree:

```js
const page = await canx.ui.page.mount({
  content: {
    type: "stack",
    props: { spacing: "md" },
    children: [
      { id: "status", type: "text", props: { text: "Ready" } },
      { type: "button", props: { label: "Refresh", action: "refresh" } }
    ]
  },
  onEvent: event => {
    if (event.type === "action" && event.action === "refresh") {
      return page.update([{ target: "status", props: { text: "Updated" } }]);
    }
  }
});
```

`page.render()` replaces the tree, `page.update()` atomically changes properties
by component id, and `setBadge()`/`clearBadge()` update the navigation badge.
The stable component list is published in `contract.json` and typed in
`index.d.ts`.

Plugins that want to offer a Magnet or HTTP resource from a page should request
`downloads.prompt` and call `canx.downloads.requestAdd()` directly from an
`action` or `change` handler. The host always opens its standard confirmation UI;
background or lifecycle calls are rejected.

## Managed media streams

Plugins that resolve protected media can declare `media.stream` together with
the `http` scopes for every origin involved, then return an opaque stream to
CanX without exposing request headers to libmpv:

```js
const stream = await canx.media.stream.create({
  url: "https://media.example.org/master.m3u8",
  format: "hls",
  allowedHosts: ["cdn.example.org"],
  headers: { Referer: "https://media.example.org/" }
});
```

The host validates every redirect and HLS child resource, applies only a small
header allowlist, and returns opaque `canx://` handles. Ordinary files use mpv stream
callbacks. HLS uses a host-managed, token-protected HTTP gateway bound only to
`127.0.0.1`, so FFmpeg can demux and seek the complete episode. All HLS sessions
share one ephemeral port, closed when the last HLS registration is released.
Call `release(stream.id)` when a handle is no longer needed. `play(recipe)` opens
the standard player and is accepted only directly inside a plugin-page event.
