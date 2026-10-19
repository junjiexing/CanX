// SPDX-License-Identifier: GPL-3.0-or-later

export {};

import { bittorrentSearchInputSchema, bittorrentSearchOutputSchema } from "./bittorrent-contract.js";
import { mountSearchConfiguration } from "./configuration.js";

interface ApiBayItem {
  readonly id?: string;
  readonly name?: string;
  readonly info_hash?: string;
  readonly leechers?: string;
  readonly seeders?: string;
  readonly size?: string;
  readonly num_files?: string;
  readonly username?: string;
  readonly added?: string;
  readonly status?: string;
  readonly category?: string;
}

interface ApiBayOptions {
  readonly endpoint?: string;
  readonly category?: number;
  readonly proxy?: string;
}

const configuration = await mountSearchConfiguration({
  description: "配置 apibay 请求使用的本机 HTTP 代理。留空时直接连接。",
});

await canx.services.register<CanX.BittorrentSearchRequest, CanX.BittorrentSearchPage>({
  id: "main",
  contract: "org.canx.bittorrent.search",
  contractVersion: "1.0",
  title: "apibay",
  description: "Public JSON index for The Pirate Bay. Availability is not guaranteed.",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: bittorrentSearchInputSchema({
    type: "object",
    additionalProperties: false,
    properties: {
      endpoint: { type: "string", default: "https://apibay.org" },
      category: { type: "integer", default: 0 },
      proxy: { type: "string", description: "Optional HTTP proxy, for example http://127.0.0.1:7890" },
    },
  }),
  outputSchema: bittorrentSearchOutputSchema,
  async invoke(request) {
    const options = (request.options ?? {}) as ApiBayOptions;
    const limit = clamp(request.limit, 1, 100);
    const endpoint = trimSlash(options.endpoint ?? "https://apibay.org");
    const category = integer(options.category) ?? 0;
    const proxy = value(options.proxy) ?? configuration.proxy;
    const response = await canx.http.request({
      url: `${endpoint}/q.php?q=${encodeURIComponent(request.query)}&cat=${category}`,
      headers: { accept: "application/json" },
      ...(proxy ? { proxy } : {}),
    });
    ensureOk(response);
    const payload = JSON.parse(response.body) as readonly ApiBayItem[];
    const rows = Array.isArray(payload) ? payload.slice(0, limit) : [];
    return {
      items: rows.flatMap(item => {
        const title = value(item.name);
        const hash = normalizeHash(item.info_hash);
        if (!title || !hash || item.id === "0") return [];
        return [{
          id: value(item.id) ?? hash,
          title,
          description: description(item),
          infoHash: hash,
          magnetUri: `magnet:?xt=urn:btih:${hash}&dn=${encodeURIComponent(title)}`,
          sizeBytes: natural(item.size),
          seeders: natural(item.seeders),
          leechers: natural(item.leechers),
          publishedAt: unixDate(item.added),
          categories: value(item.category) ? [value(item.category)!] : undefined,
          source: "The Pirate Bay",
          attributes: {
            files: natural(item.num_files) ?? null,
            uploader: value(item.username) ?? null,
            status: value(item.status) ?? null,
          },
        }];
      }),
    };
  },
});

function description(item: ApiBayItem): string | undefined {
  const parts = [value(item.username) ? `Uploader: ${value(item.username)}` : undefined,
    natural(item.num_files) !== undefined ? `Files: ${natural(item.num_files)}` : undefined].filter(Boolean);
  return parts.length ? parts.join(" · ") : undefined;
}

function trimSlash(input: string): string { return input.replace(/\/+$/, ""); }
function value(input: unknown): string | undefined { return typeof input === "string" && input.trim() ? input.trim() : undefined; }
function integer(input: unknown): number | undefined { const number = Number(input); return Number.isSafeInteger(number) ? number : undefined; }
function natural(input: unknown): number | undefined { const number = integer(input); return number !== undefined && number >= 0 ? number : undefined; }
function clamp(input: number | undefined, min: number, max: number): number { return Math.max(min, Math.min(max, Math.trunc(input ?? 20))); }
function normalizeHash(input: unknown): string | undefined { const hash = value(input)?.toUpperCase(); return hash && /^[0-9A-F]{40}$/.test(hash) ? hash : undefined; }
function unixDate(input: unknown): string | undefined { const seconds = natural(input); return seconds === undefined ? undefined : new Date(seconds * 1000).toISOString(); }
function ensureOk(response: CanX.HttpResponse): void { if (response.status < 200 || response.status >= 300) throw new Error(`apibay search returned HTTP ${response.status}.`); }
