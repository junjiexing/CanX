// SPDX-License-Identifier: GPL-3.0-or-later

export {};

declare global {
  const canx: CanX.PluginApiV2;

  namespace CanX {
  type PluginCapability =
    | "tools.invoke"
    | "services.provide"
    | "services.invoke"
    | "ui.page"
    | "ui.contribute"
    | "ui.configuration"
    | "ui.remoteImages"
    | "playback.read"
    | "playback.control"
    | "downloads.read"
    | "downloads.manage"
    | "downloads.prompt"
    | "media.stream"
    | "http"
    | "storage"
    | "secrets";

  interface PluginPermission {
    readonly capability: PluginCapability;
    readonly scopes?: readonly string[];
  }

  interface PluginManifestV1 {
    readonly manifestVersion: 1;
    readonly id: string;
    readonly name: string;
    readonly description?: string;
    readonly version: string;
    readonly apiVersion: "2.0";
    readonly entry: string;
    readonly ui?: PluginUiManifest;
    readonly configuration?: PluginConfigurationManifest;
    readonly permissions?: readonly PluginPermission[];
  }

  type UiIconName =
    | "plugin" | "home" | "search" | "globe" | "library" | "folder" | "file" | "link"
    | "download" | "upload" | "play" | "music" | "video" | "subtitles" | "image"
    | "list" | "grid" | "history" | "favorite" | "settings" | "tools" | "info" | "warning";

  type PluginUiIcon =
    | { readonly builtin: UiIconName; readonly asset?: never }
    | { readonly builtin?: never; readonly asset: `assets/${string}.png` | `assets/${string}.jpg` | `assets/${string}.jpeg` | `assets/${string}.webp` };

  interface PluginUiManifest {
    readonly title: string;
    readonly icon: PluginUiIcon;
  }

  interface PluginConfigurationManifest {
    readonly title?: string;
  }

  interface PluginApiV2 {
    readonly apiVersion: "2.0";
    readonly tools: ToolApi;
    readonly storage: StorageApi;
    readonly secrets: SecretsApi;
    readonly http: HttpApi;
    readonly html: HtmlApi;
    readonly services: ServicesApi;
    readonly playback: PlaybackApi;
    readonly downloads: DownloadsApi;
    readonly media: MediaApi;
    readonly ui: UiApi;
  }

  interface HttpApi {
    request(request: HttpRequest): Promise<HttpResponse>;
    setDefaultProxy(proxy: string | null): Promise<null>;
  }

  interface HttpRequest {
    readonly url: string;
    readonly method?: "DELETE" | "GET" | "HEAD" | "OPTIONS" | "PATCH" | "POST" | "PUT";
    readonly headers?: Readonly<Record<string, string>>;
    readonly body?: string;
    readonly responseType?: "text" | "base64";
    readonly proxy?: string;
    readonly maximumResponseBytes?: number;
    readonly truncateResponse?: boolean;
  }

  interface HttpResponse {
    readonly status: number;
    readonly url: string;
    readonly headers: Readonly<Record<string, readonly string[]>>;
    readonly body: string;
    readonly bodyEncoding: "text" | "base64";
    readonly truncated?: boolean;
  }

  type HtmlExtractValue = "text" | "html" | "outerHtml" | "attribute";

  interface HtmlExtractField {
    readonly selector?: string;
    readonly value?: HtmlExtractValue;
    readonly attribute?: string;
    readonly all?: boolean;
  }

  interface HtmlExtractRequest {
    readonly html: string;
    readonly selector: string;
    readonly limit?: number;
    readonly fields: Readonly<Record<string, HtmlExtractField>>;
  }

  interface HtmlExtractResult {
    readonly rows: readonly Readonly<Record<string, string | null | readonly (string | null)[]>>[];
  }

  interface HtmlApi {
    extract(request: HtmlExtractRequest): Promise<HtmlExtractResult>;
  }

  interface Registration {
    readonly id: string;
  }

  type JsonPrimitive = string | number | boolean | null;
  type JsonValue = JsonPrimitive | readonly JsonValue[] | { readonly [key: string]: JsonValue };
  type JsonObject = { readonly [key: string]: JsonValue };

  type ServiceRisk = "readOnly" | "network" | "localWrite" | "destructive";
  type ServiceAudience = "plugin" | "ai";

  interface ServiceDescriptor {
    readonly id: string;
    readonly pluginId: string;
    readonly localId: string;
    readonly contract: string;
    readonly contractVersion: string;
    readonly title: string;
    readonly description?: string;
    readonly risk: ServiceRisk;
    readonly audiences: readonly ServiceAudience[];
    readonly inputSchema: JsonObject;
    readonly outputSchema: JsonObject;
  }

  interface ServiceInvocationContext {
    invoke<TResult = JsonValue>(serviceId: string, input?: JsonValue): Promise<TResult>;
  }

  interface ServiceDefinition<TInput = JsonValue, TOutput = JsonValue> {
    readonly id: string;
    readonly contract: string;
    readonly contractVersion: string;
    readonly title: string;
    readonly description?: string;
    readonly risk?: ServiceRisk;
    readonly audiences?: readonly ServiceAudience[];
    readonly inputSchema: JsonObject;
    readonly outputSchema: JsonObject;
    invoke(input: TInput, context: ServiceInvocationContext): TOutput | Promise<TOutput>;
  }

  interface ServiceQuery {
    readonly contract?: string;
    readonly majorVersion?: number;
  }

  interface ServicesApi {
    register<TInput = JsonValue, TOutput = JsonValue>(
      definition: ServiceDefinition<TInput, TOutput>,
    ): Promise<Registration>;
    list(query?: ServiceQuery): Promise<readonly ServiceDescriptor[]>;
    describe(serviceId: string): Promise<ServiceDescriptor>;
    invoke<TResult = JsonValue>(serviceId: string, input?: JsonValue): Promise<TResult>;
  }

  interface BittorrentSearchRequest {
    readonly query: string;
    readonly limit?: number;
    readonly cursor?: string;
    readonly options?: unknown;
  }

  interface BittorrentSearchResult {
    readonly id: string;
    readonly title: string;
    readonly description?: string;
    readonly infoHash?: string;
    readonly magnetUri?: string;
    readonly torrentUrl?: string;
    readonly detailsUrl?: string;
    readonly thumbnailUrl?: string;
    readonly sizeBytes?: number;
    readonly seeders?: number;
    readonly leechers?: number;
    readonly completed?: number;
    readonly publishedAt?: string;
    readonly categories?: readonly string[];
    readonly source?: string;
    readonly attributes?: unknown;
  }

  interface BittorrentSearchPage {
    readonly items: readonly BittorrentSearchResult[];
    readonly nextCursor?: string;
    readonly total?: number;
  }

  type PlaybackReadAction =
    | "get"
    | "capabilities"
    | "chapter.list"
    | "bookmark.list"
    | "audioDevice.list"
    | "shader.list"
    | "diagnostics.get";

  type PlaybackControlAction =
    | "play"
    | "pause"
    | "togglePause"
    | "stop"
    | "seek"
    | "playlist.play"
    | "playlist.next"
    | "playlist.previous"
    | "volume.set"
    | "brightness.set"
    | "mute.set"
    | "speed.set"
    | "audioDevice.set"
    | "filter.video.set"
    | "filter.audio.set"
    | "filter.clear"
    | "track.select"
    | "subtitle.visibility.set"
    | "subtitle.delay.set"
    | "subtitle.style.set"
    | "color.set";

  type DownloadsReadAction = "list" | "get" | "global.get" | "capabilities" | "connections";

  type DownloadsManageAction =
    | "pause"
    | "resume"
    | "schedule.set"
    | "move";

  interface ObservationOptions {
    readonly intervalMs?: number;
  }

  interface Subscription {
    cancel(): Promise<boolean>;
  }

  interface PlaybackApi {
    invoke<TResult = unknown>(
      action: PlaybackReadAction | PlaybackControlAction,
      input?: Record<string, unknown> | null,
    ): Promise<TResult>;
    subscribe(
      listener: (state: unknown) => void | Promise<void>,
      options?: ObservationOptions,
    ): Promise<Subscription>;
  }

  interface DownloadsApi {
    requestAdd(request: DownloadAddRequest): Promise<null>;
    invoke<TResult = unknown>(
      action: DownloadsReadAction | DownloadsManageAction,
      input?: Record<string, unknown> | null,
    ): Promise<TResult>;
    subscribe(
      listener: (tasks: unknown) => void | Promise<void>,
      options?: ObservationOptions,
    ): Promise<Subscription>;
  }

  interface DownloadAddRequest {
    readonly kind: "magnet" | "http";
    readonly source: string;
  }

  interface ManagedMediaStreamRecipe {
    readonly url: string;
    readonly displayName?: string;
    readonly format?: "auto" | "file" | "hls";
    readonly allowedHosts?: readonly string[];
    readonly headers?: Readonly<Record<string, string>>;
    readonly headersByHost?: Readonly<Record<string, Readonly<Record<string, string>>>>;
  }

  interface ManagedMediaStreamHandle {
    readonly id: string;
    readonly source: string;
  }

  interface MediaStreamApi {
    create(recipe: ManagedMediaStreamRecipe): Promise<ManagedMediaStreamHandle>;
    release(id: string): Promise<boolean>;
    play(recipe: ManagedMediaStreamRecipe): Promise<ManagedMediaStreamHandle>;
  }

  interface MediaApi {
    readonly stream: MediaStreamApi;
  }

  type UiContributionKind = "page" | "panel" | "command" | "menu" | "setting" | "statusCard";
  type UiSlot =
    | "navigation.plugins"
    | "home.panel"
    | "downloads.details"
    | "playback.sidebar"
    | "command.palette"
    | "menu.app"
    | "menu.downloads"
    | "menu.playback"
    | "settings.plugins"
    | "home.status"
    | "downloads.status"
    | "playback.status";
  type UiComponentType =
    | "stack"
    | "viewStack"
    | "grid"
    | "scroll"
    | "card"
    | "spacer"
    | "expander"
    | "tabs"
    | "tab"
    | "text"
    | "link"
    | "image"
    | "button"
    | "badge"
    | "progress"
    | "spinner"
    | "alert"
    | "emptyState"
    | "keyValue"
    | "icon"
    | "separator"
    | "toggle"
    | "select"
    | "textInput"
    | "searchInput"
    | "textArea"
    | "numberInput"
    | "radioGroup"
    | "slider"
    | "list"
    | "gallery"
    | "dataTable";

  interface UiComponentLayout {
    readonly column?: number;
    readonly columnSpan?: number;
    readonly row?: number;
    readonly rowSpan?: number;
    readonly horizontalAlignment?: "left" | "center" | "right" | "stretch";
    readonly verticalAlignment?: "top" | "center" | "bottom" | "stretch";
    readonly width?: number;
  }

  type UiPropertyValue =
    | string
    | number
    | boolean
    | readonly string[]
    | readonly Readonly<Record<string, string | number | boolean | null>>[];

  interface UiComponent {
    readonly id?: string;
    readonly type: UiComponentType;
    readonly props?: Readonly<Record<string, UiPropertyValue>>;
    readonly children?: readonly UiComponent[];
    readonly layout?: UiComponentLayout;
  }

  interface UiContributionDefinition {
    readonly id: string;
    readonly title: string;
    readonly kind: UiContributionKind;
    readonly slot: UiSlot;
    readonly content: UiComponent;
    readonly onInvoke?: (input: unknown) => unknown | Promise<unknown>;
    readonly onChange?: (input: unknown) => unknown | Promise<unknown>;
  }

  type UiPageEvent =
    | { readonly type: "activate" }
    | { readonly type: "deactivate" }
    | {
        readonly type: "action";
        readonly action: string;
        readonly id?: string;
        readonly itemId?: string;
        readonly row?: Readonly<Record<string, string | number | boolean | null>>;
      }
    | { readonly type: "change"; readonly key: string; readonly value: unknown; readonly id?: string };

  interface UiPageDefinition {
    readonly content: UiComponent;
    readonly onEvent?: (event: UiPageEvent) => unknown | Promise<unknown>;
    readonly onActivate?: () => unknown | Promise<unknown>;
    readonly onDeactivate?: () => unknown | Promise<unknown>;
  }

  interface UiPageBadge {
    readonly text: string;
    readonly tone?: "default" | "accent" | "success" | "warning" | "error";
  }

  interface UiPageHandle {
    render(content: UiComponent): Promise<null>;
    update(patches: readonly UiPagePatch[]): Promise<null>;
    setBadge(badge: UiPageBadge): Promise<null>;
    clearBadge(): Promise<null>;
  }

  interface UiPagePatch {
    readonly target: string;
    readonly props: Readonly<Record<string, UiPropertyValue | null>>;
  }

  interface UiPageApi {
    mount(definition: UiPageDefinition): Promise<UiPageHandle>;
  }

  interface UiConfigurationDefinition {
    readonly content: UiComponent;
    readonly onEvent: (event: UiPageEvent) => unknown | Promise<unknown>;
    readonly onOpen?: () => unknown | Promise<unknown>;
    readonly onClose?: () => unknown | Promise<unknown>;
  }

  interface UiConfigurationHandle {
    render(content: UiComponent): Promise<null>;
    update(patches: readonly UiPagePatch[]): Promise<null>;
    close(): Promise<null>;
  }

  interface UiConfigurationApi {
    mount(definition: UiConfigurationDefinition): Promise<UiConfigurationHandle>;
  }

  interface UiApi {
    readonly page: UiPageApi;
    readonly configuration: UiConfigurationApi;
    contribute(definition: UiContributionDefinition): Promise<Registration>;
  }

  interface SecretsApi {
    get(name: string): Promise<string | null>;
    set(name: string, value: string): Promise<null>;
    remove(name: string): Promise<boolean>;
  }

  interface StorageApi {
    get<TValue = unknown>(key: string): Promise<TValue | null>;
    set(key: string, value: unknown): Promise<null>;
    remove(key: string): Promise<boolean>;
    keys(): Promise<readonly string[]>;
    clear(): Promise<null>;
  }

  interface ToolApi {
    invoke<TResult = unknown>(
      name: string,
      input?: Record<string, unknown> | null,
    ): Promise<TResult>;
  }

  interface PluginError {
    readonly code: string;
    readonly message: string;
  }
  }
}

export type PluginManifestV1 = CanX.PluginManifestV1;
export type PluginPermission = CanX.PluginPermission;
