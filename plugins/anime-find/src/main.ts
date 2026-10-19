// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from anime-find, Copyright (c) 2026 cocofhu, MIT.

export {};

import {
  clamp,
  errorMessage,
  hostOf,
  infoHash,
  parseSize,
  stableId,
  text,
} from "./common.js";
import { detailAnime, searchAnime } from "./sources.js";
import { defaultStreamRules, searchStreams, streamRecipe, validateRules } from "./streaming.js";
import type {
  AnimeCard,
  AnimeDetail,
  PluginConfig,
  SourceId,
  StreamEpisode,
  StreamSource,
  TorrentItem,
} from "./types.js";

type Tone = "muted" | "accent" | "success" | "warning" | "error";
type UiItem = Readonly<Record<string, string | number | boolean | null>>;

interface SearchRequest {
  readonly query: string;
  readonly limit?: number;
  readonly cursor?: string;
  readonly sources?: readonly SourceId[];
}

interface DetailRequest {
  readonly id: string;
}

interface MediaResolveRequest {
  readonly id: string;
}

interface CachedEpisode {
  readonly source: StreamSource;
  readonly episode: StreamEpisode;
}

const configKey = "config.v1";
const maximumCachedItems = 500;
const maximumManagedHandles = 12;
const animeCache = new Map<string, AnimeCard>();
const mediaCache = new Map<string, CachedEpisode>();
const resolvedHandles = new Map<string, CanX.ManagedMediaStreamHandle>();
const downloadItems = new Map<string, TorrentItem>();
const streamItems = new Map<string, CachedEpisode>();

let config = await loadConfig();
let query = "";
let searchOffset = 0;
let searchStatus = "输入番剧名称，或输入“本季”浏览当前季度。";
let searchTone: Tone = "muted";
let searchItems: readonly UiItem[] = [];
let selectedCard: AnimeCard | undefined;
let selectedDetail: AnimeDetail | undefined;
let detailStatus = "";
let detailTone: Tone = "muted";
let resourceStatus = "";
let resourceTone: Tone = "muted";
let onlineStatus = "";
let onlineTone: Tone = "muted";
let resourceItems: readonly UiItem[] = [];
let onlineItems: readonly UiItem[] = [];
let settingsDraft = draftConfig(config);

const page = await canx.ui.page.mount({
  content: rootPage(),
  async onEvent(event) {
    if (event.type === "change" && event.key === "anime-query") {
      query = String(event.value ?? "").trim();
      return;
    }
    if (event.type !== "action") return;
    if (event.action === "search") await runUiSearch(false);
    else if (event.action === "more") await runUiSearch(true);
    else if (event.action === "open-anime" && event.itemId) await openAnime(event.itemId);
    else if (event.action === "back") {
      selectedCard = undefined;
      selectedDetail = undefined;
      await page.render(rootPage());
    } else if (event.action === "download" && event.itemId) await requestDownload(event.itemId);
    else if (event.action === "search-streams") await loadStreams();
    else if (event.action === "play-stream" && event.itemId) await playStream(event.itemId);
  },
});

const configuration = await canx.ui.configuration.mount({
  content: configurationPage(),
  async onOpen() {
    settingsDraft = draftConfig(config);
    await configuration.render(configurationPage());
  },
  async onEvent(event) {
    if (event.type === "change") {
      if (event.key === "source-mikan") settingsDraft.mikan = Boolean(event.value);
      else if (event.key === "source-anibt") settingsDraft.anibt = Boolean(event.value);
      else if (event.key === "source-garden") settingsDraft.garden = Boolean(event.value);
      else if (event.key === "max-results") settingsDraft.maxResults = Number(event.value);
      else if (event.key === "mikan-host") settingsDraft.mikanHost = String(event.value ?? "");
      else if (event.key === "anibt-host") settingsDraft.anibtHost = String(event.value ?? "");
      else if (event.key === "garden-host") settingsDraft.gardenHost = String(event.value ?? "");
      else if (event.key === "stream-enabled") settingsDraft.streamEnabled = Boolean(event.value);
      else if (event.key === "stream-rules") settingsDraft.rules = String(event.value ?? "");
      return;
    }
    if (event.type !== "action") return;
    if (event.action === "reset-settings") {
      settingsDraft = draftConfig(defaultConfig());
      await configuration.render(configurationPage("已恢复默认值，点击保存后生效。", "warning"));
      return;
    }
    if (event.action === "save-settings") {
      try {
        const next = configFromDraft(settingsDraft);
        await canx.storage.set(configKey, next);
        config = next;
        await configuration.render(configurationPage("设置已保存。", "success"));
      } catch (error) {
        await configuration.render(configurationPage(errorMessage(error), "error"));
      }
    }
  },
});

