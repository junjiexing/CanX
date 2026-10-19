// SPDX-License-Identifier: GPL-3.0-or-later

import { mountConfiguration } from "./configuration.js";

interface SearchRequest {
  readonly query: string;
  readonly limit: number;
  readonly cursor?: string;
}

interface SearchItem {
  readonly id: string;
  readonly title: string;
  readonly description?: string;
  readonly language?: string;
  readonly format?: string;
}

interface SearchPage {
  readonly items: readonly SearchItem[];
  readonly nextCursor?: string;
  readonly error?: string;
}

interface ResolveRequest {
  readonly id: string;
}

interface SubtitleSource {
  readonly kind: "http";
  readonly uri: string;
  readonly mediaType: "subtitle";
  readonly label: string;
  readonly mimeType: string;
}

interface MediaResolution {
  readonly sources: readonly SubtitleSource[];
  readonly error?: string;
}

class AssrtError extends Error {}

interface AssrtFile {
  readonly f?: unknown;
  readonly url?: unknown;
}

interface AssrtSubtitle {
  readonly id?: unknown;
  readonly native_name?: unknown;
  readonly videoname?: unknown;
  readonly subtype?: unknown;
  readonly release_site?: unknown;
  readonly lang?: unknown;
  readonly filename?: unknown;
  readonly url?: unknown;
  readonly filelist?: unknown;
}

const pageSize = 15;
const apiOrigin = "https://api.assrt.net";
const subtitleFormats: Readonly<Record<string, string>> = {
  srt: "application/x-subrip",
  ass: "text/x-ssa",
  ssa: "text/x-ssa",
  vtt: "text/vtt",
  smi: "application/x-sami",
};

await mountConfiguration();

await canx.services.register<SearchRequest, SearchPage>({
  id: "search",
  contract: "org.canx.subtitle.search",
  contractVersion: "1.0",
  title: "ASSRT 字幕搜索",
  description: "通过 ASSRT API 搜索字幕；结果来自 assrt.net。",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: {
    type: "object",
    additionalProperties: false,
    required: ["query", "limit"],
    properties: {
      query: { type: "string", minLength: 1, maxLength: 500 },
      limit: { type: "integer", minimum: 1, maximum: 50 },
      cursor: { type: "string", pattern: "^(0|[1-9][0-9]{0,6})$" },
    },
  },
  outputSchema: {
    type: "object",
    additionalProperties: false,
    required: ["items"],
    properties: {
      items: {
        type: "array",
        maxItems: pageSize,
        items: {
          type: "object",
          additionalProperties: false,
          required: ["id", "title"],
          properties: {
            id: { type: "string", pattern: "^[1-9][0-9]{0,15}$" },
            title: { type: "string", minLength: 1, maxLength: 1000 },
            description: { type: "string", maxLength: 300 },
            language: { type: "string", maxLength: 32 },
            format: { type: "string", maxLength: 32 },
          },
        },
      },
      nextCursor: { type: "string", pattern: "^[1-9][0-9]{0,6}$" },
      error: { type: "string", minLength: 1, maxLength: 200 },
    },
  },
  async invoke(request) {
    try {
      return await search(request);
    } catch (error) {
      return { items: [], error: publicError(error, "ASSRT 搜索失败，请稍后重试。") };
    }
  },
});

async function search(request: SearchRequest): Promise<SearchPage> {
  const query = request.query.trim();
  if ([...query].length < 3) return { items: [] };
  const position = parsePosition(request.cursor);
  const count = Math.min(pageSize, request.limit);
  const payload = await apiGet("/v1/sub/search", {
    q: query,
    pos: String(position),
    cnt: String(count),
  });
  const rows = subtitleRows(payload, true);
  const seen = new Set<string>();
  const items: SearchItem[] = [];
  for (const row of rows) {
    const item = toSearchItem(row);
    if (!item || seen.has(item.id)) continue;
    seen.add(item.id);
    items.push(item);
    if (items.length === count) break;
  }
  const next = position + count;
  return {
    items,
    ...(rows.length === count && next <= 9999999 ? { nextCursor: String(next) } : {}),
  };
}

