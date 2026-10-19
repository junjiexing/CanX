// SPDX-License-Identifier: GPL-3.0-or-later

export {};

import { bittorrentSearchInputSchema, bittorrentSearchOutputSchema } from "./bittorrent-contract.js";
import { mountSearchConfiguration } from "./configuration.js";

interface JackettOptions {
  readonly endpoint?: string;
  readonly apiKey?: string;
  readonly indexer?: string;
  readonly categories?: readonly number[];
  readonly proxy?: string;
}

const configuration = await mountSearchConfiguration({
  description: "配置本机 Jackett 请求使用的 HTTP 代理和 API Key。API Key 会保存在系统凭据存储中。",
  token: {
    label: "Jackett API Key",
    secretName: "api-key",
    helpLabel: "打开 Jackett 项目说明",
    helpUrl: "https://github.com/Jackett/Jackett#web-ui",
    required: true,
  },
});

await canx.services.register<CanX.BittorrentSearchRequest, CanX.BittorrentSearchPage>({
  id: "main",
  contract: "org.canx.bittorrent.search",
  contractVersion: "1.0",
  title: "Jackett",
  description: "Searches one or all indexers configured in a local Jackett instance through Torznab.",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: bittorrentSearchInputSchema({
    type: "object",
    additionalProperties: false,
    properties: {
      endpoint: { type: "string", default: "http://127.0.0.1:9117" },
      apiKey: { type: "string", description: "Jackett API key" },
      indexer: { type: "string", default: "all" },
      categories: { type: "array", items: { type: "integer" } },
      proxy: { type: "string", description: "Optional HTTP proxy, for example http://127.0.0.1:7890" },
    },
  }),
  outputSchema: bittorrentSearchOutputSchema,
  async invoke(request) {
    const options = (request.options ?? {}) as JackettOptions;
    const apiKey = required(options.apiKey ?? configuration.token, "Jackett API key (options.apiKey or plugin setting)");
    const endpoint = trimSlash(options.endpoint ?? "http://127.0.0.1:9117");
    const proxy = stringValue(options.proxy) ?? configuration.proxy;
    const indexer = stringValue(options.indexer) ?? "all";
    const limit = clamp(request.limit, 1, 100);
    const offset = cursorNumber(request.cursor, 0);
    const category = Array.isArray(options.categories) && options.categories.length
      ? `&cat=${encodeURIComponent(options.categories.join(","))}`
      : "";
    const url = `${endpoint}/api/v2.0/indexers/${encodeURIComponent(indexer)}/results/torznab/api` +
      `?apikey=${encodeURIComponent(apiKey)}&t=search&q=${encodeURIComponent(request.query)}` +
      `&limit=${limit}&offset=${offset}${category}`;
    const response = await canx.http.request({
      url,
      headers: { accept: "application/rss+xml, application/xml;q=0.9" },
      ...(proxy ? { proxy } : {}),
    });
    if (response.status < 200 || response.status >= 300) throw new Error(`Jackett returned HTTP ${response.status}.`);
    const entries = response.body.match(/<item\b[\s\S]*?<\/item>/gi) ?? [];
    const items = entries.slice(0, limit).flatMap((xml, position) => {
      const title = tag(xml, "title");
      const infoHash = normalizeHash(attribute(xml, "infohash"));
      const rawLink = tag(xml, "link") ?? elementAttribute(xml, "enclosure", "url");
      const magnetUri = magnetValue(attribute(xml, "magneturl")) ?? magnetValue(rawLink) ??
        (infoHash && title ? `magnet:?xt=urn:btih:${infoHash}&dn=${encodeURIComponent(title)}` : undefined);
      const torrentUrl = sameEndpointHttp(rawLink, endpoint);
      if (!title || (!infoHash && !magnetUri && !torrentUrl)) return [];
      const guid = tag(xml, "guid");
      return [{
        id: (infoHash ?? stringValue(guid) ?? `${offset + position}`).slice(0, 256),
        title,
        description: cleanText(tag(xml, "description")),
        infoHash,
        magnetUri,
        torrentUrl,
        detailsUrl: sameEndpointHttp(tag(xml, "comments"), endpoint),
        sizeBytes: natural(tag(xml, "size") ?? attribute(xml, "size")),
        seeders: natural(attribute(xml, "seeders")),
        leechers: natural(attribute(xml, "peers") ?? attribute(xml, "leechers")),
        completed: natural(attribute(xml, "grabs") ?? attribute(xml, "downloads")),
        publishedAt: isoDate(tag(xml, "pubDate")),
        categories: tags(xml, "category"),
        source: attribute(xml, "jackettindexer") ?? "Jackett",
      }];
    });
    return { items, nextCursor: entries.length >= limit ? String(offset + entries.length) : undefined };
  },
});

