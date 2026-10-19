// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from anime-find, Copyright (c) 2026 cocofhu, MIT.

import { absoluteUrl, hostMatches, hostOf, requestText, stableId, text, userAgent } from "./common.js";
import type { PluginConfig, StreamEpisode, StreamRule, StreamSource } from "./types.js";

const maximumSources = 12;
const maximumEpisodes = 120;
const scriptPrefix = "script:";
const allowedHeaderNames = new Set(["accept", "accept-language", "origin", "referer", "user-agent"]);

export const defaultStreamRules: readonly StreamRule[] = [
  {
    id: "xfdm",
    name: "xfdm（静态规则试点）",
    enabled: true,
    baseURL: "https://dm1.xfdm.pro",
    searchURL: "/search.html?wd={{keyword}}",
    searchList: "div.public-list-box.search-box",
    searchName: ".thumb-txt",
    searchResult: "a.public-list-exp",
    chapterRoads: "ul.anthology-list-play li",
    chapterResult: "a",
    chapterName: "a",
    playURL: "script:player_aaaa.url",
    mediaHosts: ["xfvod.pro", "moedot.net", "pan.wo.cn"],
    mediaHeaders: {
      "moedot.net": { Referer: "" },
      "pan.wo.cn": { Referer: "" },
    },
  },
];

export async function searchStreams(query: string, config: PluginConfig): Promise<StreamSource[]> {
  if (!config.streamEnabled) return [];
  const rules = config.streamRules.filter(rule => rule.enabled && !rule.useWebview);
  const settled = await Promise.all(rules.map(rule => searchRule(query, rule).catch(() => [])));
  return settled.flat().slice(0, maximumSources);
}

export function streamRecipe(
  source: StreamSource,
  episode: StreamEpisode,
  config: PluginConfig,
): Promise<CanX.ManagedMediaStreamRecipe> {
  const rule = config.streamRules.find(candidate => candidate.id === source.ruleId && candidate.enabled);
  if (!rule || !config.streamEnabled) throw new Error("流媒体规则不存在或已停用。");
  if (!isAllowed(source.sourceUrl, rule, false) || !isAllowed(episode.pageUrl, rule, false)) {
    throw new Error("播放页不在规则允许的站点内。");
  }
  return resolveRecipe(episode, rule);
}

export function validateRules(value: unknown): StreamRule[] {
  if (!Array.isArray(value)) throw new Error("规则 JSON 必须是数组。");
  const rules = value.slice(0, 20).map((item, index) => validateRule(item, index));
  if (rules.length === 0) throw new Error("至少需要保留一条流媒体规则。");
  return rules;
}

async function searchRule(query: string, rule: StreamRule): Promise<StreamSource[]> {
  validateRule(rule, 0);
  const searchUrl = absoluteUrl(
    rule.baseURL + "/",
    rule.searchURL.replace(/\{\{\s*(?:keyword|query)\s*\}\}/gi, encodeURIComponent(query)),
  );
  const page = await fetchPage(searchUrl, rule);
  const result = await canx.html.extract({
    html: page.body,
    selector: selector(rule.searchList, "searchList"),
    limit: 5,
    fields: {
      name: { selector: selector(rule.searchName, "searchName"), value: "text" },
      rowText: { value: "text" },
      href: { selector: selector(rule.searchResult, "searchResult"), value: "attribute", attribute: "href" },
      rowHref: { value: "attribute", attribute: "href" },
    },
  });
  const sources: StreamSource[] = [];
  for (const row of result.rows) {
    const name = field(row, "name") ?? field(row, "rowText");
    const href = field(row, "href") ?? field(row, "rowHref");
    if (!name || !href) continue;
    const sourceUrl = absoluteUrl(page.url, href);
    if (!isAllowed(sourceUrl, rule, false)) continue;
    const episodes = await parseEpisodes(sourceUrl, rule);
    if (episodes.length === 0) continue;
    sources.push({
      id: stableId(rule.id, sourceUrl),
      animeTitle: query,
      ruleId: rule.id,
      ruleName: rule.name,
      lineName: name.slice(0, 80),
      sourceUrl,
      episodes,
    });
  }
  return sources;
}

