// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from anime-find, Copyright (c) 2026 cocofhu, MIT.

import {
  absoluteUrl,
  errorMessage,
  mergeCards,
  requestJson,
  requestText,
  text,
} from "./common.js";
import type {
  AnimeCard,
  AnimeDetail,
  PluginConfig,
  SearchResult,
  SourceError,
  SourceId,
  Subgroup,
  TorrentItem,
} from "./types.js";

type HtmlValue = string | null | readonly (string | null)[];
type HtmlRow = Readonly<Record<string, HtmlValue>>;

interface AniBtList {
  readonly data?: {
    readonly byWeekday?: readonly {
      readonly animes?: readonly {
        readonly animeId?: string;
        readonly bgmId?: string | number;
        readonly cover?: string;
        readonly rating?: number;
        readonly format?: string;
        readonly rssReleaseCount?: number;
        readonly title?: { readonly chinese?: string; readonly primary?: string; readonly english?: string };
      }[];
    }[];
  };
}

interface AniBtGroups {
  readonly data?: {
    readonly groups?: readonly {
      readonly name?: string;
      readonly slug?: string;
      readonly lastUpdatedAt?: number;
      readonly items?: readonly {
        readonly title?: string;
        readonly magnet?: string;
        readonly size?: number;
        readonly formatSize?: string;
        readonly publishedAt?: number;
        readonly resolution?: string;
      }[];
    }[];
  };
}

interface GardenResource {
  readonly id?: number | string;
  readonly title?: string;
  readonly magnet?: string;
  readonly size?: number;
  readonly createdAt?: string;
  readonly href?: string;
  readonly subjectId?: number | string;
  readonly fansub?: { readonly id?: number | string; readonly name?: string } | null;
}

interface GardenWrap {
  readonly resources?: readonly GardenResource[];
}

interface BangumiSubject {
  readonly id?: number;
  readonly name?: string;
  readonly name_cn?: string;
  readonly summary?: string;
  readonly date?: string;
  readonly platform?: string;
  readonly eps?: number;
  readonly total_episodes?: number;
  readonly images?: { readonly small?: string; readonly grid?: string; readonly common?: string; readonly medium?: string };
  readonly rating?: { readonly score?: number; readonly total?: number };
  readonly tags?: readonly { readonly name?: string }[];
}

interface BangumiSearch {
  readonly data?: readonly BangumiSubject[];
}

interface BangumiComments {
  readonly data?: readonly {
    readonly comment?: string;
    readonly rate?: number;
    readonly updatedAt?: number | string;
    readonly user?: { readonly nickname?: string };
  }[];
}

export async function searchAnime(
  query: string,
  config: PluginConfig,
  requestedLimit = config.maxResults,
  offset = 0,
): Promise<SearchResult> {
  const normalizedQuery = query.trim();
  if (!normalizedQuery) throw new Error("搜索关键词不能为空。");
  const season = resolveSeason(normalizedQuery);
  const errors: SourceError[] = [];
  const jobs = config.sources.map(async source => {
    try {
      if (source === "mikan") return await searchMikan(normalizedQuery, config, season);
      if (source === "anibt") return await searchAniBt(season ? "" : normalizedQuery, config, season);
      if (season) return [];
      return await searchGarden(normalizedQuery, config);
    } catch (error) {
      errors.push({ source, message: errorMessage(error) });
      return [];
    }
  });
  const merged = mergeCards(await Promise.all(jobs));
  const start = Math.max(0, Math.trunc(offset));
  const limit = Math.max(1, Math.min(50, Math.trunc(requestedLimit)));
  const items = merged.slice(start, start + limit);
  await enrichWithBangumi(items.slice(0, 12), config);
  return {
    query: normalizedQuery,
    items,
    errors,
    total: merged.length,
    offset: start,
    hasMore: start + items.length < merged.length,
  };
}

