// SPDX-License-Identifier: GPL-3.0-or-later

export {};

interface BangumiImages {
  readonly small?: string;
  readonly grid?: string;
  readonly large?: string;
  readonly medium?: string;
  readonly common?: string;
}

interface BangumiRating {
  readonly rank?: number;
  readonly total?: number;
  readonly score?: number;
}

interface BangumiCollection {
  readonly wish?: number;
  readonly collect?: number;
  readonly doing?: number;
  readonly on_hold?: number;
  readonly dropped?: number;
}

interface BangumiSubject {
  readonly id?: number;
  readonly type?: number;
  readonly name?: string;
  readonly name_cn?: string;
  readonly summary?: string;
  readonly date?: string;
  readonly air_date?: string;
  readonly air_weekday?: number;
  readonly platform?: string;
  readonly eps?: number;
  readonly total_episodes?: number;
  readonly images?: BangumiImages | null;
  readonly rating?: BangumiRating;
  readonly collection?: BangumiCollection;
  readonly tags?: readonly { readonly name?: string; readonly count?: number }[];
  readonly infobox?: readonly {
    readonly key?: string;
    readonly value?: string | readonly { readonly v?: string }[];
  }[];
}

interface BangumiCalendarGroup {
  readonly weekday?: { readonly id?: number; readonly cn?: string };
  readonly items?: readonly BangumiSubject[];
}

interface BangumiSearchResponse {
  readonly total?: number;
  readonly data?: readonly BangumiSubject[];
}

interface SubjectCard {
  readonly id: number;
  readonly title: string;
  readonly originalTitle: string;
  readonly summary: string;
  readonly coverUrl?: string;
  readonly detailCoverUrl?: string;
  readonly airDate?: string;
  readonly score?: number;
  readonly rank?: number;
}

interface CalendarCache {
  readonly expiresAt: number;
  readonly groups: readonly {
    readonly weekday: number;
    readonly label: string;
    readonly items: readonly SubjectCard[];
  }[];
}

interface FavoriteRecord extends SubjectCard {}

type GalleryItem = Readonly<Record<string, string | number | boolean | null>>;
type ListItem = Readonly<Record<string, string | number | boolean | null>>;
type StatusTone = "muted" | "accent" | "success" | "warning" | "error";
type ViewName = "home" | "detail" | "settings";

interface SearchServiceOption {
  readonly id: string;
  readonly label: string;
}

const apiBaseUrl = "https://api.bgm.tv";
const userAgent = "w4264/CanX-Bangumi/1.6.0 (https://github.com/w4264/CanX)";
const favoritesStorageKey = "favorites.v1";
const calendarStorageKey = "calendar.v2";
const proxyStorageKey = "httpProxy";
const calendarCacheDurationMs = 60 * 60 * 1000;
const maximumFavorites = 100;
const searchLimit = 20;
const weekdayLabels = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日"] as const;

let currentView: ViewName = "home";
let selectedWeekday = currentWeekday();
let pageQuery = "";
let calendarGroups: CalendarCache["groups"] = [];
let calendarLoaded = false;
let calendarStatus = "切换到此页面后加载新番日历。";
let calendarTone: StatusTone = "muted";
let calendarItems: readonly GalleryItem[] = [];
let searchStatus = "输入名称搜索 Bangumi 动画条目。";
let searchTone: StatusTone = "muted";
let searchItems: readonly GalleryItem[] = [];
let favorites = await loadFavorites();
let favoriteItems: readonly GalleryItem[] = toGalleryItems(favorites);
let currentDetail: BangumiSubject | null = null;
let currentDetailCover: string | undefined;
let detailMessage = "";
let detailMessageTone: StatusTone = "success";
let resourceQuery = "";
let resourceServices: readonly SearchServiceOption[] = [];
let selectedResourceServiceId = "";
let resourceSearchStatus = "打开番剧详情后选择搜索接口。";
let resourceSearchTone: StatusTone = "muted";
let resourceSearchItems: readonly ListItem[] = [];
const resourceResults = new Map<string, CanX.BittorrentSearchResult>();
const knownSubjects = new Map<number, SubjectCard>();

const storedProxy = await canx.storage.get<unknown>(proxyStorageKey);
const normalizedStoredProxy = typeof storedProxy === "string" ? normalizeHttpProxy(storedProxy) : "";
let httpProxy = normalizedStoredProxy ?? "";
let draftHttpProxy = httpProxy;
await canx.http.setDefaultProxy(httpProxy || null);

for (const favorite of favorites) knownSubjects.set(favorite.id, favorite);