async function registerServices(): Promise<void> {
  await canx.services.register<SearchRequest, unknown>({
    id: "anime-search",
    contract: "org.canx.anime.search",
    contractVersion: "1.0",
    title: "Anime Find 番剧搜索",
    description: "聚合 Mikan、AniBT、AnimeGarden，并用 Bangumi 补充元数据；不返回下载或播放地址。",
    risk: "network",
    audiences: ["plugin", "ai"],
    inputSchema: searchInputSchema,
    outputSchema: animeSearchOutputSchema,
    async invoke(request) {
      const result = await searchAnime(
        request.query,
        withSources(config, request.sources),
        clamp(request.limit, 1, 50, config.maxResults),
        cursor(request.cursor),
      );
      cacheAnime(result.items);
      return {
        items: result.items.map(card => ({
          id: card.id,
          title: card.title,
          description: animeDescription(card),
          score: card.score,
          format: card.format,
          thumbnailUrl: card.cover,
          resourceCount: card.resourceCount,
          sources: card.sources,
        })),
        nextCursor: result.hasMore ? String(result.offset + result.items.length) : undefined,
        total: result.total,
        errors: result.errors,
      };
    },
  });

  await canx.services.register<DetailRequest, unknown>({
    id: "anime-detail",
    contract: "org.canx.anime.detail",
    contractVersion: "1.0",
    title: "Anime Find 番剧详情",
    description: "返回番剧介绍和可用资源摘要，但不把 Magnet、Torrent 或媒体地址暴露给调用者。",
    risk: "network",
    audiences: ["plugin", "ai"],
    inputSchema: detailInputSchema,
    outputSchema: animeDetailOutputSchema,
    async invoke(request) {
      const card = animeCache.get(request.id);
      const detail = await detailAnime(request.id, config, card);
      return safeDetail(detail);
    },
  });

  await canx.services.register<CanX.BittorrentSearchRequest, CanX.BittorrentSearchPage>({
    id: "torrent-search",
    contract: "org.canx.bittorrent.search",
    contractVersion: "1.0",
    title: "Anime Find 动漫资源",
    description: "从 Mikan、AniBT 和 AnimeGarden 聚合动漫 Magnet 与 Torrent。AI 必须通过 search.torrents 使用。",
    risk: "network",
    audiences: ["plugin", "ai"],
    inputSchema: torrentSearchInputSchema,
    outputSchema: torrentSearchOutputSchema,
    async invoke(request) {
      return searchTorrents(request.query, clamp(request.limit, 1, 100, 20), cursor(request.cursor));
    },
  });

  await canx.services.register<SearchRequest, unknown>({
    id: "media-search",
    contract: "org.canx.media.search",
    contractVersion: "1.0",
    title: "Anime Find 在线播放搜索",
    description: "用启用的静态规则搜索可播放剧集；AI 只会收到宿主缓存的结果 ID。",
    risk: "network",
    audiences: ["plugin", "ai"],
    inputSchema: searchInputSchema,
    outputSchema: mediaSearchOutputSchema,
    async invoke(request) {
      const limit = clamp(request.limit, 1, 50, 20);
      const offset = cursor(request.cursor);
      const entries = flattenStreams(await searchStreams(request.query, config));
      const page = entries.slice(offset, offset + limit);
      cacheMedia(page);
      return {
        items: page.map(entry => ({
          id: mediaId(entry),
          title: `${entry.source.animeTitle} · ${entry.episode.name}`,
          description: `${entry.source.ruleName} / ${entry.source.lineName}`,
          format: "video",
        })),
        nextCursor: offset + page.length < entries.length ? String(offset + page.length) : undefined,
      };
    },
  });

  await canx.services.register<MediaResolveRequest, unknown>({
    id: "media-resolve",
    contract: "org.canx.media.resolve",
    contractVersion: "1.0",
    title: "Anime Find 在线播放解析",
    description: "把缓存的剧集解析为带域名和 Header 约束的不透明 CanX Stream Callback。",
    risk: "network",
    audiences: ["plugin", "ai"],
    inputSchema: detailInputSchema,
    outputSchema: mediaResolveOutputSchema,
    async invoke(request) {
      const entry = mediaCache.get(request.id);
      if (!entry) throw new Error("播放搜索结果已过期，请重新搜索。");
      let handle = resolvedHandles.get(request.id);
      if (!handle) {
        handle = await canx.media.stream.create(await streamRecipe(entry.source, entry.episode, config));
        await trackHandle(request.id, handle);
      }
      return {
        sources: [{
          kind: "stream",
          uri: handle.source,
          mediaType: "video",
          label: `${entry.source.ruleName} · ${entry.episode.name}`,
        }],
      };
    },
  });
}

