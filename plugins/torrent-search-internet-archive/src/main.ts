// SPDX-License-Identifier: GPL-3.0-or-later

export {};

import { bittorrentSearchInputSchema, bittorrentSearchOutputSchema } from "./bittorrent-contract.js";
import { mountSearchConfiguration } from "./configuration.js";

interface ArchiveDocument {
  readonly identifier?: string;
  readonly title?: string;
  readonly description?: string | readonly string[];
  readonly item_size?: number;
  readonly downloads?: number;
  readonly publicdate?: string;
  readonly mediatype?: string;
  readonly collection?: string | readonly string[];
}

interface ArchiveResponse {
  readonly response?: { readonly docs?: readonly ArchiveDocument[]; readonly numFound?: number };
}

interface ArchiveOptions {
  readonly endpoint?: string;
  readonly mediaType?: string;
  readonly collection?: string;
  readonly proxy?: string;
}

const configuration = await mountSearchConfiguration({
  description: "配置 Internet Archive 请求使用的本机 HTTP 代理。留空时直接连接。",
});

await canx.services.register<CanX.BittorrentSearchRequest, CanX.BittorrentSearchPage>({
  id: "main",
  contract: "org.canx.bittorrent.search",
  contractVersion: "1.0",
  title: "Internet Archive",
  description: "Searches openly accessible Internet Archive items and returns their generated torrent files.",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: bittorrentSearchInputSchema({
    type: "object",
    additionalProperties: false,
    properties: {
      endpoint: { type: "string", default: "https://archive.org" },
      mediaType: { type: "string", description: "Optional Archive mediatype, such as movies, audio, or software" },
      collection: { type: "string" },
      proxy: { type: "string", description: "Optional HTTP proxy, for example http://127.0.0.1:7890" },
    },
  }),
  outputSchema: bittorrentSearchOutputSchema,
  async invoke(request) {
    const options = (request.options ?? {}) as ArchiveOptions;
    const endpoint = trimSlash(options.endpoint ?? "https://archive.org");
    const limit = clamp(request.limit, 1, 50);
    const page = positiveInteger(request.cursor, 1);
    const proxy = stringValue(options.proxy) ?? configuration.proxy;
    const clauses = [`(${request.query})`];
    if (stringValue(options.mediaType)) clauses.unshift(`mediatype:${options.mediaType}`);
    if (stringValue(options.collection)) clauses.unshift(`collection:${options.collection}`);
    const fields = ["identifier", "title", "description", "item_size", "downloads", "publicdate", "mediatype", "collection"];
    const fieldQuery = fields.map(field => `fl%5B%5D=${encodeURIComponent(field)}`).join("&");
    const response = await canx.http.request({
      url: `${endpoint}/advancedsearch.php?q=${encodeURIComponent(clauses.join(" AND "))}&${fieldQuery}&rows=${limit}&page=${page}&output=json`,
      headers: { accept: "application/json" },
      ...(proxy ? { proxy } : {}),
    });
    if (response.status < 200 || response.status >= 300) throw new Error(`Internet Archive search returned HTTP ${response.status}.`);
    const payload = JSON.parse(response.body) as ArchiveResponse;
    const docs = Array.isArray(payload.response?.docs) ? payload.response!.docs!.slice(0, limit) : [];
    const items = docs.flatMap(item => {
      const identifier = stringValue(item.identifier);
      if (!identifier) return [];
      const encodedId = encodeURIComponent(identifier);
      const categories = [stringValue(item.mediatype), ...stringArray(item.collection)].filter((entry): entry is string => Boolean(entry));
      return [{
        id: identifier,
        title: stringValue(item.title) ?? identifier,
        description: stringArray(item.description).join(" ").slice(0, 4096) || undefined,
        torrentUrl: `${endpoint}/download/${encodedId}/${encodedId}_archive.torrent`,
        detailsUrl: `${endpoint}/details/${encodedId}`,
        thumbnailUrl: `${endpoint}/services/img/${encodedId}`,
        sizeBytes: natural(item.item_size),
        completed: natural(item.downloads),
        publishedAt: isoDate(item.publicdate),
        categories: categories.length ? categories.slice(0, 32) : undefined,
        source: "Internet Archive",
      }];
    });
    const total = natural(payload.response?.numFound);
    return {
      items,
      nextCursor: total !== undefined && page * limit < total ? String(page + 1) : undefined,
      total,
    };
  },
});

function stringArray(value: unknown): string[] {
  if (typeof value === "string") return value.trim() ? [value.trim()] : [];
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string" && Boolean(item.trim())).map(item => item.trim()) : [];
}
function stringValue(value: unknown): string | undefined { return typeof value === "string" && value.trim() ? value.trim() : undefined; }
function natural(value: unknown): number | undefined { const number = Number(value); return Number.isSafeInteger(number) && number >= 0 ? number : undefined; }
function clamp(value: number | undefined, min: number, max: number): number { return Math.max(min, Math.min(max, Math.trunc(value ?? 20))); }
function positiveInteger(value: string | undefined, fallback: number): number { const result = Number.parseInt(value ?? "", 10); return Number.isSafeInteger(result) && result > 0 ? result : fallback; }
function trimSlash(value: string): string { return value.replace(/\/+$/, ""); }
function isoDate(value: string | undefined): string | undefined { const time = value ? Date.parse(value) : NaN; return Number.isFinite(time) ? new Date(time).toISOString() : undefined; }