const page = await canx.ui.page.mount({
  content: createRootContent(),
  async onEvent(event) {
    if (event.type === "change" && event.key === "weekday") {
      const index = weekdayLabels.indexOf(String(event.value ?? "") as typeof weekdayLabels[number]);
      if (index >= 0) {
        selectedWeekday = index + 1;
        await refreshCalendarGallery();
      }
      return;
    }
    if (event.type === "change" && event.key === "query") {
      pageQuery = String(event.value ?? "").trim();
      return;
    }
    if (event.type === "change" && event.key === "httpProxy") {
      draftHttpProxy = String(event.value ?? "").trim();
      return;
    }
    if (event.type === "change" && event.key === "resourceQuery") {
      resourceQuery = String(event.value ?? "");
      return;
    }
    if (event.type === "change" && event.key === "resourceService") {
      selectedResourceServiceId = resourceServices.find(service => service.label === String(event.value ?? ""))?.id ?? "";
      return;
    }
    if (event.type !== "action") return;

    if (event.action === "search") {
      await searchSubjects();
      return;
    }
    if (event.action === "open-subject" && typeof event.itemId === "string") {
      await openSubject(event.itemId);
      return;
    }
    if (event.action === "back-home") {
      currentView = "home";
      detailMessage = "";
      await showView("home-view");
      return;
    }
    if (event.action === "toggle-favorite") {
      await toggleFavorite();
      return;
    }
    if (event.action === "search-resources") {
      await searchResources();
      return;
    }
    if (event.action === "download-resource" && typeof event.itemId === "string") {
      await downloadResource(event.itemId);
      return;
    }
    if (event.action === "open-settings") {
      currentView = "settings";
      draftHttpProxy = httpProxy;
      await page.update([
        { target: "http-proxy", props: { default: draftHttpProxy } },
        {
          target: "settings-status",
          props: { text: "留空表示直连。", tone: "muted" },
        },
        { target: "root-view", props: { active: "settings-view" } },
      ]);
      return;
    }
    if (event.action === "cancel-settings") {
      currentView = "home";
      draftHttpProxy = httpProxy;
      await showView("home-view");
      return;
    }
    if (event.action === "save-settings") {
      await saveSettings();
    }
  },
  async onActivate() {
    if (currentView === "home") await ensureHomeData();
  },
});

function createRootContent(): CanX.UiComponent {
  return {
    id: "root-view",
    type: "viewStack",
    props: { active: "home-view" },
    children: [createHomeContent(), createDetailView(), createSettingsContent()],
  };
}

function createHomeContent(): CanX.UiComponent {
  return {
    id: "home-view",
    type: "stack",
    props: { spacing: "lg" },
    children: [
      {
        type: "grid",
        props: { columns: 4, spacing: "sm" },
        children: [
          {
            type: "text",
            props: { text: "Bangumi 番剧", style: "heading" },
            layout: { column: 0, columnSpan: 3, verticalAlignment: "center" },
          },
          {
            id: "open-settings",
            type: "button",
            props: { label: "网络设置", action: "open-settings", variant: "secondary" },
            layout: { column: 3, horizontalAlignment: "right" },
          },
        ],
      },
      { type: "text", props: { text: "新番日历", style: "title" } },
      {
        id: "weekday",
        type: "select",
        props: {
          label: "放送星期",
          key: "weekday",
          options: [...weekdayLabels],
          default: weekdayLabels[selectedWeekday - 1],
        },
      },
      { id: "calendar-status", type: "text", props: { text: calendarStatus, tone: calendarTone } },
      {
        id: "calendar-gallery",
        type: "gallery",
        props: {
          items: calendarItems,
          emptyText: calendarLoaded ? "当天暂无动画条目。" : "日历尚未加载。",
          action: "open-subject",
          itemWidth: 144,
          imageHeight: 192,
          spacing: "sm",
        },
      },
      { type: "separator" },
      { type: "text", props: { text: "我的收藏", style: "title" } },
      {
        id: "favorites-gallery",
        type: "gallery",
        props: {
          items: favoriteItems,
          emptyText: "暂无收藏。打开条目详情后可加入收藏。",
          action: "open-subject",
          itemWidth: 144,
          imageHeight: 192,
          spacing: "sm",
        },
      },
      { type: "separator" },
      { type: "text", props: { text: "搜索番剧", style: "title" } },
      {
        type: "grid",
        props: { columns: 4, spacing: "sm" },
        children: [
          {
            id: "query",
            type: "searchInput",
            props: {
              label: "名称",
              key: "query",
              placeholder: "输入中文名、原名或关键词",
              default: pageQuery,
            },
            layout: { column: 0, columnSpan: 3 },
          },
          {
            id: "search",
            type: "button",
            props: { label: "搜索", action: "search", variant: "primary" },
            layout: { column: 3, verticalAlignment: "bottom" },
          },
        ],
      },
      { id: "search-status", type: "text", props: { text: searchStatus, tone: searchTone } },
      {
        id: "search-gallery",
        type: "gallery",
        props: {
          items: searchItems,
          emptyText: "暂无搜索结果。",
          action: "open-subject",
          itemWidth: 144,
          imageHeight: 192,
          spacing: "sm",
        },
      },
      {
        type: "text",
        props: {
          text: "数据来自 Bangumi 番组计划。条目信息遵循其版权声明，封面版权归原权利人。",
          style: "caption",
          tone: "muted",
        },
      },
    ],
  };
}