async function parseEpisodes(sourceUrl: string, rule: StreamRule): Promise<StreamEpisode[]> {
  const page = await fetchPage(sourceUrl, rule);
  const result = await canx.html.extract({
    html: page.body,
    selector: selector(rule.chapterRoads, "chapterRoads"),
    limit: maximumEpisodes,
    fields: {
      href: { selector: selector(rule.chapterResult, "chapterResult"), value: "attribute", attribute: "href" },
      rowHref: { value: "attribute", attribute: "href" },
      name: {
        selector: selector(rule.chapterName ?? rule.chapterResult, "chapterName"),
        value: "text",
      },
      rowText: { value: "text" },
    },
  });
  const lines = new Map<string, StreamEpisode[]>();
  for (const [index, row] of result.rows.entries()) {
    const href = field(row, "href") ?? field(row, "rowHref");
    if (!href) continue;
    const pageUrl = absoluteUrl(page.url, href);
    if (!isAllowed(pageUrl, rule, false)) continue;
    const line = lineKey(pageUrl);
    const episodes = lines.get(line) ?? [];
    episodes.push({
      id: stableId(rule.id, sourceUrl, href, String(index)),
      name: (field(row, "name") ?? field(row, "rowText") ?? `第 ${index + 1} 集`).slice(0, 100),
      pageUrl,
    });
    lines.set(line, episodes);
  }
  return [...lines.values()].sort((left, right) => right.length - left.length)[0] ?? [];
}

async function resolveRecipe(episode: StreamEpisode, rule: StreamRule): Promise<CanX.ManagedMediaStreamRecipe> {
  const page = await fetchPage(episode.pageUrl, rule);
  let urls: string[] = [];
  if (rule.playURL?.startsWith(scriptPrefix)) {
    urls = [extractScriptValue(page.body, rule.playURL.slice(scriptPrefix.length))];
  } else {
    const expression = rule.playURLs ?? rule.playURL;
    if (expression) {
      const extracted = await canx.html.extract({
        html: page.body,
        selector: selector(expression, "playURL"),
        limit: 8,
        fields: {
          src: { value: "attribute", attribute: "src" },
          href: { value: "attribute", attribute: "href" },
        },
      });
      urls = extracted.rows.map(row => field(row, "src") ?? field(row, "href") ?? "").filter(Boolean);
    }
  }
  for (const value of urls) {
    const url = absoluteUrl(page.url, value);
    if (!isAllowed(url, rule, true)) continue;
    const format = mediaFormat(url);
    if (!format) continue;
    const { headers, headersByHost } = mediaHeaders(rule);
    return {
      url,
      displayName: `${episode.name}.${format === "hls" ? "m3u8" : "mp4"}`,
      format,
      allowedHosts: [hostOf(rule.baseURL), ...(rule.mediaHosts ?? [])],
      headers,
      headersByHost,
    };
  }
  throw new Error("该集未解析出允许的 MP4 或 HLS 地址。");
}

async function fetchPage(url: string, rule: StreamRule): Promise<{ body: string; url: string }> {
  if (!isAllowed(url, rule, false)) throw new Error("页面地址不在规则站点内。");
  return requestText(url, {
    headers: {
      Accept: "text/html,application/xhtml+xml",
      Referer: rule.baseURL,
      ...safeHeaders(rule.headers),
    },
  });
}

function mediaHeaders(rule: StreamRule): {
  headers: Record<string, string>;
  headersByHost: Record<string, Record<string, string>>;
} {
  const headers: Record<string, string> = {
    "User-Agent": userAgent,
    Referer: rule.baseURL,
    ...safeHeaders(rule.headers),
  };
  const headersByHost: Record<string, Record<string, string>> = {};
  for (const [key, value] of Object.entries(rule.mediaHeaders ?? {})) {
    if (typeof value === "string") {
      if (allowedHeaderNames.has(key.toLowerCase())) headers[key] = value;
      continue;
    }
    const host = key.toLowerCase();
    if ([hostOf(rule.baseURL), ...(rule.mediaHosts ?? [])].some(allowed => host === allowed)) {
      headersByHost[host] = safeHeaders(value);
    }
  }
  return { headers, headersByHost };
}

