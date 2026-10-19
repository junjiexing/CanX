// SPDX-License-Identifier: GPL-3.0-or-later

export {};

interface MockSearchResponse {
  readonly items?: readonly {
    readonly id?: string;
    readonly title?: string;
    readonly description?: string;
    readonly coverUrl?: string | null;
    readonly sizeBytes?: number;
    readonly seeders?: number;
    readonly leechers?: number;
  }[];
  readonly nextCursor?: string;
}

interface MockResolveResponse {
  readonly kind?: "magnet" | "http";
  readonly source?: string;
}

interface MockSearchItem {
  readonly id: string;
  readonly title: string;
  readonly description?: string;
  readonly sizeBytes?: number;
  readonly seeders?: number;
  readonly leechers?: number;
  readonly coverUrl?: string;
}

interface DownloadResolution {
  readonly kind: "magnet" | "http";
  readonly source: string;
}

interface MockSearchPage {
  readonly items: readonly MockSearchItem[];
  readonly nextCursor?: string;
}

type ListItem = Readonly<Record<string, string | number | boolean | null>>;
type StatusTone = "muted" | "accent" | "success" | "warning" | "error";

const proxyStorageKey = "httpProxy";
const testBaseUrl = (globalThis as unknown as { CANX_MAGNET_SEARCH_API?: unknown })
  .CANX_MAGNET_SEARCH_API;
const baseUrl = typeof testBaseUrl === "string"
  ? testBaseUrl.replace(/\/$/, "")
  : "http://127.0.0.1:8765";

const storedProxy = await canx.storage.get<unknown>(proxyStorageKey);
const normalizedStoredProxy = typeof storedProxy === "string" ? normalizeHttpProxy(storedProxy) : "";
let httpProxy = normalizedStoredProxy ?? "";
let draftHttpProxy = httpProxy;
let pageQuery = "";
let resultItems: readonly ListItem[] = [];
let statusText = "Enter a keyword to search the local mock API.";
let statusTone: StatusTone = "muted";

const page = await canx.ui.page.mount({
  content: createSearchContent(),
  async onEvent(event) {
    if (event.type === "change" && event.key === "query") {
      pageQuery = String(event.value ?? "").trim();
      return;
    }
    if (event.type === "change" && event.key === "httpProxy") {
      draftHttpProxy = String(event.value ?? "").trim();
      return;
    }
    if (event.type !== "action") return;

    if (event.action === "options") {
      draftHttpProxy = httpProxy;
      await page.render(createSettingsContent());
      return;
    }
    if (event.action === "cancel-options") {
      draftHttpProxy = httpProxy;
      await page.render(createSearchContent());
      return;
    }
    if (event.action === "save-options") {
      const normalized = normalizeHttpProxy(draftHttpProxy);
      if (normalized == null) {
        await page.update([{
          target: "settings-status",
          props: {
            text: "Enter an HTTP proxy such as http://127.0.0.1:7890, or leave it empty.",
            tone: "error",
          },
        }]);
        return;
      }

      httpProxy = normalized;
      if (httpProxy) await canx.storage.set(proxyStorageKey, httpProxy);
      else await canx.storage.remove(proxyStorageKey);
      statusText = httpProxy ? `HTTP proxy enabled: ${httpProxy}` : "HTTP proxy disabled.";
      statusTone = "success";
      await page.render(createSearchContent());
      return;
    }
    if (event.action === "search") {
      await searchFromPage();
      return;
    }
    if (event.action === "download" && typeof event.itemId === "string") {
      const download = await resolveResource(event.itemId);
      await canx.downloads.requestAdd(download);
    }
  },
});

function createSearchContent(): CanX.UiComponent {
  return {
    type: "stack",
    props: { spacing: "md" },
    children: [
      {
        type: "grid",
        props: { columns: 4, spacing: "sm" },
        children: [
          {
            id: "query",
            type: "searchInput",
            props: {
              label: "Keyword",
              key: "query",
              placeholder: "Enter a resource keyword",
              default: pageQuery,
            },
            layout: { column: 0, columnSpan: 2 },
          },
          {
            id: "search",
            type: "button",
            props: { label: "Search", action: "search", variant: "primary" },
            layout: { column: 2, verticalAlignment: "bottom" },
          },
          {
            id: "options",
            type: "button",
            props: { label: "Options", action: "options", variant: "secondary" },
            layout: { column: 3, verticalAlignment: "bottom" },
          },
        ],
      },
      { id: "status", type: "text", props: { text: statusText, tone: statusTone } },
      {
        id: "results",
        type: "list",
        props: {
          items: resultItems,
          emptyText: "No results yet.",
          action: "download",
          actionLabel: "Download",
        },
      },
    ],
  };
}

function createSettingsContent(): CanX.UiComponent {
  return {
    type: "stack",
    props: { spacing: "md" },
    children: [
      { type: "text", props: { text: "Search options", style: "heading" } },
      {
        type: "text",
        props: {
          text: "The configured HTTP proxy is used for API, cover, and download-resolution requests.",
          tone: "muted",
        },
      },
      {
        id: "http-proxy",
        type: "textInput",
        props: {
          label: "HTTP proxy",
          key: "httpProxy",
          placeholder: "http://127.0.0.1:7890",
          default: draftHttpProxy,
        },
      },
      {
        id: "settings-status",
        type: "text",
        props: { text: "Leave empty to use no plugin-configured proxy.", tone: "muted" },
      },
      {
        type: "grid",
        props: { columns: 2, spacing: "sm" },
        children: [
          {
            id: "save-options",
            type: "button",
            props: { label: "Save", action: "save-options", variant: "primary" },
          },
          {
            id: "cancel-options",
            type: "button",
            props: { label: "Cancel", action: "cancel-options", variant: "secondary" },
            layout: { column: 1 },
          },
        ],
      },
    ],
  };
}