async function runUiSearch(more: boolean): Promise<void> {
  if (!query) {
    searchStatus = "请先输入番剧名称。";
    searchTone = "warning";
    await page.render(rootPage());
    return;
  }
  searchOffset = more ? searchOffset + searchItems.length : 0;
  searchStatus = `正在搜索“${query.slice(0, 80)}”…`;
  searchTone = "accent";
  if (!more) searchItems = [];
  await page.render(rootPage());
  try {
    const result = await searchAnime(query, config, config.maxResults, searchOffset);
    cacheAnime(result.items);
    searchItems = toGalleryItems(result.items);
    const errors = result.errors.length
      ? `；${result.errors.map(item => `${item.source}：${item.message}`).join("；")}`
      : "";
    const summary = result.items.length
      ? `显示 ${result.offset + 1}-${result.offset + result.items.length} / ${result.total}`
      : "没有找到结果";
    searchStatus = `${summary}${errors}`.slice(0, 1000);
    searchTone = result.items.length ? "success" : "warning";
    if (!result.hasMore) searchOffset = -1;
  } catch (error) {
    searchStatus = errorMessage(error);
    searchTone = "error";
  }
  await page.render(rootPage());
}

async function openAnime(id: string): Promise<void> {
  const card = animeCache.get(id);
  if (!card) return;
  selectedCard = card;
  selectedDetail = undefined;
  detailStatus = "正在加载番剧详情…";
  detailTone = "accent";
  resourceStatus = "正在加载下载资源…";
  resourceTone = "accent";
  onlineStatus = "";
  onlineTone = "muted";
  resourceItems = [];
  onlineItems = [];
  downloadItems.clear();
  streamItems.clear();
  await page.render(rootPage());
  try {
    const detail = await detailAnime(card.id, config, card);
    if (selectedCard !== card) return;
    selectedDetail = detail;
    resourceItems = toResourceItems(selectedDetail);
    detailStatus = "";
    detailTone = "muted";
    resourceStatus = resourceItems.length
      ? `找到 ${resourceItems.length} 个下载资源。点击下载后由 CanX 确认。`
      : "没有找到下载资源，可尝试其他关键词或来源。";
    resourceTone = resourceItems.length ? "success" : "warning";
  } catch (error) {
    if (selectedCard !== card) return;
    detailStatus = errorMessage(error);
    detailTone = "error";
    resourceStatus = detailStatus;
    resourceTone = "error";
  }
  await updateDetailComponents([detailHeading(), ...overviewComponents(), ...downloadComponents()]);
}

async function loadStreams(): Promise<void> {
  const card = selectedCard;
  if (!card) return;
  onlineStatus = `正在搜索“${card.title}”的在线源…`;
  onlineTone = "accent";
  onlineItems = [];
  streamItems.clear();
  await updateDetailComponents(onlineComponents());
  try {
    const entries = flattenStreams(await searchStreams(card.title, config)).slice(0, 100);
    if (selectedCard !== card) return;
    cacheMedia(entries);
    for (const entry of entries) streamItems.set(mediaId(entry), entry);
    onlineItems = entries.map(entry => ({
      id: mediaId(entry),
      title: entry.episode.name,
      description: `${entry.source.ruleName} · ${entry.source.lineName}`,
      badge: "在线播放",
    }));
    onlineStatus = entries.length ? `找到 ${entries.length} 个可解析剧集。` : "没有找到静态规则可播放的剧集。";
    onlineTone = entries.length ? "success" : "warning";
  } catch (error) {
    if (selectedCard !== card) return;
    onlineStatus = errorMessage(error);
    onlineTone = "error";
  }
  await updateDetailComponents(onlineComponents());
}

