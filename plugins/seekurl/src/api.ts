// SPDX-License-Identifier: GPL-3.0-or-later

export interface SeekEpisode {
  readonly label: string;
  readonly token: string;
}

export interface SeekResource {
  readonly title: string;
  readonly year: string;
  readonly resolution: string;
  readonly source: string;
  readonly episodes: readonly SeekEpisode[];
}

export const siteOrigin = "https://xiuyun.org";
export const userAgent = "Mozilla/5.0 CanX-SEEKURL/1.0";
const maximumApiBytes = 2 * 1024 * 1024;

export async function searchResources(query: string): Promise<readonly SeekResource[]> {
  const keyword = query.trim();
  if (!keyword || keyword.length > 200) throw new Error("请填写 1–200 个字符的搜索关键词。");
  const response = await post("/index/seek/get", { kw: keyword });
  if (response.code !== 1) {
    const message = text(response.msg, 300);
    if (response.code === 0 && /没搜索到|未搜索到|没有找到|未找到|暂无.*结果|无相关结果/.test(message)) return [];
    throw new Error(message || "SEEKURL 搜索失败，请稍后重试。");
  }
  if (!Array.isArray(response.data)) throw new Error("SEEKURL 搜索结果格式无效。");
  const resources: SeekResource[] = [];
  let remainingEpisodes = 4000;
  for (const value of response.data.slice(0, 200)) {
    const row = object(value);
    const title = text(row.title, 600);
    if (!title || typeof row.urls !== "string") continue;
    const episodes: SeekEpisode[] = [];
    const seen = new Set<string>();
    for (const entry of row.urls.split("#")) {
      const separator = entry.indexOf("$");
      if (separator < 0) continue;
      const token = entry.slice(separator + 1).trim();
      if (!token || token.length > 8192 || seen.has(token)) continue;
      seen.add(token);
      episodes.push({ label: text(entry.slice(0, separator), 150) || `第 ${episodes.length + 1} 集`, token });
      if (episodes.length >= Math.min(500, remainingEpisodes)) break;
    }
    if (episodes.length === 0) continue;
    resources.push({
      title,
      year: text(row.year, 20),
      resolution: text(row.resolution, 80),
      source: text(row.source, 80),
      episodes,
    });
    remainingEpisodes -= episodes.length;
    if (remainingEpisodes <= 0) break;
  }
  return resources;
}

export async function resolveEpisode(token: string): Promise<string> {
  const response = await post("/index/Seek/getm3u8", { token });
  if (response.code !== 1) {
    throw new Error(text(response.msg, 300) || "播放链接已过期或解析失败，请重新搜索后再试。");
  }
  const url = text(object(response.data).url, 8192);
  if (!url) throw new Error("SEEKURL 未返回播放地址，请换一个资源或重新搜索。");
  return url;
}

async function post(path: string, body: Record<string, string>): Promise<Record<string, unknown>> {
  const response = await canx.http.request({
    url: siteOrigin + path,
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Accept": "application/json",
      "X-Requested-With": "XMLHttpRequest",
      "Referer": `${siteOrigin}/?plugin=seek`,
      "User-Agent": userAgent,
    },
    body: JSON.stringify(body),
    maximumResponseBytes: maximumApiBytes,
  });
  if (response.status < 200 || response.status >= 300) throw new Error(`SEEKURL 接口返回 HTTP ${response.status}，请稍后重试。`);
  if (response.bodyEncoding !== "text" || response.truncated) throw new Error("SEEKURL 接口响应不完整。");
  try {
    const payload: unknown = JSON.parse(response.body);
    if (!payload || typeof payload !== "object" || Array.isArray(payload)) throw new Error();
    return payload as Record<string, unknown>;
  } catch {
    throw new Error("SEEKURL 接口未返回有效 JSON，请稍后重试。");
  }
}

function object(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

function text(value: unknown, maximum: number): string {
  return typeof value === "string" || typeof value === "number" ? String(value).trim().slice(0, maximum) : "";
}
