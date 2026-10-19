// SPDX-License-Identifier: GPL-3.0-or-later

import { resolveEpisode, searchResources, type SeekResource } from "./api.js";
import { prepareMedia } from "./media.js";

interface EpisodeEntry {
  readonly id: string;
  readonly title: string;
  readonly label: string;
  readonly description: string;
  readonly token: string;
}

interface ResourceEntry {
  readonly id: string;
  readonly title: string;
  readonly description: string;
  readonly episodes: readonly EpisodeEntry[];
}

interface SearchRequest {
  readonly query: string;
  readonly limit?: number;
  readonly cursor?: string;
}

const pageSize = 50;
const maximumCachedEpisodes = 5000;
const maximumHandles = 10;
const episodeCache = new Map<string, EpisodeEntry>();
const handles: CanX.ManagedMediaStreamHandle[] = [];
let generation = 0;
let query = "";
let resources: readonly ResourceEntry[] = [];
let resourceOffset = 0;
let episodeOffset = 0;
let selected: ResourceEntry | undefined;
let searching = false;
let playing = false;
let status = "输入片名搜索，可加年份缩小范围。";
let tone: "muted" | "accent" | "success" | "warning" | "error" = "muted";
let playbackStatus = "选择剧集即可在 CanX 中在线播放。";

const page = await canx.ui.page.mount({
  content: rootPage(),
  async onEvent(event) {
    if (event.type === "change" && event.key === "seek-query") {
      query = String(event.value ?? "");
      return;
    }
    if (event.type !== "action") return;
    if (event.action === "search") await runSearch();
    else if (event.action === "open-resource" && event.itemId) {
      selected = resources.find(resource => resource.id === event.itemId);
      episodeOffset = 0;
      playbackStatus = "选择剧集即可在 CanX 中在线播放。";
      await page.render(rootPage());
    } else if (event.action === "back") {
      selected = undefined;
      await page.render(rootPage());
    } else if (event.action === "play" && event.itemId) await playEpisode(event.itemId);
    else if (event.action === "resources-next" || event.action === "resources-previous") {
      resourceOffset = moveOffset(resourceOffset, resources.length, event.action.endsWith("next"));
      await page.render(rootPage());
    } else if (event.action === "episodes-next" || event.action === "episodes-previous") {
      episodeOffset = moveOffset(episodeOffset, selected?.episodes.length ?? 0, event.action.endsWith("next"));
      await page.render(rootPage());
    }
  },
});

async function runSearch(): Promise<void> {
  if (searching) return;
  searching = true;
  selected = undefined;
  resources = [];
  resourceOffset = 0;
  status = "正在搜索…";
  tone = "accent";
  await page.render(rootPage());
  try {
    resources = cacheResources(await searchResources(query));
    status = resources.length ? `找到 ${resources.length} 个资源，选择后查看剧集。` : "没有搜索到相关结果，请换一个关键词。";
    tone = resources.length ? "success" : "warning";
  } catch (error) {
    status = errorMessage(error);
    tone = "error";
  } finally {
    searching = false;
  }
  await page.render(rootPage());
}

async function playEpisode(id: string): Promise<void> {
  if (playing) return;
  const resource = selected;
  const entry = resource?.episodes.find(episode => episode.id === id);
  if (!entry) return;
  playing = true;
  playbackStatus = `正在解析 ${entry.label}…`;
  await updatePlaybackStatus();
  try {
    const recipe = await prepareEpisode(entry);
    // A late response must not start a video after the user has left its resource page.
    if (selected !== resource) return;
    await makeRoomForStream();
    handles.push(await canx.media.stream.play(recipe));
    playbackStatus = `已交给 CanX 播放：${entry.title}`;
  } catch (error) {
    playbackStatus = errorMessage(error);
  } finally {
    playing = false;
    if (selected === resource) await updatePlaybackStatus();
  }
}

