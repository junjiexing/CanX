// SPDX-License-Identifier: GPL-3.0-or-later

export {};

import { bittorrentSearchInputSchema, bittorrentSearchOutputSchema } from "./bittorrent-contract.js";
import { mountSearchConfiguration } from "./configuration.js";

interface NyaaOptions {
  readonly endpoint?: string;
  readonly category?: string;
  readonly filter?: "0" | "1" | "2";
  readonly user?: string;
  readonly sort?: "comments" | "size" | "date" | "seeders" | "leechers" | "downloads";
  readonly order?: "asc" | "desc";
  readonly proxy?: string;
}

const configuration = await mountSearchConfiguration({
  description: "配置 Nyaa RSS 请求使用的本机 HTTP 代理。留空时直接连接。",
});

await canx.services.register<CanX.BittorrentSearchRequest, CanX.BittorrentSearchPage>({
  id: "main",
  contract: "org.canx.bittorrent.search",
  contractVersion: "1.0",
  title: "Nyaa",
  description: "Anime torrent search through the public Nyaa RSS feed.",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: bittorrentSearchInputSchema({
    type: "object",
    additionalProperties: false,
    properties: {
      endpoint: { type: "string", default: "https://nyaa.si" },
      category: { type: "string", default: "0_0", description: "Nyaa category code" },
      filter: { type: "string", enum: ["0", "1", "2"], default: "0" },
      user: { type: "string" },
      sort: { type: "string", enum: ["comments", "size", "date", "seeders", "leechers", "downloads"] },
      order: { type: "string", enum: ["asc", "desc"] },
      proxy: { type: "string", description: "Optional HTTP proxy, for example http://127.0.0.1:7890" },
    },
  }),
  outputSchema: bittorrentSearchOutputSchema,
  async invoke(request) {
    const options = (request.options ?? {}) as NyaaOptions;
    const endpoint = trimSlash(options.endpoint ?? "https://nyaa.si");
    const page = positiveInteger(request.cursor, 1);
    const proxy = stringValue(options.proxy) ?? configuration.proxy;
    const parameters = [
      "page=rss",
      `q=${encodeURIComponent(request.query)}`,
      `c=${encodeURIComponent(options.category ?? "0_0")}`,
      `f=${encodeURIComponent(options.filter ?? "0")}`,
      ...(options.user ? [`u=${encodeURIComponent(options.user)}`] : []),
      ...(options.sort ? [`s=${encodeURIComponent(options.sort)}`] : []),
      ...(options.order ? [`o=${encodeURIComponent(options.order)}`] : []),
      ...(page > 1 ? [`p=${page}`] : []),
    ];
    const response = await canx.http.request({
      url: `${endpoint}/?${parameters.join("&")}`,
      headers: { accept: "application/rss+xml, application/xml;q=0.9" },
      ...(proxy ? { proxy } : {}),
    });
    if (response.status < 200 || response.status >= 300) throw new Error(`Nyaa RSS returned HTTP ${response.status}.`);
    const limit = clamp(request.limit, 1, 100);
    const entries = response.body.match(/<item\b[\s\S]*?<\/item>/gi) ?? [];
    const items = entries.slice(0, limit).flatMap(xml => {
      const title = tag(xml, "title");
      const hash = normalizeHash(tag(xml, "nyaa:infoHash"));
      const torrentUrl = httpUrl(tag(xml, "link"));
      if (!title || (!hash && !torrentUrl)) return [];
      const detailsUrl = httpUrl(tag(xml, "guid"));
      const category = tag(xml, "nyaa:category");
      return [{
        id: detailsUrl?.match(/\/view\/(\d+)/)?.[1] ?? hash ?? torrentUrl!,
        title,
        description: cleanDescription(tag(xml, "description")),
        infoHash: hash,
        magnetUri: hash ? `magnet:?xt=urn:btih:${hash}&dn=${encodeURIComponent(title)}` : undefined,
        torrentUrl,
        detailsUrl,
        sizeBytes: parseSize(tag(xml, "nyaa:size")),
        seeders: natural(tag(xml, "nyaa:seeders")),
        leechers: natural(tag(xml, "nyaa:leechers")),
        completed: natural(tag(xml, "nyaa:downloads")),
        publishedAt: isoDate(tag(xml, "pubDate")),
        categories: category ? [category] : undefined,
        source: "Nyaa",
      }];
    });
    return { items, nextCursor: entries.length >= limit ? String(page + 1) : undefined };
  },
});

function tag(xml: string, name: string): string | undefined {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const match = new RegExp(`<${escaped}(?:\\s[^>]*)?>([\\s\\S]*?)<\\/${escaped}>`, "i").exec(xml);
  if (!match) return undefined;
  const raw = match[1].replace(/^<!\[CDATA\[|\]\]>$/g, "");
  return decodeXml(raw).trim() || undefined;
}

function decodeXml(value: string): string {
  return value.replace(/&#(x?[0-9a-f]+);|&(amp|lt|gt|quot|apos);/gi, (entity, numeric, named) => {
    if (numeric) return String.fromCodePoint(Number.parseInt(numeric.replace(/^x/i, ""), /^x/i.test(numeric) ? 16 : 10));
    return ({ amp: "&", lt: "<", gt: ">", quot: "\"", apos: "'" } as Record<string, string>)[String(named).toLowerCase()] ?? entity;
  });
}

function cleanDescription(value: string | undefined): string | undefined {
  const cleaned = value?.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim();
  return cleaned ? cleaned.slice(0, 4096) : undefined;
}

function parseSize(value: string | undefined): number | undefined {
  const match = value?.match(/^([\d.]+)\s*(B|KiB|MiB|GiB|TiB)$/i);
  if (!match) return undefined;
  const units: Record<string, number> = { b: 1, kib: 1024, mib: 1024 ** 2, gib: 1024 ** 3, tib: 1024 ** 4 };
  const result = Number(match[1]) * units[match[2].toLowerCase()];
  return Number.isSafeInteger(Math.round(result)) ? Math.round(result) : undefined;
}

function stringValue(value: unknown): string | undefined { return typeof value === "string" && value.trim() ? value.trim() : undefined; }
function httpUrl(value: unknown): string | undefined { const url = stringValue(value); return url && /^https?:\/\//i.test(url) ? url : undefined; }
function normalizeHash(value: unknown): string | undefined { const hash = stringValue(value)?.toUpperCase(); return hash && /^[0-9A-F]{40}$/.test(hash) ? hash : undefined; }
function natural(value: unknown): number | undefined { const number = Number(value); return Number.isSafeInteger(number) && number >= 0 ? number : undefined; }
function clamp(value: number | undefined, min: number, max: number): number { return Math.max(min, Math.min(max, Math.trunc(value ?? 20))); }
function positiveInteger(value: string | undefined, fallback: number): number { const result = Number.parseInt(value ?? "", 10); return Number.isSafeInteger(result) && result > 0 ? result : fallback; }
function trimSlash(value: string): string { return value.replace(/\/+$/, ""); }
function isoDate(value: string | undefined): string | undefined { const time = value ? Date.parse(value) : NaN; return Number.isFinite(time) ? new Date(time).toISOString() : undefined; }