async function requestDownload(id: string): Promise<void> {
  const card = selectedCard;
  const item = downloadItems.get(id);
  const source = item?.magnet ?? item?.torrent;
  if (!item || !source) return;
  try {
    await canx.downloads.requestAdd({
      kind: source.toLowerCase().startsWith("magnet:?") ? "magnet" : "http",
      source,
    });
    if (selectedCard !== card) return;
    resourceStatus = `已为“${(item.displayTitle ?? item.title).slice(0, 80)}”打开下载确认。`;
    resourceTone = "success";
  } catch (error) {
    if (selectedCard !== card) return;
    resourceStatus = errorMessage(error);
    resourceTone = "error";
  }
  await updateDetailComponents(downloadComponents());
}

async function playStream(id: string): Promise<void> {
  const card = selectedCard;
  const entry = streamItems.get(id);
  if (!entry) return;
  try {
    const handle = await canx.media.stream.play(await streamRecipe(entry.source, entry.episode, config));
    await trackHandle(`ui:${id}:${Date.now()}`, handle);
    if (selectedCard !== card) return;
    onlineStatus = `正在播放 ${entry.episode.name}`;
    onlineTone = "success";
  } catch (error) {
    if (selectedCard !== card) return;
    onlineStatus = errorMessage(error);
    onlineTone = "error";
  }
  await updateDetailComponents(onlineComponents());
}

async function searchTorrents(query: string, limit: number, offset: number): Promise<CanX.BittorrentSearchPage> {
  const found = await searchAnime(query, config, Math.min(12, Math.max(4, limit)), 0);
  cacheAnime(found.items);
  const details = await Promise.all(found.items.slice(0, 8).map(card => detailAnime(card.id, config, card).catch(() => undefined)));
  const results: CanX.BittorrentSearchResult[] = [];
  for (const detail of details) {
    if (!detail) continue;
    for (const group of detail.groups) {
      for (const item of group.items) {
        const magnetUri = safeMagnet(item.magnet);
        const torrentUrl = safeHttp(item.torrent);
        if (!magnetUri && !torrentUrl) continue;
        results.push({
          id: stableId(group.source, magnetUri ?? torrentUrl!, item.title),
          title: item.title,
          description: `${detail.title} · ${group.label}`,
          infoHash: infoHash(magnetUri),
          magnetUri,
          torrentUrl,
          sizeBytes: parseSize(item.size),
          publishedAt: isoDate(item.createdAt),
          categories: ["anime", ...item.tags ?? []].slice(0, 8),
          source: `${group.label} (${group.source})`,
          attributes: { animeId: detail.id, episode: item.episode ?? null },
        });
      }
    }
  }
  const deduplicated = [...new Map(results.map(item => [item.infoHash ?? item.magnetUri ?? item.torrentUrl ?? item.id, item])).values()];
  return {
    items: deduplicated.slice(offset, offset + limit),
    nextCursor: offset + limit < deduplicated.length ? String(offset + limit) : undefined,
    total: deduplicated.length,
  };
}

function rootPage(): CanX.UiComponent {
  return {
    id: "root",
    type: "viewStack",
    props: { active: selectedCard ? "detail" : "search" },
    children: [searchPage(), detailPage()],
  };
}

function searchPage(): CanX.UiComponent {
  return {
    id: "search",
    type: "stack",
    props: { spacing: "lg" },
    children: [
      { type: "text", props: { text: "搜番", style: "heading" } },
      {
        type: "text",
        props: {
          text: "聚合 Mikan、AniBT、AnimeGarden，并使用 Bangumi 补充番剧信息。",
          tone: "muted",
        },
      },
      {
        type: "grid",
        props: { columns: 4, spacing: "sm" },
        children: [
          {
            id: "anime-query",
            type: "searchInput",
            props: { label: "番剧", key: "anime-query", placeholder: "名称、本季、2026 春", default: query },
            layout: { column: 0, columnSpan: 3 },
          },
          {
            type: "button",
            props: { label: "搜索", action: "search", variant: "primary" },
            layout: { column: 3, verticalAlignment: "bottom" },
          },
        ],
      },
      { type: "text", props: { text: searchStatus, tone: searchTone } },
      {
        id: "anime-results",
        type: "gallery",
        props: {
          items: searchItems,
          emptyText: "暂无搜索结果。",
          action: "open-anime",
          itemWidth: 156,
          imageHeight: 210,
          spacing: "sm",
        },
      },
      {
        type: "button",
        props: { label: "下一批", action: "more", variant: "secondary", disabled: searchOffset < 0 || searchItems.length === 0 },
      },
    ],
  };
}