export async function detailAnime(
  id: string,
  config: PluginConfig,
  hint?: Pick<AnimeCard, "title" | "refs" | "bgmId" | "cover">,
): Promise<AnimeDetail> {
  const refs = { ...(hint?.refs ?? {}) };
  const separator = id.indexOf(":");
  const source = separator > 0 ? id.slice(0, separator) as SourceId : undefined;
  const key = separator > 0 ? id.slice(separator + 1) : id;
  if (source === "mikan" || source === "anibt" || source === "garden") refs[source] ||= key;
  const groups: Subgroup[] = [];
  const sources: SourceId[] = [];
  let title = hint?.title ?? id;
  let cover = hint?.cover;
  let pageUrl: string | undefined;
  let bgmId = hint?.bgmId;
  const jobs: Promise<void>[] = [];
  if (refs.mikan) jobs.push(detailMikan(refs.mikan, config).then(result => {
    title = result.title || title;
    cover ||= result.cover;
    pageUrl ||= result.pageUrl;
    bgmId ||= result.bgmId;
    sources.push("mikan");
    groups.push(...result.groups);
  }).catch(() => undefined));
  if (refs.anibt) jobs.push(detailAniBt(refs.anibt, config).then(result => {
    bgmId ||= refs.anibt;
    sources.push("anibt");
    groups.push(...result);
  }).catch(() => undefined));
  if (refs.garden) jobs.push(detailGarden(refs.garden, config).then(result => {
    sources.push("garden");
    groups.push(...result);
  }).catch(() => undefined));
  await Promise.all(jobs);
  if (groups.length === 0 && hint?.title) {
    try {
      const fallback = await detailGarden(`title:${hint.title}`, config);
      if (fallback.length) {
        sources.push("garden");
        groups.push(...fallback);
      }
    } catch { /* A failed fallback must not hide metadata from other sources. */ }
  }

  let summary: string | undefined;
  let nameOrig: string | undefined;
  let score: number | undefined;
  let ratingCount: number | undefined;
  let tags: string[] | undefined;
  let comments: AnimeDetail["comments"];
  if (bgmId && /^\d+$/.test(bgmId)) {
    try {
      const [subject, commentResponse] = await Promise.all([
        getBangumiSubject(bgmId),
        requestJson<BangumiComments>(`https://next.bgm.tv/p1/subjects/${bgmId}/comments?limit=5`)
          .catch(() => ({ data: [] })),
      ]);
      title = text(subject.name_cn) ?? text(subject.name) ?? title;
      nameOrig = text(subject.name);
      summary = text(subject.summary, 4000);
      score = positive(subject.rating?.score);
      ratingCount = positive(subject.rating?.total);
      tags = subject.tags?.flatMap(tag => text(tag.name, 40) ?? []).slice(0, 8);
      comments = (commentResponse.data ?? []).flatMap(item => {
        const comment = text(item.comment, 1000);
        if (!comment) return [];
        return [{
          nickname: text(item.user?.nickname, 80) ?? "Bangumi 用户",
          comment,
          rate: positive(item.rate),
          updatedAt: typeof item.updatedAt === "number" ? dateText(item.updatedAt) : text(item.updatedAt, 80),
        }];
      });
      cover ||= subject.images?.common ?? subject.images?.medium ?? subject.images?.grid ?? subject.images?.small;
      pageUrl ||= `https://bgm.tv/subject/${bgmId}`;
    } catch { /* Resource results remain useful when Bangumi is unavailable. */ }
  }
  return {
    id,
    title,
    cover,
    pageUrl,
    bgmId,
    summary,
    nameOrig,
    score,
    ratingCount,
    tags,
    comments,
    sources: [...new Set(sources)],
    groups: groups.map(group => ({
      ...group,
      items: group.items.map(item => enrichRelease(item, title)),
    })),
  };
}

async function searchMikan(
  query: string,
  config: PluginConfig,
  season?: { year: number; name: string },
): Promise<AnimeCard[]> {
  const host = config.mikanHost.replace(/\/$/, "");
  const url = season
    ? `${host}/Home/BangumiCoverFlowByDayOfWeek?year=${season.year}&seasonStr=${encodeURIComponent(season.name)}`
    : `${host}/Home/Search?searchstr=${encodeURIComponent(query)}`;
  const { body } = await requestText(url, {
    maximumResponseBytes: 512 * 1024,
    truncateResponse: true,
  });
  let items = await parseMikanSearch(body, host);
  if (season && items.length === 0) {
    items = await parseMikanSearch((await requestText(`${host}/`, {
      maximumResponseBytes: 512 * 1024,
      truncateResponse: true,
    })).body, host);
  }
  return season
    ? items.map(item => ({ ...item, season: item.season ?? `${season.year} ${season.name}` }))
    : items;
}