await canx.services.register<ResolveRequest, MediaResolution>({
  id: "resolve",
  contract: "org.canx.media.resolve",
  contractVersion: "1.0",
  title: "ASSRT 字幕地址解析",
  description: "在播放前获取 ASSRT 的临时字幕下载地址。",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: {
    type: "object",
    additionalProperties: false,
    required: ["id"],
    properties: { id: { type: "string", pattern: "^[1-9][0-9]{0,15}$" } },
  },
  outputSchema: {
    type: "object",
    additionalProperties: false,
    required: ["sources"],
    properties: {
      sources: {
        type: "array",
        maxItems: 32,
        items: {
          type: "object",
          additionalProperties: false,
          required: ["kind", "uri", "mediaType", "label", "mimeType"],
          properties: {
            kind: { const: "http" },
            uri: { type: "string", pattern: "^https?://", maxLength: 8192 },
            mediaType: { const: "subtitle" },
            label: { type: "string", minLength: 1, maxLength: 512 },
            mimeType: { type: "string", maxLength: 128 },
          },
        },
      },
      error: { type: "string", minLength: 1, maxLength: 200 },
    },
  },
  async invoke(request) {
    try {
      return await resolve(request);
    } catch (error) {
      return { sources: [], error: publicError(error, "ASSRT 字幕地址解析失败，请稍后重试。") };
    }
  },
});

async function resolve(request: ResolveRequest): Promise<MediaResolution> {
  const payload = await apiGet("/v1/sub/detail", { id: request.id });
  const detail = subtitleRows(payload).find(row => subtitleId(row.id) === request.id);
  if (!detail) throw new AssrtError("ASSRT 未返回所选字幕的详情。");

  const sources: SubtitleSource[] = [];
  const seen = new Set<string>();
  const add = (nameValue: unknown, urlValue: unknown) => {
    const name = safeText(nameValue, 512);
    const format = extension(name);
    if (!format) return;
    const uri = subtitleUrl(urlValue);
    if (!uri || seen.has(uri)) return;
    seen.add(uri);
    sources.push({
      kind: "http",
      uri,
      mediaType: "subtitle",
      label: name ?? `ASSRT #${request.id}`,
      mimeType: subtitleFormats[format],
    });
  };

  add(detail.filename, detail.url);
  if (Array.isArray(detail.filelist)) {
    for (const file of detail.filelist as readonly AssrtFile[]) {
      if (file && typeof file === "object") add(file.f, file.url);
      if (sources.length === 32) break;
    }
  }
  if (sources.length === 0) {
    throw new AssrtError("ASSRT 详情中没有可直接播放的字幕文件（SRT、ASS、SSA、VTT 或 SMI）。");
  }
  return { sources };
}

async function apiGet(path: string, parameters: Readonly<Record<string, string>>): Promise<unknown> {
  let token: string | undefined;
  try {
    token = (await canx.secrets.get("token"))?.trim();
  } catch {
    throw new AssrtError("无法读取 API Token，请检查系统凭据存储。");
  }
  if (!token) throw new AssrtError("请先在 ASSRT 字幕插件设置中填写 API Token。");
  const query = Object.entries(parameters)
    .map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(value)}`)
    .join("&");
  let response: CanX.HttpResponse;
  try {
    response = await canx.http.request({
      url: `${apiOrigin}${path}?${query}`,
      headers: { Authorization: `Bearer ${token}`, Accept: "application/json" },
      maximumResponseBytes: 1024 * 1024,
    });
  } catch {
    throw new AssrtError("无法连接 ASSRT，请检查网络或代理设置。");
  }
  let payload: unknown;
  try {
    payload = JSON.parse(response.body);
  } catch {
    if (response.status !== 200) throw new AssrtError(`ASSRT 请求失败：HTTP ${response.status}。`);
    throw new AssrtError("ASSRT 返回的数据不是有效的 JSON。");
  }
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
    throw new AssrtError("ASSRT 返回的数据格式无效。");
  }
  const result = payload as { status?: unknown; errmsg?: unknown };
  if (result.status !== 0) {
    if (result.status === 20001) throw new AssrtError("ASSRT API Token 无效，请在插件设置中更新。");
    if (result.status === 30900) throw new AssrtError("ASSRT 请求次数已达到配额上限，请稍后重试。");
    if (typeof result.status === "number" && Number.isInteger(result.status)) {
      throw new AssrtError(`ASSRT API 错误 ${result.status}，请稍后重试。`);
    }
    throw new AssrtError("ASSRT 返回的数据缺少有效状态码。");
  }
  if (response.status !== 200) throw new AssrtError(`ASSRT 请求失败：HTTP ${response.status}。`);
  return payload;
}

function subtitleRows(payload: unknown, emptyOnNonArray = false): readonly AssrtSubtitle[] {
  const sub = (payload as { sub?: unknown }).sub;
  if (!sub || typeof sub !== "object" || Array.isArray(sub)) {
    if (emptyOnNonArray) return [];
    throw new AssrtError("ASSRT 返回的数据缺少字幕列表。");
  }
  const rows = (sub as { subs?: unknown }).subs;
  if (rows == null) return [];
  if (!Array.isArray(rows)) {
    // ASSRT sometimes returns a non-array `subs` value for an empty search.
    // Details must still be an array so malformed download data is rejected.
    if (emptyOnNonArray) {
      if (isSubtitleRow(rows) && subtitleId(rows.id)) return [rows];
      if (rows && typeof rows === "object") {
        const entries = Object.entries(rows);
        if (entries.every(([key, value]) => /^(0|[1-9][0-9]*)$/.test(key) && isSubtitleRow(value))) {
          return entries.sort(([left], [right]) => Number(left) - Number(right))
            .map(([, value]) => value as AssrtSubtitle);
        }
      }
      return [];
    }
    throw new AssrtError("ASSRT 返回的字幕列表格式无效。");
  }
  return rows.filter(isSubtitleRow);
}

function isSubtitleRow(value: unknown): value is AssrtSubtitle {
  return Boolean(value && typeof value === "object" && !Array.isArray(value));
}

function toSearchItem(row: AssrtSubtitle): SearchItem | undefined {
  const id = subtitleId(row.id);
  if (!id) return undefined;
  const name = safeText(row.native_name, 450);
  const video = safeText(row.videoname, 450);
  const title = video && name && video !== name ? `${video} · ${name}` : video ?? name ?? `ASSRT #${id}`;
  const lang = row.lang && typeof row.lang === "object" && !Array.isArray(row.lang)
    ? row.lang as { desc?: unknown; langlist?: unknown }
    : undefined;
  const description = [safeText(lang?.desc, 100), safeText(row.release_site, 100)]
    .filter((value): value is string => Boolean(value)).join(" · ");
  const format = subtitleFormat(row.subtype);
  const language = subtitleLanguage(lang?.langlist);
  return {
    id,
    title,
    ...(description ? { description } : {}),
    ...(language ? { language } : {}),
    ...(format ? { format } : {}),
  };
}