function safeHeaders(value: Record<string, string> | undefined): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [name, content] of Object.entries(value ?? {})) {
    if (allowedHeaderNames.has(name.toLowerCase()) && !/[\r\n]/.test(content)) result[name] = content.slice(0, 4096);
  }
  return result;
}

function validateRule(value: unknown, index: number): StreamRule {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("规则必须是 JSON 对象。");
  const input = value as Partial<StreamRule>;
  const required = [
    "name", "baseURL", "searchURL", "searchList", "searchName", "searchResult", "chapterRoads", "chapterResult",
  ] as const;
  for (const key of required) if (!text(input[key], 2000)) throw new Error(`规则缺少 ${key}。`);
  const baseHost = hostOf(input.baseURL!);
  if (!authorizedHost(baseHost)) throw new Error(`规则站点 ${baseHost || "(无效)"} 未在插件 Manifest 中授权。`);
  const mediaHosts = (input.mediaHosts ?? []).map(value => value.toLowerCase()).filter(authorizedHost).slice(0, 10);
  for (const key of ["searchList", "searchName", "searchResult", "chapterRoads", "chapterResult"] as const) {
    selector(input[key]!, key);
  }
  return {
    id: text(input.id, 80) ?? `rule-${index + 1}`,
    name: text(input.name, 100)!,
    enabled: input.enabled !== false,
    baseURL: input.baseURL!.replace(/\/$/, ""),
    searchURL: input.searchURL!,
    searchList: input.searchList!,
    searchName: input.searchName!,
    searchResult: input.searchResult!,
    chapterRoads: input.chapterRoads!,
    chapterResult: input.chapterResult!,
    chapterName: text(input.chapterName, 2000),
    playURL: text(input.playURL, 2000),
    playURLs: text(input.playURLs, 2000),
    mediaHosts,
    headers: safeHeaders(input.headers),
    mediaHeaders: sanitizeHostHeaders(input.mediaHeaders, [baseHost, ...mediaHosts]),
    useWebview: input.useWebview === true,
  };
}

function sanitizeHostHeaders(
  value: Record<string, string | Record<string, string>> | undefined,
  hosts: readonly string[],
): Record<string, string | Record<string, string>> {
  const result: Record<string, string | Record<string, string>> = {};
  for (const [key, headers] of Object.entries(value ?? {})) {
    if (typeof headers === "string") {
      if (allowedHeaderNames.has(key.toLowerCase())) result[key] = headers;
    } else if (hosts.includes(key.toLowerCase())) {
      result[key.toLowerCase()] = safeHeaders(headers);
    }
  }
  return result;
}

function authorizedHost(host: string): boolean {
  return ["dm1.xfdm.pro", "xfvod.pro", "moedot.net", "pan.wo.cn"].some(scope => hostMatches(host, scope));
}

function isAllowed(url: string, rule: StreamRule, media: boolean): boolean {
  const host = hostOf(url);
  if (!host) return false;
  const hosts = [hostOf(rule.baseURL), ...(media ? rule.mediaHosts ?? [] : [])];
  return hosts.some(allowed => hostMatches(host, allowed));
}