async function parseMikanSearch(body: string, host: string): Promise<AnimeCard[]> {
  const extracted = await canx.html.extract({
    html: body,
    selector: "ul.an-ul li",
    limit: 100,
    fields: {
      href: { selector: "a", value: "attribute", attribute: "href" },
      titleAttribute: { selector: ".an-text", value: "attribute", attribute: "title" },
      linkTitle: { selector: "a", value: "attribute", attribute: "title" },
      titleText: { selector: ".an-text", value: "text" },
      rowText: { value: "text" },
      cover: { selector: "[data-src]", value: "attribute", attribute: "data-src" },
    },
  });
  const seen = new Set<string>();
  return extracted.rows.flatMap(row => {
    const href = cell(row, "href") ?? "";
    const title = cell(row, "titleAttribute") ?? cell(row, "linkTitle") ??
      cell(row, "titleText") ?? cell(row, "rowText");
    if (!title) return [];
    const pageUrl = absoluteUrl(host + "/", href);
    const mikanId = /\/(\d+)\/?(?:[?#].*)?$/.exec(href)?.[1];
    const id = `mikan:${mikanId ?? pageUrl}`;
    if (seen.has(id)) return [];
    seen.add(id);
    // Mikan's query string selects its small WebP thumbnail. Keeping it avoids
    // downloading the much larger original cover into the inline UI payload.
    const cover = absoluteUrl(host + "/", cell(row, "cover"));
    return [{
      id,
      title,
      cover: cover || undefined,
      pageUrl,
      format: "tv",
      sources: ["mikan" as const],
      refs: mikanId ? { mikan: mikanId } : {},
    }];
  });
}

async function detailMikan(
  mikanId: string,
  config: PluginConfig,
): Promise<{ title: string; cover?: string; pageUrl: string; bgmId?: string; groups: Subgroup[] }> {
  const host = config.mikanHost.replace(/\/$/, "");
  const pageUrl = `${host}/Home/Bangumi/${encodeURIComponent(mikanId)}`;
  const { body } = await requestText(pageUrl);
  const meta = await canx.html.extract({
    html: body,
    selector: "body",
    limit: 1,
    fields: {
      title: { selector: ".bangumi-title", value: "text" },
      poster: { selector: ".bangumi-poster", value: "attribute", attribute: "style" },
      bangumi: { selector: "a[href*='bgm.tv/subject/']", value: "attribute", attribute: "href" },
    },
  });
  const metaRow = meta.rows[0] ?? {};
  const title = cell(metaRow, "title") ?? `Mikan ${mikanId}`;
  const poster = cell(metaRow, "poster") ?? "";
  const coverPath = /url\(['"]?([^'")]+)['"]?\)/i.exec(poster)?.[1]?.split("?")[0];
  const bangumi = cell(metaRow, "bangumi") ?? "";
  const bgmId = /bgm\.tv\/subject\/(\d+)/i.exec(bangumi)?.[1];
  const groupRows = await canx.html.extract({
    html: body,
    selector: ".leftbar-item",
    limit: 100,
    fields: {
      label: { selector: ".subgroup-name", value: "text" },
      anchor: { selector: ".subgroup-name", value: "attribute", attribute: "data-anchor" },
      updateDay: { selector: ".date", value: "text" },
    },
  });
  const groups: Subgroup[] = [];
  for (const row of groupRows.rows) {
    const label = cell(row, "label");
    const anchor = (cell(row, "anchor") ?? "").replace(/^#/, "");
    if (!label || !/^[A-Za-z0-9_-]{1,100}$/.test(anchor)) continue;
    const rows = (await extractMikanReleaseRows(body, `[id="${anchor}"] + div.episode-table tbody > tr, [id="${anchor}"] + table tbody > tr`))
      .map(item => ({ ...item, torrent: absoluteUrl(host + "/", item.torrent) || undefined }));
    const rss = await canx.html.extract({
      html: body,
      selector: `[id="${anchor}"]`,
      limit: 1,
      fields: { href: { selector: "a.mikan-rss", value: "attribute", attribute: "href" } },
    });
    groups.push({
      label,
      source: "mikan",
      updateDay: cell(row, "updateDay"),
      rss: absoluteUrl(host + "/", cell(rss.rows[0] ?? {}, "href")) || undefined,
      items: rows,
    });
  }
  if (groups.length === 0) {
    const items = (await extractMikanReleaseRows(body, "div.episode-table tbody > tr, table tbody > tr"))
      .map(item => ({ ...item, torrent: absoluteUrl(host + "/", item.torrent) || undefined }));
    if (items.length) groups.push({ label: "Mikan", source: "mikan", items });
  }
  return {
    title,
    cover: absoluteUrl(host + "/", coverPath) || undefined,
    pageUrl,
    bgmId,
    groups,
  };
}

async function extractMikanReleaseRows(html: string, selector: string): Promise<TorrentItem[]> {
  const result = await canx.html.extract({
    html,
    selector,
    limit: 500,
    fields: {
      magnet: { selector: "[data-magnet]", value: "attribute", attribute: "data-magnet" },
      clipboard: { selector: "[data-clipboard-text]", value: "attribute", attribute: "data-clipboard-text" },
      title: { selector: "td:nth-child(2) a, a", value: "text" },
      size: { selector: "td:nth-child(3)", value: "text" },
      createdAt: { selector: "td:nth-child(4)", value: "text" },
      torrent: { selector: "a[href*='Download'], a[href$='.torrent']", value: "attribute", attribute: "href" },
    },
  });
  return result.rows.flatMap(row => {
    const title = cell(row, "title");
    const magnet = cell(row, "magnet") ?? cell(row, "clipboard");
    if (!title && !magnet) return [];
    return [{
      title: title ?? magnet!,
      magnet,
      torrent: cell(row, "torrent"),
      size: cell(row, "size"),
      createdAt: cell(row, "createdAt"),
    }];
  });
}

async function searchAniBt(
  query: string,
  config: PluginConfig,
  season?: { year: number; name: string },
): Promise<AnimeCard[]> {
  const host = config.anibtHost.replace(/\/$/, "");
  const seasonQuery = season ? `&season=${encodeURIComponent(`${season.year}${season.name}`)}` : "";
  const payload = await requestJson<AniBtList>(
    `${host}/api/seasons/anime?query=${encodeURIComponent(query)}${seasonQuery}`,
  );
  return (payload.data?.byWeekday ?? []).flatMap(day => day.animes ?? []).flatMap(item => {
    const title = text(item.title?.chinese) ?? text(item.title?.primary) ?? text(item.title?.english);
    if (!title) return [];
    const bgmId = item.bgmId === undefined ? undefined : String(item.bgmId);
    const reference = bgmId ?? text(item.animeId) ?? title;
    return [{
      id: `anibt:${reference}`,
      title,
      nameOrig: [item.title?.primary, item.title?.english].map(value => text(value)).find(value => value && value !== title),
      bgmId,
      cover: text(item.cover, 1000),
      score: positive(item.rating),
      format: text(item.format, 40) ?? "tv",
      resourceCount: positive(item.rssReleaseCount),
      sources: ["anibt" as const],
      refs: { anibt: reference },
    }];
  });
}

async function detailAniBt(reference: string, config: PluginConfig): Promise<Subgroup[]> {
  const host = config.anibtHost.replace(/\/$/, "");
  const payload = await requestJson<AniBtGroups>(
    `${host}/api/anime/groups?bgmId=${encodeURIComponent(reference)}`,
  );
  return (payload.data?.groups ?? []).map(group => ({
    label: text(group.name, 160) ?? "未知字幕组",
    source: "anibt" as const,
    updateDay: group.lastUpdatedAt ? dateText(group.lastUpdatedAt) : undefined,
    rss: group.slug
      ? `${host}/rss/anime.xml?bgmId=${encodeURIComponent(reference)}&groupSlug=${encodeURIComponent(group.slug)}`
      : undefined,
    items: (group.items ?? []).flatMap(item => {
      const title = text(item.title, 1000);
      if (!title) return [];
      return [{
        title,
        magnet: text(item.magnet, 8192),
        size: text(item.formatSize, 80) ?? (item.size ? formatSize(item.size) : undefined),
        createdAt: item.publishedAt ? dateText(item.publishedAt) : undefined,
        tags: text(item.resolution, 40) ? [text(item.resolution, 40)!] : undefined,
      }];
    }),
  }));
}

async function searchGarden(query: string, config: PluginConfig): Promise<AnimeCard[]> {
  const host = config.gardenHost.replace(/\/$/, "");
  const payload = await requestJson<GardenWrap>(
    `${host}/resources?keyword=${encodeURIComponent(query)}&pageSize=80&duplicate=false`,
  );
  const groups = new Map<string, { title: string; count: number; fansub?: string }>();
  for (const resource of payload.resources ?? []) {
    const subject = resource.subjectId === undefined
      ? `title:${seriesTitle(resource.title ?? "", query)}`
      : String(resource.subjectId);
    if (!subject || subject === "title:") continue;
    const current = groups.get(subject);
    if (current) {
      current.count++;
      current.fansub ||= text(resource.fansub?.name);
    } else {
      groups.set(subject, {
        title: seriesTitle(resource.title ?? subject, query),
        count: 1,
        fansub: text(resource.fansub?.name),
      });
    }
  }
  return [...groups.entries()].map(([reference, group]) => ({
    id: `garden:${reference}`,
    title: group.title,
    subgroup: group.fansub,
    resourceCount: group.count,
    bgmId: /^\d+$/.test(reference) ? reference : undefined,
    format: "tv",
    sources: ["garden"],
    refs: { garden: reference },
  }));
}

async function detailGarden(reference: string, config: PluginConfig): Promise<Subgroup[]> {
  const host = config.gardenHost.replace(/\/$/, "");
  const key = reference.replace(/^title:/, "");
  const url = /^\d+$/.test(reference)
    ? `${host}/resources?subject=${encodeURIComponent(reference)}&pageSize=200&duplicate=false`
    : `${host}/resources?keyword=${encodeURIComponent(key)}&pageSize=200&duplicate=false`;
  const payload = await requestJson<GardenWrap>(url);
  const groups = new Map<string, Subgroup>();
  for (const resource of payload.resources ?? []) {
    const name = text(resource.fansub?.name, 160) ?? "未知字幕组";
    const groupKey = String(resource.fansub?.id ?? name);
    const group = groups.get(groupKey) ?? { label: name, source: "garden", items: [] };
    const title = text(resource.title, 1000);
    if (title) group.items.push({
      title,
      magnet: text(resource.magnet, 8192),
      torrent: absoluteUrl(host + "/", text(resource.href, 8192)) || undefined,
      size: resource.size ? formatSize(resource.size) : undefined,
      createdAt: text(resource.createdAt, 80)?.replace("T", " ").slice(0, 16),
    });
    groups.set(groupKey, group);
  }
  return [...groups.values()].filter(group => group.items.length > 0);
}

async function enrichWithBangumi(cards: AnimeCard[], config: PluginConfig): Promise<void> {
  await runLimited(cards, 4, async card => {
    try {
      const subject = card.bgmId
        ? await getBangumiSubject(card.bgmId)
        : await searchBangumiSubject(card.title);
      if (!subject?.id) return;
      card.bgmId = String(subject.id);
      card.nameOrig ||= text(subject.name);
      card.score ??= positive(subject.rating?.score);
      card.ratingCount ??= positive(subject.rating?.total);
      card.tags ||= subject.tags?.flatMap(tag => text(tag.name, 40) ?? []).slice(0, 5);
      card.cover ||= subject.images?.grid ?? subject.images?.common ?? subject.images?.small;
    } catch (error) {
      void config;
      // Search sources remain usable when optional Bangumi enrichment fails.
    }
  });
}

async function searchBangumiSubject(title: string): Promise<BangumiSubject | undefined> {
  const payload = await requestJson<BangumiSearch>("https://api.bgm.tv/v0/search/subjects?limit=1", {
    method: "POST",
    body: JSON.stringify({ keyword: title, sort: "match", filter: { type: [2] } }),
  });
  return payload.data?.[0];
}

async function getBangumiSubject(id: string): Promise<BangumiSubject> {
  return requestJson<BangumiSubject>(`https://api.bgm.tv/v0/subjects/${encodeURIComponent(id)}`);
}

async function runLimited<T>(items: readonly T[], limit: number, action: (item: T) => Promise<void>): Promise<void> {
  let index = 0;
  const worker = async () => {
    while (index < items.length) {
      const current = items[index++];
      await action(current);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
}

function cell(row: HtmlRow, key: string): string | undefined {
  const value = row[key];
  return typeof value === "string" ? text(value) : undefined;
}

function positive(value: unknown): number | undefined {
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? number : undefined;
}

function formatSize(bytes: number): string {
  const units = ["B", "KiB", "MiB", "GiB", "TiB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return unit === 0 ? `${Math.round(value)} ${units[unit]}` : `${value.toFixed(2)} ${units[unit]}`;
}

function dateText(value: number): string {
  const milliseconds = value < 10_000_000_000 ? value * 1000 : value;
  return new Date(milliseconds).toISOString().slice(0, 16).replace("T", " ");
}

function seriesTitle(value: string, query: string): string {
  if (query && value.includes(query)) return query;
  const bracket = [...value.matchAll(/[【\[]([^】\]]+)[】\]]/g)]
    .map(match => match[1].trim())
    .find(item => !/^(1080p|720p|2160p|web-?rip|bdrip|简中|繁中)$/i.test(item));
  return (bracket ?? value.replace(/[【\[][^】\]]+[】\]]/g, " "))
    .replace(/\s*[-–]\s*\d{1,3}(?:\s|$).*$/, "")
    .replace(/\s+/g, " ")
    .trim();
}

function enrichRelease(item: TorrentItem, animeTitle: string): TorrentItem {
  const title = item.title.replace(/\s+/g, " ").trim();
  const episode = title.match(/S\d{1,2}\s*E(\d{1,3}(?:\.\d+)?)/i)?.[1]
    ?? title.match(/第\s*(\d{1,3}(?:\.\d+)?)\s*[话話集]/)?.[1]
    ?? title.match(/[【\[](\d{1,3}(?:\.\d+)?)(?:\s*[vV]\d)?[】\]]/)?.[1];
  const tags = [...(item.tags ?? [])];
  for (const [pattern, label] of [
    [/2160p/i, "2160p"], [/1080p/i, "1080p"], [/720p/i, "720p"],
    [/\b(?:HEVC|x265)\b/i, "HEVC"], [/\b(?:AVC|x264)\b/i, "AVC"],
    [/(?:简体|简中|CHS)/i, "简中"], [/(?:繁体|繁中|CHT|BIG5)/i, "繁中"],
  ] as const) if (pattern.test(title) && !tags.includes(label)) tags.push(label);
  return {
    ...item,
    episode,
    tags,
    displayTitle: episode ? `第 ${episode} 集` : (title.includes(animeTitle) ? animeTitle : title.slice(0, 80)),
  };
}

function resolveSeason(query: string): { year: number; name: string } | undefined {
  const explicit = /(\d{4})\s*年?\s*(春|夏|秋|冬)/.exec(query);
  if (explicit) return { year: Number(explicit[1]), name: explicit[2] };
  const stripped = query
    .replace(/最近|近期|本季|这季|当季|这一季|这个季度|有哪些|有什么|推荐|热门|好看|最新/g, "")
    .replace(/动漫|番剧|动画|新番|番|[?？!！。,.，、\s]/g, "");
  if (stripped.length >= 2) return undefined;
  const shanghai = new Date(Date.now() + 8 * 60 * 60 * 1000);
  const month = shanghai.getUTCMonth() + 1;
  const name = month === 12 || month <= 2 ? "冬" : month <= 5 ? "春" : month <= 8 ? "夏" : "秋";
  return { year: shanghai.getUTCFullYear(), name };
}