function createSettingsContent(): CanX.UiComponent {
  return {
    id: "settings-view",
    type: "stack",
    props: { spacing: "md" },
    children: [
      { type: "text", props: { text: "网络设置", style: "heading" } },
      {
        type: "text",
        props: {
          text: "Bangumi API 无法直连时，可配置无凭据 HTTP 代理。",
          tone: "muted",
        },
      },
      {
        id: "http-proxy",
        type: "textInput",
        props: {
          label: "HTTP 代理",
          key: "httpProxy",
          placeholder: "http://127.0.0.1:7890",
          default: draftHttpProxy,
        },
      },
      {
        id: "settings-status",
        type: "text",
        props: { text: "留空表示直连。", tone: "muted" },
      },
      {
        type: "grid",
        props: { columns: 2, spacing: "sm" },
        children: [
          { type: "button", props: { label: "保存", action: "save-settings", variant: "primary" } },
          {
            type: "button",
            props: { label: "取消", action: "cancel-settings", variant: "secondary" },
            layout: { column: 1 },
          },
        ],
      },
    ],
  };
}

function createDetailView(): CanX.UiComponent {
  return {
    id: "detail-view",
    type: "viewStack",
    props: { active: "detail-loading" },
    children: [
      {
        id: "detail-loading",
        type: "stack",
        props: { spacing: "md" },
        children: [
          { type: "button", props: { label: "返回", action: "back-home", variant: "secondary" } },
          { id: "detail-loading-spinner", type: "spinner", props: { label: "正在加载条目详情…" } },
        ],
      },
      createDetailContent(),
      {
        id: "detail-error",
        type: "stack",
        props: { spacing: "md" },
        children: [
          { type: "button", props: { label: "返回", action: "back-home", variant: "secondary" } },
          {
            id: "detail-error-alert",
            type: "alert",
            props: { title: "无法加载详情", message: "详情数据不可用。", tone: "error" },
          },
        ],
      },
    ],
  };
}

function createDetailContent(): CanX.UiComponent {
  return {
    id: "detail-content",
    type: "stack",
    props: { spacing: "md" },
    children: [
      {
        type: "grid",
        props: { columns: 4, spacing: "sm" },
        children: [
          { type: "button", props: { label: "返回", action: "back-home", variant: "secondary" } },
          {
            id: "favorite-button",
            type: "button",
            props: { label: "加入收藏", action: "toggle-favorite", variant: "primary" },
            layout: { column: 3, horizontalAlignment: "right" },
          },
        ],
      },
      { id: "detail-title", type: "text", props: { text: "", style: "heading" } },
      { id: "detail-original", type: "text", props: { text: "", tone: "muted" } },
      {
        id: "detail-cover-view",
        type: "viewStack",
        props: { active: "detail-cover-empty" },
        children: [
          {
            id: "detail-cover-image",
            type: "image",
            props: { alt: "番剧封面", width: 220, height: 308 },
            layout: { horizontalAlignment: "center" },
          },
          {
            id: "detail-cover-empty",
            type: "emptyState",
            props: { title: "暂无封面", message: "Bangumi 未提供可用封面。", icon: "image" },
          },
        ],
      },
      {
        id: "detail-message-view",
        type: "viewStack",
        props: { active: "detail-message-empty" },
        children: [
          { id: "detail-message-empty", type: "text", props: { text: "" } },
          {
            id: "detail-message-alert",
            type: "alert",
            props: { title: "收藏", message: "", tone: "success" },
          },
        ],
      },
      { id: "detail-values", type: "keyValue", props: { items: [] } },
      { type: "text", props: { text: "简介", style: "title" } },
      ...Array.from({ length: 8 }, (_, index): CanX.UiComponent => ({
        id: `detail-summary-${index + 1}`,
        type: "text",
        props: { text: "" },
      })),
      {
        id: "detail-info-view",
        type: "viewStack",
        props: { active: "detail-info-empty" },
        children: [
          { id: "detail-info-empty", type: "text", props: { text: "" } },
          {
            id: "detail-info-content",
            type: "expander",
            props: { title: "更多资料", expanded: false },
            children: [{ id: "detail-info-values", type: "keyValue", props: { items: [] } }],
          },
        ],
      },
      { id: "detail-id", type: "text", props: { text: "", style: "caption", tone: "muted" } },
      { type: "separator" },
      { type: "text", props: { text: "搜索下载资源", style: "title" } },
      {
        id: "resource-query",
        type: "searchInput",
        props: {
          label: "关键词",
          key: "resourceQuery",
          placeholder: "输入番剧名称",
          default: "",
        },
      },
      {
        id: "resource-service",
        type: "radioGroup",
        props: {
          label: "搜索接口",
          key: "resourceService",
          options: ["暂无可用接口"],
          default: "暂无可用接口",
          disabled: true,
        },
      },
      {
        id: "resource-search",
        type: "button",
        props: { label: "搜索资源", action: "search-resources", variant: "primary", disabled: true },
        layout: { horizontalAlignment: "left" },
      },
      {
        id: "resource-search-status",
        type: "text",
        props: { text: "打开番剧详情后选择搜索接口。", tone: "muted" },
      },
      {
        id: "resource-search-results",
        type: "list",
        props: {
          items: [],
          emptyText: "尚未搜索。",
          action: "download-resource",
          actionLabel: "下载",
        },
      },
    ],
  };
}