function mediaFormat(url: string): "hls" | "file" | undefined {
  const path = url.split(/[?#]/)[0].toLowerCase();
  if (path.endsWith(".m3u8")) return "hls";
  if (path.endsWith(".mp4")) return "file";
  return undefined;
}

function lineKey(url: string): string {
  const path = url.replace(/^https?:\/\/[^/]+/i, "").split(/[?#]/)[0].split("/").filter(Boolean);
  const watch = path.indexOf("watch");
  return watch >= 0 && path.length > watch + 2 ? `line:${path[watch + 2]}` : "default";
}

function field(row: Readonly<Record<string, string | null | readonly (string | null)[]>>, key: string): string | undefined {
  const value = row[key];
  return typeof value === "string" ? text(value) : undefined;
}

function selector(value: string, field: string): string {
  const input = value.replace(/\/text\(\)\s*$/, "").trim();
  if (!input.startsWith("//")) return input;
  const parts = input.split(/(\/\/|\/)/).filter(Boolean);
  const output: string[] = [];
  let combinator = "";
  for (const part of parts) {
    if (part === "//") { combinator = " "; continue; }
    if (part === "/") { combinator = " > "; continue; }
    const match = /^([a-z*][\w-]*)(?:\[(.+)\])?$/i.exec(part);
    if (!match) throw new Error(`${field} 含不支持的 XPath 片段：${part}`);
    let css = match[1];
    const predicate = match[2];
    if (predicate) {
      if (/^\d+$/.test(predicate)) css += `:nth-of-type(${predicate})`;
      else {
        const equals = /^@([\w-]+)\s*=\s*(['"])(.*?)\2$/.exec(predicate);
        const contains = /^contains\(\s*@([\w-]+)\s*,\s*(['"])(.*?)\2\s*\)$/.exec(predicate);
        const exists = /^@([\w-]+)$/.exec(predicate);
        if (equals) css += `[${equals[1]}="${escapeCss(equals[3])}"]`;
        else if (contains) css += `[${contains[1]}*="${escapeCss(contains[3])}"]`;
        else if (exists) css += `[${exists[1]}]`;
        else throw new Error(`${field} 含不支持的 XPath 谓词：${predicate}`);
      }
    }
    output.push(`${output.length ? combinator || " > " : ""}${css}`);
    combinator = " > ";
  }
  return output.join("");
}

function escapeCss(value: string): string {
  return value.replace(/["\\\n\r\f]/g, character => `\\${character}`);
}

function extractScriptValue(html: string, expression: string): string {
  const [variable, ...path] = expression.split(".").map(part => part.trim()).filter(Boolean);
  if (!variable || path.length === 0) throw new Error("script 表达式格式无效。");
  const start = html.indexOf(`${variable}=`);
  const brace = start < 0 ? -1 : html.indexOf("{", start);
  if (brace < 0) throw new Error(`播放页未找到 ${variable}。`);
  let depth = 0;
  let quoted = false;
  let escaped = false;
  let end = -1;
  for (let index = brace; index < html.length; index++) {
    const character = html[index];
    if (escaped) { escaped = false; continue; }
    if (character === "\\") { escaped = true; continue; }
    if (character === "\"") { quoted = !quoted; continue; }
    if (quoted) continue;
    if (character === "{") depth++;
    else if (character === "}" && --depth === 0) { end = index + 1; break; }
  }
  if (end < 0) throw new Error(`播放页中的 ${variable} 不是完整 JSON。`);
  const root = JSON.parse(html.slice(brace, end)) as Record<string, unknown>;
  let value: unknown = root;
  for (const key of path) value = value && typeof value === "object" ? (value as Record<string, unknown>)[key] : undefined;
  if (typeof value !== "string" || !value) throw new Error(`${variable} 缺少 ${path.join(".")}。`);
  const encrypt = Number(root.encrypt) || 0;
  if (encrypt === 1) return decodeURIComponent(value);
  if (encrypt === 2) return decodeURIComponent(decodeBase64(value));
  return value;
}

function decodeBase64(value: string): string {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  const clean = value.replace(/[^A-Za-z0-9+/=]/g, "");
  let bytes = "";
  for (let index = 0; index < clean.length; index += 4) {
    const a = alphabet.indexOf(clean[index]);
    const b = alphabet.indexOf(clean[index + 1]);
    const c = clean[index + 2] === "=" ? -1 : alphabet.indexOf(clean[index + 2]);
    const d = clean[index + 3] === "=" ? -1 : alphabet.indexOf(clean[index + 3]);
    bytes += `%${((a << 2) | (b >> 4)).toString(16).padStart(2, "0")}`;
    if (c >= 0) bytes += `%${(((b & 15) << 4) | (c >> 2)).toString(16).padStart(2, "0")}`;
    if (d >= 0) bytes += `%${(((c & 3) << 6) | d).toString(16).padStart(2, "0")}`;
  }
  return decodeURIComponent(bytes);
}