function detailPage(): CanX.UiComponent {
  return {
    id: "detail",
    type: "stack",
    props: { spacing: "md" },
    children: [
      { type: "button", props: { label: "返回搜索", action: "back", variant: "secondary" } },
      detailHeading(),
      {
        id: "detail-tabs",
        type: "tabs",
        props: { selected: 0 },
        children: [
          {
            type: "tab",
            props: { label: "详情和评论" },
            children: [{ type: "stack", props: { spacing: "md" }, children: overviewComponents() }],
          },
          {
            type: "tab",
            props: { label: "在线资源" },
            children: [{ type: "stack", props: { spacing: "md" }, children: onlineComponents() }],
          },
          {
            type: "tab",
            props: { label: "下载资源" },
            children: [{ type: "stack", props: { spacing: "md" }, children: downloadComponents() }],
          },
        ],
      },
    ],
  };
}

function detailHeading(): CanX.UiComponent {
  const title = selectedDetail?.title ?? selectedCard?.title ?? "番剧详情";
  return { id: "detail-title", type: "text", props: { text: title.slice(0, 300), style: "heading" } };
}

function overviewComponents(): readonly CanX.UiComponent[] {
  const detail = selectedDetail;
  const meta = [
    detail?.score ? `评分 ${detail.score.toFixed(1)}` : "",
    detail?.sources.length ? detail.sources.join(" + ") : "",
    detail?.tags?.join(" / ") ?? "",
  ].filter(Boolean).join(" · ");
  return [
    { id: "detail-status", type: "text", props: { text: detailStatus, tone: detailTone } },
    { id: "detail-meta", type: "text", props: { text: meta, tone: "accent" } },
    { id: "detail-summary", type: "text", props: { text: (detail?.summary ?? detail?.nameOrig ?? "").slice(0, 1000) } },
    {
      id: "detail-comments",
      type: "list",
      props: {
        items: (detail?.comments ?? []).map((comment, index) => ({
          id: `comment-${index}`,
          title: `${comment.nickname}${comment.rate ? ` · ${comment.rate} 分` : ""}`,
          description: comment.comment.slice(0, 1000),
          badge: comment.updatedAt ?? "短评",
        })),
        emptyText: "暂无 Bangumi 短评。",
      },
    },
  ];
}

function onlineComponents(): readonly CanX.UiComponent[] {
  return [
    {
      type: "button",
      props: { label: "搜索在线源", action: "search-streams", variant: "primary", disabled: !config.streamEnabled },
    },
    { id: "stream-status", type: "text", props: { text: onlineStatus, tone: onlineTone } },
    {
      id: "stream-results",
      type: "list",
      props: { items: onlineItems, emptyText: "尚未搜索在线播放源。", action: "play-stream", actionLabel: "播放" },
    },
  ];
}

function downloadComponents(): readonly CanX.UiComponent[] {
  return [
    { id: "resource-status", type: "text", props: { text: resourceStatus, tone: resourceTone } },
    {
      id: "resource-results",
      type: "list",
      props: { items: resourceItems, emptyText: "暂无下载资源。", action: "download", actionLabel: "下载" },
    },
  ];
}

function updateDetailComponents(components: readonly CanX.UiComponent[]): Promise<null> {
  // Update tab contents in place so asynchronous results do not reset the user's selected tab.
  return page.update(components.flatMap(component => component.id && component.props
    ? [{ target: component.id, props: component.props }]
    : []));
}

