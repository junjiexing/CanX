// SPDX-License-Identifier: GPL-3.0-or-later

export {};

import { bittorrentSearchInputSchema, bittorrentSearchOutputSchema } from "./bittorrent-contract.js";
import { mountSearchConfiguration } from "./configuration.js";

interface TriblerTorrent {
  readonly infohash?: string;
  readonly name?: string;
  readonly title?: string;
  readonly description?: string;
  readonly size?: number;
  readonly category?: string;
  readonly tags?: string | readonly string[];
  readonly num_seeders?: number;
  readonly num_leechers?: number;
  readonly seeders?: number;
  readonly leechers?: number;
  readonly created?: number | string;
  readonly torrent_date?: number | string;
  readonly status?: string;
}
interface TriblerResponse { readonly results?: readonly TriblerTorrent[]; readonly total?: number; }
interface TriblerOptions {
  readonly endpoint?: string;
  readonly apiKey?: string;
  readonly category?: string;
  readonly hideAdult?: boolean;
  readonly sortBy?: "health" | "date" | "size" | "name";
  readonly proxy?: string;
}

const configuration = await mountSearchConfiguration({
  description: "配置本机 Tribler REST API 请求使用的 HTTP 代理和可选 API Key。API Key 会保存在系统凭据存储中。",
  token: {
    label: "Tribler API Key（可选）",
    secretName: "api-key",
    helpLabel: "查看 Tribler REST API 说明",
    helpUrl: "https://github.com/Tribler/tribler/wiki/REST-API",
    required: false,
  },
});

await canx.services.register<CanX.BittorrentSearchRequest, CanX.BittorrentSearchPage>({
  id: "main",
  contract: "org.canx.bittorrent.search",
  contractVersion: "1.0",
  title: "Tribler",
  description: "Searches the local Tribler metadata database through its REST API.",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: bittorrentSearchInputSchema({
    type: "object",
    additionalProperties: false,
    properties: {
      endpoint: { type: "string", default: "http://127.0.0.1:8085" },
      apiKey: { type: "string", description: "Optional Tribler REST API key" },
      category: { type: "string" },
      hideAdult: { type: "boolean", default: true },
      sortBy: { type: "string", enum: ["health", "date", "size", "name"], default: "health" },
      proxy: { type: "string", description: "Optional HTTP proxy, for example http://127.0.0.1:7890" },
    },
  }),
  outputSchema: bittorrentSearchOutputSchema,
  async invoke(request) {
    const options = (request.options ?? {}) as TriblerOptions;
    const endpoint = trimSlash(options.endpoint ?? "http://127.0.0.1:8085");
    const limit = clamp(request.limit, 1, 100);
    const first = positiveInteger(request.cursor, 1);
    const last = first + limit - 1;
    const query = [
      `first=${first}`,
      `last=${last}`,
      "metadata_type=300",
      "exclude_deleted=1",
      "include_total=1",
      `fts_text=${encodeURIComponent(request.query)}`,
      `hide_xxx=${options.hideAdult ?? true}`,
      `sort_by=${encodeURIComponent(options.sortBy ?? "health")}`,
      "sort_desc=true",
      ...(stringValue(options.category) ? [`category=${encodeURIComponent(options.category!)}`] : []),
    ].join("&");
    const apiKey = stringValue(options.apiKey) ?? configuration.token;
    const proxy = stringValue(options.proxy) ?? configuration.proxy;
    const response = await canx.http.request({
      url: `${endpoint}/api/metadata/search/local?${query}`,
      headers: { accept: "application/json", ...(apiKey ? { "x-api-key": apiKey } : {}) },
      ...(proxy ? { proxy } : {}),
    });
    if (response.status < 200 || response.status >= 300) throw new Error(`Tribler returned HTTP ${response.status}.`);
    const payload = JSON.parse(response.body) as TriblerResponse;
    const rows = Array.isArray(payload.results) ? payload.results.slice(0, limit) : [];
    const total = natural(payload.total);
    return {
      items: rows.flatMap(item => {
        const title = stringValue(item.name) ?? stringValue(item.title);
        const infoHash = normalizeHash(item.infohash);
        if (!title || !infoHash) return [];
        const categories = [stringValue(item.category), ...stringArray(item.tags)].filter((entry): entry is string => Boolean(entry));
        return [{
          id: infoHash,
          title,
          description: stringValue(item.description)?.slice(0, 4096),
          infoHash,
          magnetUri: `magnet:?xt=urn:btih:${infoHash}&dn=${encodeURIComponent(title)}`,
          sizeBytes: natural(item.size),
          seeders: natural(item.num_seeders) ?? natural(item.seeders),
          leechers: natural(item.num_leechers) ?? natural(item.leechers),
          publishedAt: timestamp(item.torrent_date ?? item.created),
          categories: categories.length ? categories.slice(0, 32) : undefined,
          source: "Tribler",
          attributes: { status: stringValue(item.status) ?? null },
        }];
      }),
      nextCursor: total !== undefined ? (last < total ? String(last + 1) : undefined) : (rows.length >= limit ? String(last + 1) : undefined),
      total,
    };
  },
});

function stringArray(value: unknown): string[] { if (typeof value === "string") return value.split(",").map(item => item.trim()).filter(Boolean); return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string" && Boolean(item.trim())).map(item => item.trim()) : []; }
function stringValue(value: unknown): string | undefined { return typeof value === "string" && value.trim() ? value.trim() : undefined; }
function natural(value: unknown): number | undefined { const number = Number(value); return Number.isSafeInteger(number) && number >= 0 ? number : undefined; }
function normalizeHash(value: unknown): string | undefined { const hash = stringValue(value)?.toUpperCase(); return hash && /^[0-9A-F]{40}$/.test(hash) ? hash : undefined; }
function clamp(value: number | undefined, min: number, max: number): number { return Math.max(min, Math.min(max, Math.trunc(value ?? 20))); }
function positiveInteger(value: string | undefined, fallback: number): number { const result = Number.parseInt(value ?? "", 10); return Number.isSafeInteger(result) && result > 0 ? result : fallback; }
function trimSlash(value: string): string { return value.replace(/\/+$/, ""); }
function timestamp(value: unknown): string | undefined { if (typeof value === "number" && Number.isFinite(value)) return new Date(value * 1000).toISOString(); const time = typeof value === "string" ? Date.parse(value) : NaN; return Number.isFinite(time) ? new Date(time).toISOString() : undefined; }