function subtitleId(value: unknown): string | undefined {
  if (typeof value === "number" && Number.isSafeInteger(value) && value > 0) return String(value);
  if (typeof value === "string" && /^[1-9][0-9]{0,15}$/.test(value)) return value;
  return undefined;
}

function safeText(value: unknown, max: number): string | undefined {
  if (typeof value !== "string") return undefined;
  const text = value.trim();
  return text ? text.slice(0, max) : undefined;
}

function subtitleLanguage(value: unknown): string | undefined {
  if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
  const flags = value as Readonly<Record<string, unknown>>;
  for (const [flag, language] of [
    ["langchs", "zh"], ["langcht", "zh"], ["langeng", "en"], ["langjpn", "ja"],
    ["langkor", "ko"], ["langfre", "fr"], ["langger", "de"], ["langspa", "es"],
    ["langrus", "ru"], ["langpor", "pt"], ["langita", "it"],
  ]) {
    if (flags[flag] === true) return language;
  }
  return undefined;
}

function subtitleFormat(value: unknown): string | undefined {
  const subtype = safeText(value, 64)?.toLowerCase();
  if (!subtype) return undefined;
  if (subtype.includes("subrip")) return "srt";
  const match = subtype.match(/\b(srt|ass|ssa|vtt|smi)\b/);
  return match?.[1];
}

function extension(name: string | undefined): string | undefined {
  const format = name?.match(/\.([a-z0-9]+)$/i)?.[1]?.toLowerCase();
  return format && Object.prototype.hasOwnProperty.call(subtitleFormats, format) ? format : undefined;
}

function subtitleUrl(value: unknown): string | undefined {
  if (typeof value !== "string" || value.length > 8192 || value.trim() !== value) return undefined;
  // The download hosts in the API response are fixed. Requiring a slash after the
  // hostname also excludes credentials, ports and lookalike domains.
  return /^https?:\/\/file[0-9]+\.assrt\.net\/[^\u0000-\u001f\u007f#\\]*$/i.test(value)
    ? value
    : undefined;
}

function parsePosition(cursor: string | undefined): number {
  if (cursor === undefined) return 0;
  if (!/^(0|[1-9][0-9]{0,6})$/.test(cursor)) throw new AssrtError("ASSRT 分页位置无效。");
  return Number(cursor);
}

function publicError(error: unknown, fallback: string): string {
  return error instanceof AssrtError ? error.message : fallback;
}
