// SPDX-License-Identifier: GPL-3.0-or-later

export {};

import { bittorrentSearchInputSchema, bittorrentSearchOutputSchema } from "./bittorrent-contract.js";
import { mountSearchConfiguration } from "./configuration.js";

interface BitmagnetTorrent {
  readonly name?: string;
  readonly size?: number;
  readonly magnetUri?: string;
  readonly seeders?: number;
  readonly leechers?: number;
  readonly sources?: readonly { readonly name?: string }[];
}
interface BitmagnetItem {
  readonly id?: string;
  readonly infoHash?: string;
  readonly title?: string;
  readonly contentType?: string;
  readonly seeders?: number;
  readonly leechers?: number;
  readonly publishedAt?: string;
  readonly torrent?: BitmagnetTorrent;
  readonly content?: { readonly overview?: string };
}
interface BitmagnetResponse {
  readonly data?: { readonly torrentContent?: { readonly search?: { readonly items?: readonly BitmagnetItem[]; readonly totalCount?: number; readonly hasNextPage?: boolean } } };
  readonly errors?: readonly { readonly message?: string }[];
}
interface BitmagnetOptions { readonly endpoint?: string; readonly proxy?: string; }

const query = `query CanXTorrentSearch($input: TorrentContentSearchQueryInput!) {
  torrentContent { search(input: $input) {
    totalCount hasNextPage
    items { id infoHash title contentType seeders leechers publishedAt
      torrent { name size magnetUri seeders leechers sources { name } }
      content { overview }
    }
  } }
}`;

const configuration = await mountSearchConfiguration({
  description: "配置 bitmagnet 请求使用的本机 HTTP 代理。留空时直接连接本机服务。",
});

await canx.services.register<CanX.BittorrentSearchRequest, CanX.BittorrentSearchPage>({
  id: "main",
  contract: "org.canx.bittorrent.search",
  contractVersion: "1.0",
  title: "bitmagnet",
  description: "Searches a local self-hosted bitmagnet DHT index through GraphQL.",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: bittorrentSearchInputSchema({
    type: "object",
    additionalProperties: false,
    properties: {
      endpoint: { type: "string", default: "http://127.0.0.1:3333/graphql" },
      proxy: { type: "string", description: "Optional HTTP proxy, for example http://127.0.0.1:7890" },
    },
  }),
  outputSchema: bittorrentSearchOutputSchema,
  async invoke(request) {
    const options = (request.options ?? {}) as BitmagnetOptions;
    const limit = clamp(request.limit, 1, 100);
    const page = positiveInteger(request.cursor, 1);
    const proxy = stringValue(options.proxy) ?? configuration.proxy;
    const response = await canx.http.request({
      url: options.endpoint ?? "http://127.0.0.1:3333/graphql",
      method: "POST",
      headers: { accept: "application/json", "content-type": "application/json" },
      body: JSON.stringify({ query, variables: { input: { queryString: request.query, limit, page, totalCount: true, hasNextPage: true } } }),
      ...(proxy ? { proxy } : {}),
    });
    if (response.status < 200 || response.status >= 300) throw new Error(`bitmagnet returned HTTP ${response.status}.`);
    const payload = JSON.parse(response.body) as BitmagnetResponse;
    if (payload.errors?.length) throw new Error(payload.errors.map(error => error.message ?? "GraphQL error").join("; "));
    const search = payload.data?.torrentContent?.search;
    const rows = Array.isArray(search?.items) ? search.items.slice(0, limit) : [];
    return {
      items: rows.flatMap(item => {
        const title = stringValue(item.title) ?? stringValue(item.torrent?.name);
        const infoHash = normalizeHash(item.infoHash);
        const magnetUri = magnetValue(item.torrent?.magnetUri) ?? (infoHash && title ? `magnet:?xt=urn:btih:${infoHash}&dn=${encodeURIComponent(title)}` : undefined);
        if (!title || (!infoHash && !magnetUri)) return [];
        return [{
          id: (stringValue(item.id) ?? infoHash!).slice(0, 256),
          title,
          description: stringValue(item.content?.overview)?.slice(0, 4096),
          infoHash,
          magnetUri,
          sizeBytes: natural(item.torrent?.size),
          seeders: natural(item.seeders) ?? natural(item.torrent?.seeders),
          leechers: natural(item.leechers) ?? natural(item.torrent?.leechers),
          publishedAt: isoDate(item.publishedAt),
          categories: stringValue(item.contentType) ? [stringValue(item.contentType)!] : undefined,
          source: stringValue(item.torrent?.sources?.[0]?.name) ?? "bitmagnet",
        }];
      }),
      nextCursor: search?.hasNextPage ? String(page + 1) : undefined,
      total: natural(search?.totalCount),
    };
  },
});

function stringValue(value: unknown): string | undefined { return typeof value === "string" && value.trim() ? value.trim() : undefined; }
function natural(value: unknown): number | undefined { const number = Number(value); return Number.isSafeInteger(number) && number >= 0 ? number : undefined; }
function normalizeHash(value: unknown): string | undefined { const hash = stringValue(value)?.toUpperCase(); return hash && /^[0-9A-F]{40}$/.test(hash) ? hash : undefined; }
function magnetValue(value: unknown): string | undefined { const result = stringValue(value); return result?.toLowerCase().startsWith("magnet:?") ? result : undefined; }
function clamp(value: number | undefined, min: number, max: number): number { return Math.max(min, Math.min(max, Math.trunc(value ?? 20))); }
function positiveInteger(value: string | undefined, fallback: number): number { const result = Number.parseInt(value ?? "", 10); return Number.isSafeInteger(result) && result > 0 ? result : fallback; }
function isoDate(value: string | undefined): string | undefined { const time = value ? Date.parse(value) : NaN; return Number.isFinite(time) ? new Date(time).toISOString() : undefined; }
