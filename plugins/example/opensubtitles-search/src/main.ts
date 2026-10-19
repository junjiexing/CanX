// SPDX-License-Identifier: GPL-3.0-or-later

import { mountConfiguration } from "./configuration.js";

interface SubtitleFile {
  readonly file_id?: number;
  readonly file_name?: string;
}

interface SubtitleAttributes {
  readonly language?: string;
  readonly release?: string;
  readonly hearing_impaired?: boolean;
  readonly files?: readonly SubtitleFile[];
}

interface SubtitleItem {
  readonly id?: string;
  readonly attributes?: SubtitleAttributes;
}

interface SubtitleSearchResponse {
  readonly data?: readonly SubtitleItem[];
  readonly total_pages?: number;
}

interface SubtitleResolveData {
  readonly fileId: number;
  readonly fileName?: string;
}

interface SubtitleDownloadResponse {
  readonly link?: string;
  readonly file_name?: string;
}

interface SearchRequest {
  readonly query: string;
  readonly limit: number;
  readonly cursor?: string;
}

interface SearchPage {
  readonly items: readonly {
    readonly id: string;
    readonly title: string;
    readonly language?: string;
    readonly format?: string;
    readonly hearingImpaired?: boolean;
    readonly resolveData: SubtitleResolveData;
  }[];
  readonly nextCursor?: string;
  readonly error?: string;
}

interface ResolveRequest {
  readonly id: string;
  readonly data?: Partial<SubtitleResolveData>;
}

interface MediaResolution {
  readonly sources: readonly {
    readonly kind: "http";
    readonly uri: string;
    readonly mediaType: "subtitle";
    readonly label: string;
    readonly mimeType?: string;
  }[];
  readonly error?: string;
}

class OpenSubtitlesError extends Error {}

const userAgent = "CanX OpenSubtitles v1";

await mountConfiguration();

await canx.services.register<SearchRequest, SearchPage>({
  id: "search",
  contract: "org.canx.subtitle.search",
  contractVersion: "1.0",
  title: "OpenSubtitles search",
  description: "Searches subtitle releases through the official OpenSubtitles API.",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: {
    type: "object",
    additionalProperties: false,
    required: ["query", "limit"],
    properties: {
      query: { type: "string", minLength: 1, maxLength: 500 },
      limit: { type: "integer", minimum: 1, maximum: 50 },
      cursor: { type: "string", maxLength: 32 },
    },
  },
  outputSchema: {
    type: "object",
    additionalProperties: false,
    required: ["items"],
    properties: {
      items: {
        type: "array",
        maxItems: 50,
        items: {
          type: "object",
          additionalProperties: false,
          required: ["id", "title", "resolveData"],
          properties: {
            id: { type: "string", minLength: 1, maxLength: 256 },
            title: { type: "string", minLength: 1, maxLength: 1000 },
            language: { type: "string", maxLength: 32 },
            format: { type: "string", maxLength: 32 },
            hearingImpaired: { type: "boolean" },
            resolveData: {
              type: "object",
              additionalProperties: false,
              required: ["fileId"],
              properties: {
                fileId: { type: "integer", minimum: 1 },
                fileName: { type: "string", maxLength: 512 },
              },
            },
          },
        },
      },
      nextCursor: { type: "string", maxLength: 32 },
      error: { type: "string", minLength: 1, maxLength: 200 },
    },
  },
  async invoke(request) {
    try {
      return await search(request);
    } catch (error) {
      return { items: [], error: publicError(error, "OpenSubtitles 搜索失败，请稍后重试。") };
    }
  },
});

async function search(request: SearchRequest): Promise<SearchPage> {
  const apiKey = await requireApiKey();
  const page = parseCursor(request.cursor);
  const url = "https://api.opensubtitles.com/api/v1/subtitles" +
    `?query=${encodeURIComponent(request.query)}` + (page > 1 ? `&page=${page}` : "");
  const response = await requestApi({
    url,
    headers: { "api-key": apiKey, "user-agent": userAgent, accept: "application/json" },
  });
  const payload = parseJson<SubtitleSearchResponse>(response.body);
  if (!Array.isArray(payload.data)) throw new OpenSubtitlesError("OpenSubtitles 返回的字幕列表格式无效。");
  const items = payload.data.flatMap((item: SubtitleItem) => {
    const file = item.attributes?.files?.find(candidate => Number.isSafeInteger(candidate.file_id));
    if (!file?.file_id) return [];
    return [{
      id: item.id ?? String(file.file_id),
      title: item.attributes?.release || file.file_name || `Subtitle ${file.file_id}`,
      language: item.attributes?.language,
      format: extension(file.file_name),
      hearingImpaired: item.attributes?.hearing_impaired,
      resolveData: { fileId: file.file_id, fileName: file.file_name },
    }];
  }).slice(0, Math.max(1, Math.min(50, request.limit)));
  const nextCursor = page < (payload.total_pages ?? page) ? String(page + 1) : undefined;
  return { items, nextCursor };
}