function attribute(xml: string, name: string): string | undefined {
  const attributes = xml.match(/<(?:torznab|newznab):attr\b[^>]*>/gi) ?? [];
  for (const entry of attributes) {
    const key = xmlAttribute(entry, "name");
    if (key?.toLowerCase() === name.toLowerCase()) return xmlAttribute(entry, "value");
  }
  return undefined;
}

function elementAttribute(xml: string, element: string, name: string): string | undefined {
  const match = new RegExp(`<${element}\\b[^>]*>`, "i").exec(xml);
  return match ? xmlAttribute(match[0], name) : undefined;
}

function xmlAttribute(element: string, name: string): string | undefined {
  const match = new RegExp(`\\b${name}\\s*=\\s*["']([^"']*)["']`, "i").exec(element);
  return match ? decodeXml(match[1]).trim() || undefined : undefined;
}

function tag(xml: string, name: string): string | undefined {
  const matches = tags(xml, name);
  return matches[0];
}

function tags(xml: string, name: string): string[] {
  const values: string[] = [];
  const pattern = new RegExp(`<${name}(?:\\s[^>]*)?>([\\s\\S]*?)<\\/${name}>`, "gi");
  for (let match = pattern.exec(xml); match; match = pattern.exec(xml)) {
    const value = decodeXml(match[1].replace(/^<!\[CDATA\[|\]\]>$/g, "")).trim();
    if (value) values.push(value.slice(0, 128));
  }
  return values.slice(0, 32);
}

function decodeXml(value: string): string {
  return value.replace(/&#(x?[0-9a-f]+);|&(amp|lt|gt|quot|apos);/gi, (entity, numeric, named) => {
    if (numeric) return String.fromCodePoint(Number.parseInt(numeric.replace(/^x/i, ""), /^x/i.test(numeric) ? 16 : 10));
    return ({ amp: "&", lt: "<", gt: ">", quot: "\"", apos: "'" } as Record<string, string>)[String(named).toLowerCase()] ?? entity;
  });
}

function sameEndpointHttp(value: unknown, endpoint: string): string | undefined {
  const url = stringValue(value);
  return url && /^https?:\/\//i.test(url) && origin(url) === origin(endpoint) ? url : undefined;
}
function origin(url: string): string | undefined { return /^(https?:\/\/[^/]+)/i.exec(url)?.[1].toLowerCase(); }
function cleanText(value: string | undefined): string | undefined { const result = value?.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim(); return result ? result.slice(0, 4096) : undefined; }
function magnetValue(value: unknown): string | undefined { const result = stringValue(value); return result?.toLowerCase().startsWith("magnet:?") ? result : undefined; }
function normalizeHash(value: unknown): string | undefined { const hash = stringValue(value)?.toUpperCase(); return hash && /^[0-9A-F]{40}$/.test(hash) ? hash : undefined; }
function stringValue(value: unknown): string | undefined { return typeof value === "string" && value.trim() ? value.trim() : undefined; }
function required(value: unknown, name: string): string { const result = stringValue(value); if (!result) throw new Error(`${name} is required.`); return result; }
function natural(value: unknown): number | undefined { const number = Number(value); return Number.isSafeInteger(number) && number >= 0 ? number : undefined; }
function clamp(value: number | undefined, min: number, max: number): number { return Math.max(min, Math.min(max, Math.trunc(value ?? 20))); }
function cursorNumber(value: string | undefined, fallback: number): number { const result = Number.parseInt(value ?? "", 10); return Number.isSafeInteger(result) && result >= 0 ? result : fallback; }
function trimSlash(value: string): string { return value.replace(/\/+$/, ""); }
function isoDate(value: string | undefined): string | undefined { const time = value ? Date.parse(value) : NaN; return Number.isFinite(time) ? new Date(time).toISOString() : undefined; }