async function ensureHomeData(): Promise<void> {
  await Promise.all([ensureCalendar(), refreshFavoritesGallery()]);
}

async function ensureCalendar(force = false): Promise<void> {
  if (calendarLoaded && !force) return;
  calendarStatus = "正在加载 Bangumi 新番日历…";
  calendarTone = "accent";
  await page.update([{ target: "calendar-status", props: { text: calendarStatus, tone: calendarTone } }]);
  try {
    const cached = force ? null : await readCalendarCache();
    if (cached) {
      calendarGroups = cached.groups;
    } else {
      const payload = await getJson<readonly BangumiCalendarGroup[]>(`${apiBaseUrl}/calendar`);
      calendarGroups = normalizeCalendar(payload);
      try {
        await canx.storage.set(calendarStorageKey, {
          expiresAt: Date.now() + calendarCacheDurationMs,
          groups: calendarGroups,
        } satisfies CalendarCache);
      } catch {
        // A cache write failure must not make otherwise valid online data unusable.
      }
    }
    for (const group of calendarGroups) {
      for (const item of group.items) knownSubjects.set(item.id, item);
    }
    calendarLoaded = true;
    await refreshCalendarGallery();
  } catch (error) {
    calendarStatus = errorMessage(error, "新番日历加载失败。");
    calendarTone = "error";
    await page.update([{ target: "calendar-status", props: { text: calendarStatus, tone: calendarTone } }]);
  }
}

async function refreshCalendarGallery(): Promise<void> {
  const group = calendarGroups.find(item => item.weekday === selectedWeekday);
  const cards = group?.items ?? [];
  calendarStatus = group ? `${group.label} · ${cards.length} 部` : `${weekdayLabels[selectedWeekday - 1]}暂无数据`;
  calendarTone = cards.length > 0 ? "success" : "muted";
  calendarItems = toGalleryItems(cards);
  await page.update([
    { target: "weekday", props: { default: weekdayLabels[selectedWeekday - 1] } },
    { target: "calendar-status", props: { text: calendarStatus, tone: calendarTone } },
    { target: "calendar-gallery", props: { items: calendarItems } },
  ]);
}

async function refreshFavoritesGallery(): Promise<void> {
  favoriteItems = toGalleryItems(favorites);
  await page.update([{ target: "favorites-gallery", props: { items: favoriteItems } }]);
}

async function searchSubjects(): Promise<void> {
  if (!pageQuery) {
    searchStatus = "请先输入番剧名称或关键词。";
    searchTone = "warning";
    await page.update([{ target: "search-status", props: { text: searchStatus, tone: searchTone } }]);
    return;
  }

  searchStatus = `正在搜索“${truncate(pageQuery, 80)}”…`;
  searchTone = "accent";
  searchItems = [];
  await page.update([
    { target: "query", props: { default: pageQuery } },
    { target: "search-status", props: { text: searchStatus, tone: searchTone } },
    { target: "search-gallery", props: { items: searchItems } },
  ]);
  try {
    const payload = await postJson<BangumiSearchResponse>(
      `${apiBaseUrl}/v0/search/subjects?limit=${searchLimit}&offset=0`,
      { keyword: pageQuery, sort: "match", filter: { type: [2] } },
    );
    const cards = (payload.data ?? [])
      .filter(item => item.type === undefined || item.type === 2)
      .map(item => toSubjectCard(item, true))
      .filter((item): item is SubjectCard => item !== null);
    for (const card of cards) knownSubjects.set(card.id, card);
    searchItems = toGalleryItems(cards);
    searchStatus = `找到 ${cards.length} 个结果，点击封面查看详情。`;
    searchTone = "success";
    await page.update([
      { target: "search-status", props: { text: searchStatus, tone: searchTone } },
      { target: "search-gallery", props: { items: searchItems } },
    ]);
  } catch (error) {
    searchStatus = errorMessage(error, "搜索失败。");
    searchTone = "error";
    await page.update([{ target: "search-status", props: { text: searchStatus, tone: searchTone } }]);
  }
}