async function searchFromPage(): Promise<void> {
  if (!pageQuery) {
    statusText = "Please enter a keyword.";
    statusTone = "warning";
    await page.update([{ target: "status", props: { text: statusText, tone: statusTone } }]);
    return;
  }

  statusText = `Searching for ${pageQuery}...`;
  statusTone = "accent";
  resultItems = [];
  await page.update([
    { target: "status", props: { text: statusText, tone: statusTone } },
    { target: "results", props: { items: resultItems } },
  ]);
  try {
    const result = await searchResources(pageQuery);
    resultItems = await Promise.all(result.items.map(async item =>
      toListItem(item, await loadCoverImage(item.coverUrl))));
    statusText = `Found ${result.items.length} result(s). Use a result's Download button to add it.`;
    statusTone = "success";
    await page.update([
      { target: "status", props: { text: statusText, tone: statusTone } },
      { target: "results", props: { items: resultItems } },
    ]);
  } catch (error) {
    statusText = error instanceof Error ? error.message : "Search failed.";
    statusTone = "error";
    await page.update([{ target: "status", props: { text: statusText, tone: statusTone } }]);
  }
}

async function searchResources(query: string, limit = 30, cursor?: string): Promise<MockSearchPage> {
  const normalizedLimit = Math.max(1, Math.min(100, Math.trunc(limit)));
  const parameters = [
    `q=${encodeURIComponent(query)}`,
    `limit=${normalizedLimit}`,
  ];
  if (cursor) parameters.push(`cursor=${encodeURIComponent(cursor)}`);
  const payload = await getJson<MockSearchResponse>(`${baseUrl}/search?${parameters.join("&")}`);
  return {
    items: (payload.items ?? [])
      .filter((item): item is Required<Pick<typeof item, "id" | "title">> & typeof item =>
        typeof item.id === "string" && typeof item.title === "string")
      .map(item => ({
        id: item.id,
        title: item.title,
        description: item.description,
        coverUrl: typeof item.coverUrl === "string" ? item.coverUrl : undefined,
        sizeBytes: item.sizeBytes,
        seeders: item.seeders,
        leechers: item.leechers,
      })),
    nextCursor: payload.nextCursor,
  };
}

async function resolveResource(id: string): Promise<DownloadResolution> {
  const payload = await getJson<MockResolveResponse>(
    `${baseUrl}/resolve/${encodeURIComponent(id)}`,
  );
  if ((payload.kind !== "magnet" && payload.kind !== "http") || typeof payload.source !== "string") {
    throw new Error("Mock API returned an invalid download source.");
  }
  return { kind: payload.kind, source: payload.source };
}

function formatBytes(value: number | undefined): string {
  if (!Number.isFinite(value) || !value || value <= 0) return "-";
  const units = ["B", "KiB", "MiB", "GiB", "TiB"];
  let amount = value;
  let unit = 0;
  while (amount >= 1024 && unit < units.length - 1) {
    amount /= 1024;
    unit++;
  }
  return `${amount.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

function toListItem(item: MockSearchItem, image: string | undefined): ListItem {
  const result: Record<string, string | number | boolean | null> = {
    id: item.id,
    title: item.title,
    description: item.description ?? "No description provided.",
    badge: `${formatBytes(item.sizeBytes)} - S ${item.seeders ?? 0} / L ${item.leechers ?? 0}`,
  };
  if (image) result.image = image;
  return result;
}

async function loadCoverImage(url: string | undefined): Promise<string | undefined> {
  if (!url) return undefined;
  try {
    const response = await canx.http.request({
      url,
      headers: { accept: "image/png,image/jpeg,image/webp" },
      responseType: "base64",
      ...proxyOption(),
    });
    if (response.status !== 200 || response.bodyEncoding !== "base64" || !response.body) return undefined;
    const contentTypeHeader = Object.entries(response.headers)
      .find(([name]) => name.toLowerCase() === "content-type")?.[1]?.[0];
    const contentType = contentTypeHeader?.split(";", 1)[0]?.toLowerCase();
    if (contentType !== "image/png" && contentType !== "image/jpeg" && contentType !== "image/webp") {
      return undefined;
    }
    return `data:${contentType};base64,${response.body}`;
  } catch {
    return undefined;
  }
}

async function getJson<T>(url: string): Promise<T> {
  const response = await canx.http.request({
    url,
    headers: { accept: "application/json" },
    ...proxyOption(),
  });
  if (response.status !== 200) {
    throw new Error(`Mock search API returned HTTP ${response.status}.`);
  }
  return JSON.parse(response.body) as T;
}

function proxyOption(): { readonly proxy?: string } {
  return httpProxy ? { proxy: httpProxy } : {};
}

function normalizeHttpProxy(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed) return "";
  const match = /^http:\/\/(\[[0-9a-f:]+\]|[a-z0-9.-]+)(?::([0-9]{1,5}))?\/?$/i.exec(trimmed);
  if (!match) return null;
  if (match[2] && (Number(match[2]) < 1 || Number(match[2]) > 65535)) return null;
  return trimmed.endsWith("/") ? trimmed.slice(0, -1) : trimmed;
}