await canx.services.register<ResolveRequest, MediaResolution>({
  id: "resolve",
  contract: "org.canx.media.resolve",
  contractVersion: "1.0",
  title: "OpenSubtitles download resolver",
  description: "Resolves an OpenSubtitles result to a temporary HTTPS subtitle URL.",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: {
    type: "object",
    additionalProperties: false,
    required: ["id", "data"],
    properties: {
      id: { type: "string", minLength: 1, maxLength: 256 },
      data: {
        type: "object",
        additionalProperties: false,
        required: ["fileId"],
        properties: {
          fileId: { type: "integer", minimum: 1 },
          fileName: { type: "string", maxLength: 512 },
        },
      },
    },
  },
  outputSchema: {
    type: "object",
    additionalProperties: false,
    required: ["sources"],
    properties: {
      sources: {
        type: "array",
        maxItems: 1,
        items: {
          type: "object",
          additionalProperties: false,
          required: ["kind", "uri", "mediaType", "label"],
          properties: {
            kind: { const: "http" },
            uri: { type: "string", pattern: "^https://" },
            mediaType: { const: "subtitle" },
            label: { type: "string", minLength: 1 },
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
      return { sources: [], error: publicError(error, "OpenSubtitles 字幕地址解析失败，请稍后重试。") };
    }
  },
});

async function resolve(request: ResolveRequest): Promise<MediaResolution> {
  const data = request.data;
  const fileId = data?.fileId;
  const fileName = data?.fileName;
  if (typeof fileId !== "number" || !Number.isSafeInteger(fileId) || fileId <= 0) {
    throw new OpenSubtitlesError("字幕文件 ID 缺失，请重新搜索。");
  }
  const apiKey = await requireApiKey();
  const response = await requestApi({
    url: "https://api.opensubtitles.com/api/v1/download",
    method: "POST",
    headers: {
      "api-key": apiKey,
      "user-agent": userAgent,
      "content-type": "application/json",
      accept: "application/json",
    },
    body: JSON.stringify({ file_id: fileId }),
  });
  const payload = parseJson<SubtitleDownloadResponse>(response.body);
  if (typeof payload.link !== "string" || !payload.link.startsWith("https://")) {
    throw new OpenSubtitlesError("OpenSubtitles 没有返回可用的字幕下载地址。");
  }
  const descriptor = {
    kind: "http" as const,
    uri: payload.link,
    mediaType: "subtitle" as const,
    label: payload.file_name || fileName || "OpenSubtitles",
    mimeType: subtitleMimeType(payload.file_name || fileName),
  };
  return { sources: [descriptor] };
}

async function requireApiKey(): Promise<string> {
  let apiKey: string | null;
  try {
    apiKey = await canx.secrets.get("api-key");
  } catch {
    throw new OpenSubtitlesError("无法读取 API Key，请检查系统凭据存储。");
  }
  if (!apiKey?.trim()) throw new OpenSubtitlesError("请先在 OpenSubtitles 插件设置中填写 API Key。");
  return apiKey.trim();
}

async function requestApi(request: CanX.HttpRequest): Promise<CanX.HttpResponse> {
  let response: CanX.HttpResponse;
  try {
    response = await canx.http.request(request);
  } catch {
    throw new OpenSubtitlesError("无法连接 OpenSubtitles，请检查网络或代理设置。");
  }
  if (response.status === 401 || response.status === 403) {
    throw new OpenSubtitlesError("API Key 无效或无权访问，请在 OpenSubtitles 插件设置中更新。");
  }
  if (response.status === 429) throw new OpenSubtitlesError("请求次数已达到 OpenSubtitles 配额，请稍后重试。");
  if (response.status !== 200) throw new OpenSubtitlesError(`OpenSubtitles 请求失败：HTTP ${response.status}。`);
  return response;
}

function parseJson<T>(body: string): T {
  try {
    const value: unknown = JSON.parse(body);
    if (value && typeof value === "object" && !Array.isArray(value)) return value as T;
  } catch { /* Report a stable, credential-free message below. */ }
  throw new OpenSubtitlesError("OpenSubtitles 返回的数据格式无效。");
}

function publicError(error: unknown, fallback: string): string {
  return error instanceof OpenSubtitlesError ? error.message : fallback;
}

function parseCursor(cursor: string | undefined): number {
  const page = Number.parseInt(cursor ?? "1", 10);
  return Number.isSafeInteger(page) && page > 0 ? page : 1;
}

function extension(fileName: string | undefined): string | undefined {
  const match = fileName?.match(/\.([A-Za-z0-9]+)$/);
  return match?.[1]?.toLowerCase();
}

function subtitleMimeType(fileName: string | undefined): string | undefined {
  const format = extension(fileName);
  if (format === "vtt") return "text/vtt";
  if (format === "srt") return "application/x-subrip";
  if (format === "ass" || format === "ssa") return "text/x-ssa";
  return undefined;
}