async function openSubject(itemId: string): Promise<void> {
  const id = Number(itemId);
  if (!Number.isInteger(id) || id <= 0) return;
  currentView = "detail";
  currentDetail = null;
  currentDetailCover = undefined;
  detailMessage = "";
  detailMessageTone = "success";
  const known = knownSubjects.get(id);
  await page.update([
    {
      target: "detail-loading-spinner",
      props: { label: `正在加载《${truncate(known?.title ?? `#${id}`, 80)}》的详情…` },
    },
    { target: "detail-view", props: { active: "detail-loading" } },
    { target: "root-view", props: { active: "detail-view" } },
  ]);
  try {
    currentDetail = await getJson<BangumiSubject>(`${apiBaseUrl}/v0/subjects/${id}`);
    if (currentDetail.id !== id) throw new Error("Bangumi 返回了不匹配的条目。");
    const card = toSubjectCard(currentDetail, true);
    if (card) knownSubjects.set(card.id, card);
    currentDetailCover = remoteCover(preferredCover(currentDetail.images, true));
    await prepareResourceSearch(displayTitle(currentDetail));
    await updateDetailContent();
  } catch (error) {
    currentDetail = null;
    await page.update([
      {
        target: "detail-error-alert",
        props: { message: errorMessage(error, "详情加载失败。") },
      },
      { target: "detail-view", props: { active: "detail-error" } },
    ]);
  }
}

async function updateDetailContent(): Promise<void> {
  if (!currentDetail) return;
  const subject = currentDetail;
  const title = displayTitle(subject);
  const originalName = subject.name && subject.name !== title ? subject.name : "";
  const favorite = favorites.some(item => item.id === subject.id);
  const summary = splitText(subject.summary?.trim() || "Bangumi 暂未提供简介。", 1000);
  const infobox = detailInfobox(subject);
  const patches: CanX.UiPagePatch[] = [
    {
      target: "favorite-button",
      props: {
        label: favorite ? "取消收藏" : "加入收藏",
        variant: favorite ? "danger" : "primary",
      },
    },
    { target: "detail-title", props: { text: truncate(title, 1024) } },
    { target: "detail-original", props: { text: truncate(originalName, 1024) } },
    {
      target: "detail-cover-image",
      props: { source: currentDetailCover ?? null, alt: truncate(title, 1024) },
    },
    {
      target: "detail-cover-view",
      props: { active: currentDetailCover ? "detail-cover-image" : "detail-cover-empty" },
    },
    {
      target: "detail-message-alert",
      props: { message: detailMessage, tone: detailMessageTone },
    },
    {
      target: "detail-message-view",
      props: { active: detailMessage ? "detail-message-alert" : "detail-message-empty" },
    },
    { target: "detail-values", props: { items: detailKeyValues(subject) } },
    { target: "detail-info-values", props: { items: infobox } },
    {
      target: "detail-info-view",
      props: { active: infobox.length > 0 ? "detail-info-content" : "detail-info-empty" },
    },
    {
      target: "detail-id",
      props: { text: `Bangumi 条目 ID：${subject.id ?? "-"}` },
    },
    { target: "resource-query", props: { default: resourceQuery } },
    {
      target: "resource-service",
      props: {
        options: resourceServices.length > 0 ? resourceServices.map(service => service.label) : ["暂无可用接口"],
        default: selectedResourceLabel() || "暂无可用接口",
        disabled: resourceServices.length === 0,
      },
    },
    { target: "resource-search", props: { disabled: resourceServices.length === 0 } },
    {
      target: "resource-search-status",
      props: { text: resourceSearchStatus, tone: resourceSearchTone },
    },
    { target: "resource-search-results", props: { items: resourceSearchItems } },
    { target: "detail-view", props: { active: "detail-content" } },
  ];
  for (let index = 0; index < 8; index++) {
    patches.push({ target: `detail-summary-${index + 1}`, props: { text: summary[index] ?? "" } });
  }
  await page.update(patches);
}

async function prepareResourceSearch(title: string): Promise<void> {
  resourceQuery = title;
  resourceSearchItems = [];
  resourceResults.clear();
  try {
    const services = await canx.services.list({
      contract: "org.canx.bittorrent.search",
      majorVersion: 1,
    });
    resourceServices = services
      .slice()
      .sort((left, right) => left.title.localeCompare(right.title) || left.id.localeCompare(right.id))
      .map((service, index) => ({
        id: service.id,
        label: `${index + 1}. ${truncate(service.title, 40)} · ${truncate(service.pluginId, 50)}`,
      }));
    if (!resourceServices.some(service => service.id === selectedResourceServiceId)) {
      selectedResourceServiceId = resourceServices[0]?.id ?? "";
    }
    resourceSearchStatus = resourceServices.length > 0
      ? `已发现 ${resourceServices.length} 个搜索接口。确认关键词和接口后点击搜索。`
      : "未发现已启用的 BitTorrent 搜索插件，请先到插件页面安装并启用。";
    resourceSearchTone = resourceServices.length > 0 ? "muted" : "warning";
  } catch (error) {
    resourceServices = [];
    selectedResourceServiceId = "";
    resourceSearchStatus = errorMessage(error, "无法读取已注册的搜索接口。");
    resourceSearchTone = "error";
  }
}

async function searchResources(): Promise<void> {
  const query = resourceQuery.trim();
  if (!query) {
    resourceSearchStatus = "请先输入搜索关键词。";
    resourceSearchTone = "warning";
    await updateResourceSearchView();
    return;
  }
  if (!selectedResourceServiceId) {
    resourceSearchStatus = "请先选择一个可用的搜索接口。";
    resourceSearchTone = "warning";
    await updateResourceSearchView();
    return;
  }

  resourceQuery = query;
  resourceSearchItems = [];
  resourceResults.clear();
  resourceSearchStatus = `正在通过 ${selectedResourceLabel()} 搜索“${truncate(query, 80)}”…`;
  resourceSearchTone = "accent";
  await updateResourceSearchView();
  try {
    const result = await canx.services.invoke<CanX.BittorrentSearchPage>(selectedResourceServiceId, {
      query,
      limit: 30,
    });
    const items = Array.isArray(result?.items) ? result.items.slice(0, 100) : [];
    resourceSearchItems = items.map((item, index) => {
      const id = `${index}:${item.id}`;
      resourceResults.set(id, item);
      return {
        id,
        title: truncate(item.title, 300),
        description: truncate(item.description || item.source || "暂无简介。", 1000),
        badge: resourceBadge(item),
      };
    });
    resourceSearchStatus = `找到 ${resourceSearchItems.length} 个结果。点击“下载”后仍会由 CanX 要求确认。`;
    resourceSearchTone = "success";
  } catch (error) {
    resourceSearchStatus = errorMessage(error, "资源搜索失败。");
    resourceSearchTone = "error";
  }
  await updateResourceSearchView();
}

async function downloadResource(itemId: string): Promise<void> {
  const result = resourceResults.get(itemId);
  const source = result?.magnetUri || result?.torrentUrl;
  if (!source) {
    resourceSearchStatus = "该结果没有可用的 Magnet 或 Torrent 下载地址。";
    resourceSearchTone = "warning";
    await updateResourceSearchView();
    return;
  }
  try {
    await canx.downloads.requestAdd({
      kind: source.toLowerCase().startsWith("magnet:?") ? "magnet" : "http",
      source,
    });
    resourceSearchStatus = `已为“${truncate(result?.title ?? "资源", 80)}”打开下载确认。`;
    resourceSearchTone = "success";
  } catch (error) {
    resourceSearchStatus = errorMessage(error, "无法打开下载确认。");
    resourceSearchTone = "error";
  }
  await updateResourceSearchView();
}

async function updateResourceSearchView(): Promise<void> {
  await page.update([
    { target: "resource-query", props: { default: resourceQuery } },
    {
      target: "resource-search-status",
      props: { text: resourceSearchStatus, tone: resourceSearchTone },
    },
    { target: "resource-search-results", props: { items: resourceSearchItems } },
  ]);
}

function selectedResourceLabel(): string {
  return resourceServices.find(service => service.id === selectedResourceServiceId)?.label ?? "";
}

function resourceBadge(item: CanX.BittorrentSearchResult): string {
  const parts: string[] = [];
  if (typeof item.sizeBytes === "number") parts.push(formatBytes(item.sizeBytes));
  if (typeof item.seeders === "number") parts.push(`↑ ${item.seeders}`);
  if (typeof item.leechers === "number") parts.push(`↓ ${item.leechers}`);
  if (item.source) parts.push(item.source);
  return truncate(parts.join(" · "), 300);
}

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return "";
  const units = ["B", "KiB", "MiB", "GiB", "TiB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value >= 10 || unit === 0 ? value.toFixed(0) : value.toFixed(1)} ${units[unit]}`;
}

async function toggleFavorite(): Promise<void> {
  if (!currentDetail?.id) return;
  const index = favorites.findIndex(item => item.id === currentDetail!.id);
  let nextFavorites: readonly FavoriteRecord[];
  if (index >= 0) {
    nextFavorites = [...favorites.slice(0, index), ...favorites.slice(index + 1)];
    detailMessage = "已从我的收藏移除。";
    detailMessageTone = "success";
  } else {
    if (favorites.length >= maximumFavorites) {
      detailMessage = `最多保存 ${maximumFavorites} 个本机收藏。`;
      detailMessageTone = "warning";
      await updateDetailContent();
      return;
    }
    const card = toSubjectCard(currentDetail, true);
    if (!card) return;
    nextFavorites = [card, ...favorites];
    knownSubjects.set(card.id, card);
    detailMessage = "已加入我的收藏。";
    detailMessageTone = "success";
  }
  await canx.storage.set(favoritesStorageKey, nextFavorites.map(toStoredFavorite));
  favorites = nextFavorites;
  await Promise.all([refreshFavoritesGallery(), updateDetailContent()]);
}

async function saveSettings(): Promise<void> {
  const normalized = normalizeHttpProxy(draftHttpProxy);
  if (normalized === null) {
    await page.update([
      { target: "http-proxy", props: { default: draftHttpProxy } },
      {
        target: "settings-status",
        props: { text: "请输入不带账号和路径的 HTTP 代理地址，或留空。", tone: "error" },
      },
    ]);
    return;
  }
  await canx.http.setDefaultProxy(normalized || null);
  httpProxy = normalized;
  if (httpProxy) await canx.storage.set(proxyStorageKey, httpProxy);
  else await canx.storage.remove(proxyStorageKey);
  await canx.storage.remove(calendarStorageKey);
  calendarGroups = [];
  calendarItems = [];
  calendarLoaded = false;
  calendarStatus = httpProxy ? `已启用代理 ${httpProxy}，正在重新加载…` : "已改为直连，正在重新加载…";
  calendarTone = "success";
  currentView = "home";
  await page.update([
    { target: "calendar-status", props: { text: calendarStatus, tone: calendarTone } },
    { target: "calendar-gallery", props: { items: [] } },
    { target: "root-view", props: { active: "home-view" } },
  ]);
  await ensureHomeData();
}

async function showView(viewId: "home-view" | "detail-view" | "settings-view"): Promise<void> {
  await page.update([{ target: "root-view", props: { active: viewId } }]);
}

async function loadFavorites(): Promise<readonly FavoriteRecord[]> {
  const stored = await canx.storage.get<unknown>(favoritesStorageKey);
  if (!Array.isArray(stored)) return [];
  const result: FavoriteRecord[] = [];
  for (const value of stored.slice(0, maximumFavorites)) {
    if (!value || typeof value !== "object") continue;
    const item = value as Partial<FavoriteRecord>;
    if (!Number.isInteger(item.id) || Number(item.id) <= 0 || typeof item.title !== "string") continue;
    result.push({
      id: Number(item.id),
      title: truncate(item.title, 300),
      originalTitle: typeof item.originalTitle === "string" ? truncate(item.originalTitle, 300) : "",
      summary: typeof item.summary === "string" ? truncate(item.summary, 4000) : "",
      ...(typeof item.coverUrl === "string" ? { coverUrl: normalizeHttps(item.coverUrl) } : {}),
      ...(typeof item.detailCoverUrl === "string" ? { detailCoverUrl: normalizeHttps(item.detailCoverUrl) } : {}),
      ...(typeof item.airDate === "string" ? { airDate: truncate(item.airDate, 40) } : {}),
      ...(typeof item.score === "number" ? { score: item.score } : {}),
      ...(typeof item.rank === "number" ? { rank: item.rank } : {}),
    });
  }
  return result;
}

async function readCalendarCache(): Promise<CalendarCache | null> {
  const stored = await canx.storage.get<unknown>(calendarStorageKey);
  if (!stored || typeof stored !== "object") return null;
  const cache = stored as Partial<CalendarCache>;
  if (typeof cache.expiresAt !== "number" || cache.expiresAt <= Date.now() || !Array.isArray(cache.groups)) {
    return null;
  }
  return cache as CalendarCache;
}

function normalizeCalendar(payload: readonly BangumiCalendarGroup[]): CalendarCache["groups"] {
  const groups: CalendarCache["groups"][number][] = [];
  for (const group of payload) {
    const weekday = group.weekday?.id;
    if (!Number.isInteger(weekday) || Number(weekday) < 1 || Number(weekday) > 7) continue;
    const items = (group.items ?? [])
      .map(item => toSubjectCard(item, false))
      .filter((item): item is SubjectCard => item !== null);
    groups.push({
      weekday: Number(weekday),
      label: group.weekday?.cn || weekdayLabels[Number(weekday) - 1],
      items,
    });
  }
  return groups;
}

function toSubjectCard(subject: BangumiSubject, includeSummary: boolean): SubjectCard | null {
  if (!Number.isInteger(subject.id) || Number(subject.id) <= 0) return null;
  const title = displayTitle(subject);
  if (!title) return null;
  return {
    id: Number(subject.id),
    title: truncate(title, 300),
    originalTitle: truncate(subject.name ?? "", 300),
    summary: includeSummary ? truncate(subject.summary ?? "", 4000) : "",
    ...(preferredCover(subject.images, false) ? { coverUrl: preferredCover(subject.images, false) } : {}),
    ...(preferredCover(subject.images, true) ? { detailCoverUrl: preferredCover(subject.images, true) } : {}),
    ...((subject.date || subject.air_date) ? { airDate: truncate(subject.date || subject.air_date || "", 40) } : {}),
    ...(typeof subject.rating?.score === "number" ? { score: subject.rating.score } : {}),
    ...(typeof subject.rating?.rank === "number" ? { rank: subject.rating.rank } : {}),
  };
}

function toGalleryItems(cards: readonly SubjectCard[]): readonly GalleryItem[] {
  return cards.map(card => {
    const item: Record<string, string | number | boolean | null> = {
      id: String(card.id),
      title: truncate(card.title, 300),
      description: truncate(card.summary || card.originalTitle || card.title, 1000),
    };
    const badge = cardBadge(card);
    if (badge) item.badge = badge;
    const image = remoteCover(card.coverUrl);
    if (image) item.image = image;
    return item;
  });
}

function toStoredFavorite(card: SubjectCard): FavoriteRecord {
  return {
    id: card.id,
    title: truncate(card.title, 160),
    originalTitle: truncate(card.originalTitle, 160),
    summary: "",
    ...(card.coverUrl ? { coverUrl: truncate(card.coverUrl, 500) } : {}),
    ...(card.airDate ? { airDate: truncate(card.airDate, 40) } : {}),
    ...(typeof card.score === "number" ? { score: card.score } : {}),
    ...(typeof card.rank === "number" ? { rank: card.rank } : {}),
  };
}

function cardBadge(card: SubjectCard): string {
  const parts: string[] = [];
  if (typeof card.score === "number" && card.score > 0) parts.push(`评分 ${card.score.toFixed(1)}`);
  if (card.airDate) parts.push(card.airDate);
  return truncate(parts.join(" · "), 100);
}

function remoteCover(url: string | undefined): string | undefined {
  if (!url) return undefined;
  const normalizedUrl = normalizeHttps(url);
  const authority = /^https:\/\/([^/?#]+)(?:[/?#]|$)/i.exec(normalizedUrl)?.[1]?.toLowerCase();
  return authority === "lain.bgm.tv" ? normalizedUrl : undefined;
}

async function getJson<T>(url: string): Promise<T> {
  const response = await canx.http.request({
    url,
    headers: { accept: "application/json", "user-agent": userAgent },
  });
  return parseJsonResponse<T>(response);
}

async function postJson<T>(url: string, body: unknown): Promise<T> {
  const response = await canx.http.request({
    url,
    method: "POST",
    headers: {
      accept: "application/json",
      "content-type": "application/json",
      "user-agent": userAgent,
    },
    body: JSON.stringify(body),
  });
  return parseJsonResponse<T>(response);
}

function parseJsonResponse<T>(response: CanX.HttpResponse): T {
  if (response.status < 200 || response.status >= 300) {
    throw new Error(`Bangumi API 返回 HTTP ${response.status}。`);
  }
  try {
    return JSON.parse(response.body) as T;
  } catch {
    throw new Error("Bangumi API 返回了无效 JSON。");
  }
}

function detailKeyValues(subject: BangumiSubject): readonly Readonly<Record<string, string>>[] {
  const tags = (subject.tags ?? []).slice(0, 12).map(tag => tag.name).filter((tag): tag is string => Boolean(tag));
  const collection = subject.collection;
  return [
    { key: "类型", value: subjectType(subject.type) },
    { key: "首播", value: subject.date || subject.air_date || "-" },
    { key: "平台", value: subject.platform || "-" },
    { key: "话数", value: String(subject.total_episodes || subject.eps || "-") },
    { key: "评分", value: subject.rating?.score ? `${subject.rating.score.toFixed(1)} / 10` : "-" },
    { key: "排名", value: subject.rating?.rank ? `#${subject.rating.rank}` : "-" },
    { key: "评分人数", value: String(subject.rating?.total ?? "-") },
    { key: "在看 / 看过", value: `${collection?.doing ?? 0} / ${collection?.collect ?? 0}` },
    { key: "标签", value: truncate(tags.join("、") || "-", 1000) },
  ];
}

function detailInfobox(subject: BangumiSubject): readonly Readonly<Record<string, string>>[] {
  const result: Readonly<Record<string, string>>[] = [];
  for (const item of (subject.infobox ?? []).slice(0, 30)) {
    if (!item.key) continue;
    const value = typeof item.value === "string"
      ? item.value
      : (item.value ?? []).map(entry => entry.v ?? "").filter(Boolean).join(" / ");
    if (value) result.push({ key: truncate(item.key, 100), value: truncate(value, 1000) });
  }
  return result;
}

function displayTitle(subject: BangumiSubject): string {
  return subject.name_cn?.trim() || subject.name?.trim() || "";
}

function preferredCover(images: BangumiImages | null | undefined, detail: boolean): string | undefined {
  const value = detail
    ? images?.common || images?.medium || images?.large || images?.grid || images?.small
    : images?.small || images?.common || images?.grid || images?.medium || images?.large;
  return value ? normalizeHttps(value) : undefined;
}

function normalizeHttps(value: string): string {
  return value.startsWith("http://") ? `https://${value.slice(7)}` : value;
}

function currentWeekday(): number {
  const day = new Date().getDay();
  return day === 0 ? 7 : day;
}

function subjectType(value: number | undefined): string {
  return value === 2 ? "动画" : value === 1 ? "书籍" : value === 3 ? "音乐" :
    value === 4 ? "游戏" : value === 6 ? "三次元" : "未知";
}

function normalizeHttpProxy(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed) return "";
  const match = /^http:\/\/(\[[0-9a-f:.]+\]|[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?)(?::([0-9]{1,5}))?\/?$/i.exec(trimmed);
  if (!match) return null;
  if (match[2] && (Number(match[2]) < 1 || Number(match[2]) > 65535)) return null;
  return trimmed.endsWith("/") ? trimmed.slice(0, -1) : trimmed;
}

function splitText(value: string, maximumLength: number): readonly string[] {
  const normalized = value.replace(/\r\n/g, "\n").trim();
  if (!normalized) return [""];
  const result: string[] = [];
  for (let offset = 0; offset < normalized.length; offset += maximumLength) {
    result.push(normalized.slice(offset, offset + maximumLength));
  }
  return result.slice(0, 8);
}

function truncate(value: string, maximumLength: number): string {
  return value.length <= maximumLength ? value : `${value.slice(0, Math.max(0, maximumLength - 1))}…`;
}

function errorMessage(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? truncate(error.message, 1000) : fallback;
}
