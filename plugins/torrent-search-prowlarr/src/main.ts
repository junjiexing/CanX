// SPDX-License-Identifier: GPL-3.0-or-later

export {};

import { bittorrentSearchInputSchema, bittorrentSearchOutputSchema } from "./bittorrent-contract.js";
import { mountSearchConfiguration } from "./configuration.js";

interface ProwlarrCategory { readonly id?: number; readonly name?: string; }
interface ProwlarrRelease {
  readonly guid?: string;
  readonly title?: string;
  readonly protocol?: string;
  readonly size?: number;
  readonly seeders?: number;
  readonly leechers?: number;
  readonly grabs?: number;
  readonly publishDate?: string;
  readonly downloadUrl?: string;
  readonly magnetUrl?: string;
  readonly infoUrl?: string;
  readonly indexer?: string;
  readonly indexerId?: number;
  readonly categories?: readonly ProwlarrCategory[];
}

interface ProwlarrOptions {
  readonly endpoint?: string;
  readonly apiKey?: string;
  readonly indexerIds?: readonly number[];
  readonly categoryIds?: readonly number[];
  readonly proxy?: string;
}

const configuration = await mountSearchConfiguration({
  description: "配置本机 Prowlarr 请求使用的 HTTP 代理和 API Key。API Key 会保存在系统凭据存储中。",
  token: {
    label: "Prowlarr API Key",
    secretName: "api-key",
    helpLabel: "查看 Prowlarr API Key 获取说明",
    helpUrl: "https://wiki.servarr.com/prowlarr/settings#security",
    required: true,
  },
});

await canx.services.register<CanX.BittorrentSearchRequest, CanX.BittorrentSearchPage>({
  id: "main",
  contract: "org.canx.bittorrent.search",
  contractVersion: "1.0",
  title: "Prowlarr",
  description: "Searches torrent indexers configured in a local Prowlarr instance.",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: bittorrentSearchInputSchema({
    type: "object",
    additionalProperties: false,
    properties: {
      endpoint: { type: "string", default: "http://127.0.0.1:9696" },
      apiKey: { type: "string", description: "Prowlarr API key" },
      indexerIds: { type: "array", items: { type: "integer" } },
      categoryIds: { type: "array", items: { type: "integer" } },
      proxy: { type: "string", description: "Optional HTTP proxy, for example http://127.0.0.1:7890" },
    },
  }),
  outputSchema: bittorrentSearchOutputSchema,
  async invoke(request) {
    const options = (request.options ?? {}) as ProwlarrOptions;
    const endpoint = trimSlash(options.endpoint ?? "http://127.0.0.1:9696");
    const apiKey = required(options.apiKey ?? configuration.token, "Prowlarr API key (options.apiKey or plugin setting)");
    const proxy = stringValue(options.proxy) ?? configuration.proxy;
    const limit = clamp(request.limit, 1, 100);
    const offset = cursorNumber(request.cursor, 0);
    const repeated = (name: string, values: readonly number[] | undefined) =>
      Array.isArray(values) ? values.map(value => `&${name}=${Math.trunc(value)}`).join("") : "";
    const url = `${endpoint}/api/v1/search?query=${encodeURIComponent(request.query)}` +
      `&type=search&limit=${limit}&offset=${offset}` +
      repeated("indexerIds", options.indexerIds) + repeated("categories", options.categoryIds);
    const response = await canx.http.request({
      url,
      headers: { accept: "application/json", "x-api-key": apiKey },
      ...(proxy ? { proxy } : {}),
    });
    if (response.status < 200 || response.status >= 300) throw new Error(`Prowlarr returned HTTP ${response.status}.`);
    const payload = JSON.parse(response.body) as readonly ProwlarrRelease[];
    const rows: readonly ProwlarrRelease[] = Array.isArray(payload)
      ? payload.filter((item: ProwlarrRelease) => !item.protocol || item.protocol === "torrent").slice(0, limit)
      : [];
    const items = rows.flatMap((item, position) => {
      const title = stringValue(item.title);
      const download = stringValue(item.magnetUrl) ?? stringValue(item.downloadUrl);
      const magnetUri = magnetValue(download);
      const infoHash = hashFromMagnet(magnetUri);
      const torrentUrl = sameEndpointHttp(download, endpoint);
      if (!title || (!magnetUri && !torrentUrl)) return [];
      return [{
        id: (stringValue(item.guid) ?? infoHash ?? `${offset + position}`).slice(0, 256),
        title,
        infoHash,
        magnetUri,
        torrentUrl,
        detailsUrl: sameEndpointHttp(item.infoUrl, endpoint),
        sizeBytes: natural(item.size),
        seeders: natural(item.seeders),
        leechers: natural(item.leechers),
        completed: natural(item.grabs),
        publishedAt: isoDate(item.publishDate),
        categories: item.categories
          ?.map(category => stringValue(category.name) ?? natural(category.id)?.toString())
          .filter((category): category is string => Boolean(category))
          .slice(0, 32),
        source: stringValue(item.indexer) ?? "Prowlarr",
        attributes: { indexerId: natural(item.indexerId) ?? null },
      }];
    });
    return { items, nextCursor: rows.length >= limit ? String(offset + rows.length) : undefined };
  },
});

function sameEndpointHttp(value: unknown, endpoint: string): string | undefined { const url = stringValue(value); return url && /^https?:\/\//i.test(url) && origin(url) === origin(endpoint) ? url : undefined; }
function origin(url: string): string | undefined { return /^(https?:\/\/[^/]+)/i.exec(url)?.[1].toLowerCase(); }
function magnetValue(value: unknown): string | undefined { const result = stringValue(value); return result?.toLowerCase().startsWith("magnet:?") ? result : undefined; }
function hashFromMagnet(value: string | undefined): string | undefined { const hash = value?.match(/[?&]xt=urn%3Abtih%3A([^&]+)|[?&]xt=urn:btih:([^&]+)/i); const result = hash ? decodeURIComponent(hash[1] ?? hash[2]).toUpperCase() : undefined; return result && /^[0-9A-F]{40}$/.test(result) ? result : undefined; }
function stringValue(value: unknown): string | undefined { return typeof value === "string" && value.trim() ? value.trim() : undefined; }
function required(value: unknown, name: string): string { const result = stringValue(value); if (!result) throw new Error(`${name} is required.`); return result; }
function natural(value: unknown): number | undefined { const number = Number(value); return Number.isSafeInteger(number) && number >= 0 ? number : undefined; }
function clamp(value: number | undefined, min: number, max: number): number { return Math.max(min, Math.min(max, Math.trunc(value ?? 20))); }
function cursorNumber(value: string | undefined, fallback: number): number { const result = Number.parseInt(value ?? "", 10); return Number.isSafeInteger(result) && result >= 0 ? result : fallback; }
function trimSlash(value: string): string { return value.replace(/\/+$/, ""); }
function isoDate(value: string | undefined): string | undefined { const time = value ? Date.parse(value) : NaN; return Number.isFinite(time) ? new Date(time).toISOString() : undefined; }