async function prepareEpisode(entry: EpisodeEntry): Promise<CanX.ManagedMediaStreamRecipe> {
  // Resolve on every play: SEEKURL's episode tokens and signed CDN links can expire.
  return prepareMedia(await resolveEpisode(entry.token), entry.title);
}

async function makeRoomForStream(): Promise<void> {
  while (handles.length >= maximumHandles) {
    await canx.media.stream.release(handles.shift()!.id);
  }
}

function cacheResources(found: readonly SeekResource[]): readonly ResourceEntry[] {
  const batch = ++generation;
  return found.map((resource, resourceIndex) => {
    const id = `r${batch}-${resourceIndex}`;
    const description = [resource.year, resource.resolution, resource.source ? `来源 ${resource.source}` : ""].filter(Boolean).join(" · ");
    const episodes = resource.episodes.map((episode, index) => {
      const entry = {
        id: `${id}-e${index}`,
        title: `${resource.title} · ${episode.label}`,
        label: episode.label,
        description,
        token: episode.token,
      };
      episodeCache.set(entry.id, entry);
      while (episodeCache.size > maximumCachedEpisodes) episodeCache.delete(episodeCache.keys().next().value!);
      return entry;
    });
    return { id, title: resource.title, description, episodes };
  });
}

function rootPage(): CanX.UiComponent {
  return {
    id: "root",
    type: "viewStack",
    props: { active: selected ? "episodes" : "search" },
    children: [
      {
        id: "search", type: "stack", props: { spacing: "md" }, children: [
          { type: "text", props: { text: "SEEKURL", style: "heading" } },
          { type: "text", props: { text: "搜索秀云 SEEKURL 的电影、电视剧，在 CanX 中直接播放。", tone: "muted" } },
          {
            type: "grid", props: { columns: 4, spacing: "sm" }, children: [
              {
                type: "searchInput",
                props: { label: "片名", key: "seek-query", placeholder: "片名，可加年份", default: query, disabled: searching },
                layout: { column: 0, columnSpan: 3 },
              },
              {
                type: "button", props: { label: "搜索", action: "search", variant: "primary", disabled: searching },
                layout: { column: 3, verticalAlignment: "bottom" },
              },
            ],
          },
          { type: "text", props: { text: status, tone } },
          {
            id: "seek-results", type: "list", props: {
              items: resources.slice(resourceOffset, resourceOffset + pageSize).map(resource => ({
                id: resource.id, title: resource.title, description: resource.description, badge: `${resource.episodes.length} 集 / 版本`,
              })),
              action: "open-resource", actionLabel: "查看剧集", emptyText: "暂无搜索结果。",
            },
          },
          pagination("resources", resourceOffset, resources.length),
        ],
      },
      {
        id: "episodes", type: "stack", props: { spacing: "md" }, children: [
          { type: "button", props: { label: "返回搜索", action: "back", variant: "secondary" } },
          { type: "text", props: { text: selected?.title ?? "选择资源", style: "heading" } },
          { type: "text", props: { text: selected?.description ?? "", tone: "muted" } },
          { id: "playback-status", type: "text", props: { text: playbackStatus } },
          {
            id: "seek-episodes", type: "list", props: {
              items: (selected?.episodes ?? []).slice(episodeOffset, episodeOffset + pageSize).map(episode => ({
                id: episode.id, title: episode.label, description: episode.description,
              })),
              action: "play", actionLabel: "播放", emptyText: "该资源没有可播放的剧集。",
            },
          },
          pagination("episodes", episodeOffset, selected?.episodes.length ?? 0),
          { type: "text", props: { text: "链接过期时请返回重新搜索，也可选择其他来源。", tone: "muted" } },
        ],
      },
    ],
  };
}

function pagination(prefix: string, offset: number, total: number): CanX.UiComponent {
  return {
    type: "stack", props: { orientation: "horizontal", spacing: "sm" }, children: [
      { type: "button", props: { label: "上一页", action: `${prefix}-previous`, disabled: offset === 0 } },
      { type: "text", props: { text: `${total ? Math.floor(offset / pageSize) + 1 : 0} / ${Math.ceil(total / pageSize)}`, tone: "muted" } },
      { type: "button", props: { label: "下一页", action: `${prefix}-next`, disabled: offset + pageSize >= total } },
    ],
  };
}