function configurationPage(message = "规则站点必须预先写入插件 Manifest，修改为任意域名不会获得网络权限。", tone: Tone = "muted"): CanX.UiComponent {
  return {
    type: "stack",
    props: { spacing: "md" },
    children: [
      { type: "text", props: { text: "搜索源", style: "heading" } },
      { type: "separator" },
      { type: "toggle", props: { label: "Mikan", key: "source-mikan", default: settingsDraft.mikan } },
      { type: "toggle", props: { label: "AniBT", key: "source-anibt", default: settingsDraft.anibt } },
      { type: "toggle", props: { label: "AnimeGarden", key: "source-garden", default: settingsDraft.garden } },
      { type: "numberInput", props: { label: "每页结果数", key: "max-results", default: settingsDraft.maxResults, min: 1, max: 50 } },
      { type: "text", props: { text: "站点地址", style: "title" } },
      { type: "textInput", props: { label: "Mikan", key: "mikan-host", default: settingsDraft.mikanHost } },
      { type: "textInput", props: { label: "AniBT", key: "anibt-host", default: settingsDraft.anibtHost } },
      { type: "textInput", props: { label: "AnimeGarden", key: "garden-host", default: settingsDraft.gardenHost } },
      { type: "text", props: { text: "在线播放", style: "heading" } },
      { type: "separator" },
      { type: "toggle", props: { label: "启用静态规则在线播放", key: "stream-enabled", default: settingsDraft.streamEnabled } },
      {
        type: "textArea",
        props: { label: "规则 JSON", key: "stream-rules", default: settingsDraft.rules, rows: 12 },
      },
      { type: "text", props: { text: message, tone } },
      {
        type: "grid",
        props: { columns: 2, spacing: "sm" },
        children: [
          { type: "button", props: { label: "恢复默认", action: "reset-settings", variant: "secondary" } },
          { type: "button", props: { label: "保存", action: "save-settings", variant: "primary" }, layout: { column: 1 } },
        ],
      },
    ],
  };
}

function toGalleryItems(cards: readonly AnimeCard[]): readonly UiItem[] {
  return cards.map(card => {
    const image = safeRemoteImage(card.cover);
    return {
      id: card.id,
      title: card.title.slice(0, 300),
      description: animeDescription(card).slice(0, 1000),
      badge: card.score ? `${card.score.toFixed(1)} · ${card.sources.join("+")}` : card.sources.join("+"),
      ...(image ? { image } : {}),
    };
  });
}

function toResourceItems(detail: AnimeDetail): readonly UiItem[] {
  downloadItems.clear();
  const output: UiItem[] = [];
  for (const group of detail.groups) {
    for (const item of group.items.slice(0, 100)) {
      if (!safeMagnet(item.magnet) && !safeHttp(item.torrent)) continue;
      const id = stableId(group.source, group.label, item.magnet ?? item.torrent ?? item.title);
      downloadItems.set(id, item);
      output.push({
        id,
        title: (item.displayTitle ?? item.title).slice(0, 300),
        description: [group.label, item.size, item.createdAt, ...(item.tags ?? [])].filter(Boolean).join(" · ").slice(0, 1000),
        badge: item.episode ? `第 ${item.episode} 集` : group.source,
      });
    }
  }
  return output.slice(0, 100);
}

function flattenStreams(sources: readonly StreamSource[]): CachedEpisode[] {
  return sources.flatMap(source => source.episodes.map(episode => ({ source, episode })));
}

function mediaId(entry: CachedEpisode): string {
  return stableId(entry.source.id, entry.episode.id);
}

function cacheAnime(cards: readonly AnimeCard[]): void {
  for (const card of cards) boundedSet(animeCache, card.id, card);
}

function cacheMedia(entries: readonly CachedEpisode[]): void {
  for (const entry of entries) boundedSet(mediaCache, mediaId(entry), entry);
}

function boundedSet<T>(map: Map<string, T>, key: string, value: T): void {
  map.delete(key);
  map.set(key, value);
  while (map.size > maximumCachedItems) map.delete(map.keys().next().value!);
}

async function trackHandle(key: string, handle: CanX.ManagedMediaStreamHandle): Promise<void> {
  resolvedHandles.set(key, handle);
  while (resolvedHandles.size > maximumManagedHandles) {
    const oldest = resolvedHandles.entries().next().value as [string, CanX.ManagedMediaStreamHandle] | undefined;
    if (!oldest) break;
    resolvedHandles.delete(oldest[0]);
    await canx.media.stream.release(oldest[1].id);
  }
}

function animeDescription(card: AnimeCard): string {
  return [
    card.nameOrig,
    card.resourceCount ? `${card.resourceCount} 个资源` : undefined,
    card.tags?.join(" / "),
  ].filter(Boolean).join(" · ") || `来源：${card.sources.join(" + ")}`;
}

