// SPDX-License-Identifier: GPL-3.0-or-later

export {};

import { bittorrentSearchInputSchema, bittorrentSearchOutputSchema } from "./bittorrent-contract.js";
import { mountSearchConfiguration } from "./configuration.js";

interface KnabenHit {
  readonly id?: string;
  readonly title?: string;
  readonly hash?: string;
  readonly magnetUrl?: string;
  readonly link?: string;
  readonly details?: string;
  readonly bytes?: number;
  readonly seeders?: number;
  readonly peers?: number;
  readonly grabs?: number;
  readonly date?: string;
  readonly category?: string;
  readonly tracker?: string;
}

interface KnabenResponse {
  readonly hits?: readonly KnabenHit[];
  readonly total?: { readonly value?: number };
}

interface KnabenOptions {
  readonly endpoint?: string;
  readonly proxy?: string;
  readonly categoryIds?: readonly number[];
  readonly hideUnsafe?: boolean;
  readonly hideAdult?: boolean;
}

const configuration = await mountSearchConfiguration({
  description: "配置 Knaben API 请求使用的本机 HTTP 代理。留空时直接连接。",
});

await canx.services.register<CanX.BittorrentSearchRequest, CanX.BittorrentSearchPage>({
  id: "main",
  contract: "org.canx.bittorrent.search",
  contractVersion: "1.0",
  title: "Knaben",
  description: "Public aggregated torrent index. No API key is required.",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: bittorrentSearchInputSchema({
    type: "object",
    additionalProperties: false,
    properties: {
      endpoint: { type: "string", default: "https://api.knaben.org/v1" },
      proxy: { type: "string", description: "Optional HTTP proxy, for example http://127.0.0.1:7890" },
      categoryIds: { type: "array", items: { type: "integer" } },
      hideUnsafe: { type: "boolean", default: true },
      hideAdult: { type: "boolean", default: true },
    },
  }),
  outputSchema: bittorrentSearchOutputSchema,
  async invoke(request) {
    const options = (request.options ?? {}) as KnabenOptions;
    const limit = clamp(request.limit, 1, 100);
    const offset = cursorNumber(request.cursor, 0);
    const proxy = text(options.proxy) ?? configuration.proxy;
    const response = await canx.http.request({
      url: options.endpoint ?? "https://api.knaben.org/v1",
      method: "POST",
      headers: { accept: "application/json", "content-type": "application/json" },
      body: JSON.stringify({
        search_type: "100%",
        search_field: "title",
        query: request.query,
        from: offset,
        size: limit,
        hide_unsafe: options.hideUnsafe ?? true,
        hide_xxx: options.hideAdult ?? true,
        ...(Array.isArray(options.categoryIds) ? { categories: options.categoryIds } : {}),
      }),
      ...(proxy ? { proxy } : {}),
    });
    ensureOk(response, "Knaben");
    const payload = JSON.parse(response.body) as KnabenResponse;
    const hits = Array.isArray(payload.hits) ? payload.hits.slice(0, limit) : [];
    const total = nonNegativeInteger(payload.total?.value);
    return {
      items: hits.flatMap(hit => {
        const title = text(hit.title);
        const infoHash = normalizeInfoHash(hit.hash);
        const magnetUri = magnet(hit.magnetUrl, infoHash, title);
        const torrentUrl = httpUrl(hit.link);
        if (!title || (!infoHash && !magnetUri && !torrentUrl)) return [];
        return [{
          id: text(hit.id) ?? infoHash ?? magnetUri ?? torrentUrl!,
          title,
          infoHash,
          magnetUri,
          torrentUrl,
          detailsUrl: httpUrl(hit.details),
          sizeBytes: nonNegativeInteger(hit.bytes),
          seeders: nonNegativeInteger(hit.seeders),
          leechers: nonNegativeInteger(hit.peers),
          completed: nonNegativeInteger(hit.grabs),
          publishedAt: isoDate(hit.date),
          categories: text(hit.category) ? [text(hit.category)!] : undefined,
          source: text(hit.tracker) ?? "Knaben",
        }];
      }),
      nextCursor: total !== undefined && offset + hits.length < total ? String(offset + hits.length) : undefined,
      total,
    };
  },
});

function clamp(value: number | undefined, minimum: number, maximum: number): number {
  const number = Number.isFinite(value) ? Math.trunc(value!) : 20;
  return Math.max(minimum, Math.min(maximum, number));
}

function cursorNumber(value: string | undefined, fallback: number): number {
  const parsed = Number.parseInt(value ?? "", 10);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : fallback;
}

function text(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value.trim() : undefined;
}

function nonNegativeInteger(value: unknown): number | undefined {
  const number = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(number) && number >= 0 ? number : undefined;
}

function normalizeInfoHash(value: unknown): string | undefined {
  const hash = text(value)?.toUpperCase();
  return hash && /^[0-9A-F]{40}$/.test(hash) ? hash : undefined;
}

function magnet(value: unknown, hash: string | undefined, title: string | undefined): string | undefined {
  const supplied = text(value);
  if (supplied?.toLowerCase().startsWith("magnet:?")) return supplied;
  return hash ? `magnet:?xt=urn:btih:${hash}${title ? `&dn=${encodeURIComponent(title)}` : ""}` : undefined;
}

function httpUrl(value: unknown): string | undefined {
  const url = text(value);
  return url && /^https?:\/\//i.test(url) ? url : undefined;
}

function isoDate(value: unknown): string | undefined {
  const date = text(value);
  if (!date) return undefined;
  const milliseconds = Date.parse(date);
  return Number.isFinite(milliseconds) ? new Date(milliseconds).toISOString() : undefined;
}

function ensureOk(response: CanX.HttpResponse, source: string): void {
  if (response.status < 200 || response.status >= 300) {
    throw new Error(`${source} search returned HTTP ${response.status}.`);
  }
}