function moveOffset(offset: number, total: number, forward: boolean): number {
  return Math.max(0, Math.min(offset + (forward ? pageSize : -pageSize), Math.floor(Math.max(0, total - 1) / pageSize) * pageSize));
}

function updatePlaybackStatus(): Promise<null> {
  return page.update([{ target: "playback-status", props: { text: playbackStatus } }]);
}

function errorMessage(error: unknown): string {
  return (error instanceof Error ? error.message : "请求失败，请稍后重试。").slice(0, 500);
}

await canx.services.register<SearchRequest, unknown>({
  id: "search",
  contract: "org.canx.media.search",
  contractVersion: "1.0",
  title: "SEEKURL 视频搜索",
  description: "搜索秀云 SEEKURL 电影和电视剧的可播放剧集；仅返回缓存 ID，不返回播放令牌或媒体地址。",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: {
    type: "object", additionalProperties: false, required: ["query"], properties: {
      query: { type: "string", minLength: 1, maxLength: 200 },
      limit: { type: "integer", minimum: 1, maximum: 50 },
      cursor: { type: "string", pattern: "^[0-9]{1,6}$", maxLength: 6 },
    },
  },
  outputSchema: {
    type: "object", additionalProperties: false, required: ["items"], properties: {
      items: {
        type: "array", maxItems: 50, items: {
          type: "object", additionalProperties: false, required: ["id", "title", "description", "format"], properties: {
            id: { type: "string", maxLength: 100 },
            title: { type: "string", maxLength: 1000 },
            description: { type: "string", maxLength: 300 },
            format: { const: "video" },
          },
        },
      },
      nextCursor: { type: "string", maxLength: 6 },
      total: { type: "integer", minimum: 0 },
    },
  },
  async invoke(request) {
    const limit = Math.max(1, Math.min(50, Math.trunc(request.limit ?? 20)));
    const offset = Number(request.cursor ?? "0");
    const entries = cacheResources(await searchResources(request.query)).flatMap(resource => resource.episodes);
    return {
      items: entries.slice(offset, offset + limit).map(entry => ({
        id: entry.id, title: entry.title, description: entry.description, format: "video",
      })),
      nextCursor: offset + limit < entries.length ? String(offset + limit) : undefined,
      total: entries.length,
    };
  },
});

await canx.services.register<{ readonly id: string }, unknown>({
  id: "resolve",
  contract: "org.canx.media.resolve",
  contractVersion: "1.0",
  title: "SEEKURL 播放解析",
  description: "将缓存的 SEEKURL 剧集解析为 CanX 媒体流，由原生播放器解码播放。",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: {
    type: "object", additionalProperties: false, required: ["id"],
    properties: { id: { type: "string", minLength: 1, maxLength: 100 } },
  },
  outputSchema: {
    type: "object", additionalProperties: false, required: ["sources"], properties: {
      sources: {
        type: "array", minItems: 1, maxItems: 1, items: {
          type: "object", additionalProperties: false, required: ["kind", "uri", "mediaType", "label"], properties: {
            kind: { const: "stream" }, uri: { type: "string", pattern: "^canx://" },
            mediaType: { const: "video" }, label: { type: "string", maxLength: 1000 },
          },
        },
      },
    },
  },
  async invoke(request) {
    const entry = episodeCache.get(request.id);
    if (!entry) throw new Error("搜索结果已过期，请重新搜索。");
    const recipe = await prepareEpisode(entry);
    await makeRoomForStream();
    const handle = await canx.media.stream.create(recipe);
    handles.push(handle);
    return { sources: [{ kind: "stream", uri: handle.source, mediaType: "video", label: entry.title }] };
  },
});