function safeDetail(detail: AnimeDetail): unknown {
  return {
    id: detail.id,
    title: detail.title,
    originalTitle: detail.nameOrig,
    summary: detail.summary,
    score: detail.score,
    ratingCount: detail.ratingCount,
    tags: detail.tags,
    comments: detail.comments,
    sources: detail.sources,
    groups: detail.groups.map(group => ({
      label: group.label,
      source: group.source,
      updateDay: group.updateDay,
      items: group.items.map(item => ({
        title: item.title,
        displayTitle: item.displayTitle,
        episode: item.episode,
        size: item.size,
        createdAt: item.createdAt,
        tags: item.tags,
        downloadable: Boolean(item.magnet || item.torrent),
      })),
    })),
  };
}

function safeMagnet(value: string | undefined): string | undefined {
  const source = text(value, 8192);
  return source?.toLowerCase().startsWith("magnet:?") ? source : undefined;
}

function safeHttp(value: string | undefined): string | undefined {
  const source = text(value, 8192);
  return source && /^https?:\/\/[^\s]+$/i.test(source) ? source : undefined;
}

function safeRemoteImage(value: string | undefined): string | undefined {
  const source = safeHttp(value);
  if (!source) return undefined;
  const authority = /^(?:https?):\/\/([^/?#]+)/i.exec(source)?.[1] ?? "";
  return authority && !authority.includes("@") ? source : undefined;
}

function isoDate(value: string | undefined): string | undefined {
  if (!value) return undefined;
  const milliseconds = Date.parse(value);
  return Number.isFinite(milliseconds) ? new Date(milliseconds).toISOString() : undefined;
}

function cursor(value: string | undefined): number {
  const parsed = Number.parseInt(value ?? "0", 10);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : 0;
}

function withSources(current: PluginConfig, sources: readonly SourceId[] | undefined): PluginConfig {
  const selected = sources?.filter(source => source === "mikan" || source === "anibt" || source === "garden");
  return selected?.length ? { ...current, sources: [...new Set(selected)] } : current;
}

function defaultConfig(): PluginConfig {
  return {
    mikanHost: "https://mikanani.me",
    anibtHost: "https://anibt.net",
    gardenHost: "https://api.animes.garden",
    maxResults: 12,
    sources: ["mikan"],
    streamEnabled: true,
    streamRules: defaultStreamRules.map(rule => ({
      ...rule,
      mediaHosts: [...rule.mediaHosts ?? []],
      headers: { ...rule.headers },
      mediaHeaders: { ...rule.mediaHeaders },
    })),
  };
}

async function loadConfig(): Promise<PluginConfig> {
  try {
    const stored = await canx.storage.get<unknown>(configKey);
    return sanitizeConfig(stored);
  } catch {
    return defaultConfig();
  }
}

function sanitizeConfig(value: unknown): PluginConfig {
  const fallback = defaultConfig();
  if (!value || typeof value !== "object" || Array.isArray(value)) return fallback;
  const input = value as Partial<PluginConfig>;
  const sources = Array.isArray(input.sources)
    ? input.sources.filter(source => source === "mikan" || source === "anibt" || source === "garden")
    : fallback.sources;
  let rules = fallback.streamRules;
  try { if (Array.isArray(input.streamRules)) rules = validateRules(input.streamRules); } catch { /* use defaults */ }
  return {
    mikanHost: endpoint(input.mikanHost, "mikanani.me", fallback.mikanHost),
    anibtHost: endpoint(input.anibtHost, "anibt.net", fallback.anibtHost),
    gardenHost: endpoint(input.gardenHost, "api.animes.garden", fallback.gardenHost),
    maxResults: clamp(input.maxResults, 1, 50, fallback.maxResults),
    sources: sources.length ? [...new Set(sources)] : fallback.sources,
    streamEnabled: input.streamEnabled !== false,
    streamRules: rules,
  };
}

function endpoint(value: unknown, allowedHost: string, fallback: string): string {
  const candidate = text(value, 500)?.replace(/\/$/, "");
  return candidate && hostOf(candidate) === allowedHost ? candidate : fallback;
}

interface SettingsDraft {
  mikan: boolean;
  anibt: boolean;
  garden: boolean;
  maxResults: number;
  mikanHost: string;
  anibtHost: string;
  gardenHost: string;
  streamEnabled: boolean;
  rules: string;
}

function draftConfig(value: PluginConfig): SettingsDraft {
  return {
    mikan: value.sources.includes("mikan"),
    anibt: value.sources.includes("anibt"),
    garden: value.sources.includes("garden"),
    maxResults: value.maxResults,
    mikanHost: value.mikanHost,
    anibtHost: value.anibtHost,
    gardenHost: value.gardenHost,
    streamEnabled: value.streamEnabled,
    rules: JSON.stringify(value.streamRules, null, 2),
  };
}

function configFromDraft(value: SettingsDraft): PluginConfig {
  const sources: SourceId[] = [];
  if (value.mikan) sources.push("mikan");
  if (value.anibt) sources.push("anibt");
  if (value.garden) sources.push("garden");
  if (sources.length === 0) throw new Error("至少启用一个搜索源。");
  return {
    mikanHost: requireEndpoint(value.mikanHost, "mikanani.me", "Mikan"),
    anibtHost: requireEndpoint(value.anibtHost, "anibt.net", "AniBT"),
    gardenHost: requireEndpoint(value.gardenHost, "api.animes.garden", "AnimeGarden"),
    maxResults: clamp(value.maxResults, 1, 50, 12),
    sources,
    streamEnabled: value.streamEnabled,
    streamRules: validateRules(JSON.parse(value.rules) as unknown),
  };
}

function requireEndpoint(value: string, host: string, label: string): string {
  const normalized = value.trim().replace(/\/$/, "");
  if (!/^https?:\/\//i.test(normalized) || hostOf(normalized) !== host) {
    throw new Error(`${label} 地址必须使用 Manifest 已授权的 ${host}。`);
  }
  return normalized;
}

const searchInputSchema: CanX.JsonObject = {
  type: "object",
  additionalProperties: false,
  required: ["query"],
  properties: {
    query: { type: "string", minLength: 1, maxLength: 500 },
    limit: { type: "integer", minimum: 1, maximum: 50 },
    cursor: { type: "string", maxLength: 20 },
    sources: { type: "array", maxItems: 3, items: { enum: ["mikan", "anibt", "garden"] } },
  },
};

const detailInputSchema: CanX.JsonObject = {
  type: "object",
  additionalProperties: false,
  required: ["id"],
  properties: { id: { type: "string", minLength: 1, maxLength: 512 } },
};

const animeSearchOutputSchema: CanX.JsonObject = {
  type: "object",
  required: ["items", "total", "errors"],
  properties: {
    items: { type: "array", maxItems: 50 },
    nextCursor: { type: "string" },
    total: { type: "integer", minimum: 0 },
    errors: { type: "array" },
  },
};

const animeDetailOutputSchema: CanX.JsonObject = {
  type: "object",
  required: ["id", "title", "sources", "groups"],
  properties: {
    id: { type: "string" }, title: { type: "string" }, originalTitle: { type: "string" },
    summary: { type: "string" }, score: { type: "number" }, ratingCount: { type: "number" },
    tags: { type: "array" }, comments: { type: "array" }, sources: { type: "array" }, groups: { type: "array" },
  },
};

const torrentSearchInputSchema: CanX.JsonObject = {
  type: "object",
  additionalProperties: true,
  required: ["query"],
  properties: {
    query: { type: "string", minLength: 1, maxLength: 500 },
    limit: { type: "integer", minimum: 1, maximum: 100 },
    cursor: { type: "string", maxLength: 20 },
    options: {},
  },
};

const torrentSearchOutputSchema: CanX.JsonObject = {
  type: "object",
  required: ["items"],
  properties: {
    items: { type: "array", maxItems: 100 },
    nextCursor: { type: "string" },
    total: { type: "integer", minimum: 0 },
  },
};

const mediaSearchOutputSchema: CanX.JsonObject = {
  type: "object",
  required: ["items"],
  properties: { items: { type: "array", maxItems: 50 }, nextCursor: { type: "string" } },
};

const mediaResolveOutputSchema: CanX.JsonObject = {
  type: "object",
  required: ["sources"],
  properties: { sources: { type: "array", minItems: 1, maxItems: 1 } },
};

await registerServices();
