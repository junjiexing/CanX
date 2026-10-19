// SPDX-License-Identifier: GPL-3.0-or-later

import { pathToFileURL } from "node:url";
import { isAllowedHost, readManifest } from "./lib/manifest.mjs";

const playbackReadActions = new Set([
  "get", "capabilities", "chapter.list", "bookmark.list", "audioDevice.list", "shader.list", "diagnostics.get",
]);
const playbackControlActions = new Set([
  "play", "pause", "togglePause", "stop", "seek", "playlist.play", "playlist.next", "playlist.previous",
  "volume.set", "brightness.set", "mute.set", "speed.set", "audioDevice.set", "filter.video.set",
  "filter.audio.set", "filter.clear", "track.select", "subtitle.visibility.set", "subtitle.delay.set",
  "subtitle.style.set", "color.set",
]);
const downloadsReadActions = new Set(["list", "get", "global.get", "capabilities", "connections"]);
const downloadsManageActions = new Set([
  "pause", "resume", "schedule.set", "move",
]);
const uiComponentProperties = Object.freeze({
  stack: ["orientation", "spacing", "horizontalAlignment"],
  viewStack: ["active"],
  grid: ["columns", "spacing"],
  scroll: ["maxHeight"],
  card: ["title"],
  separator: [],
  spacer: ["size"],
  expander: ["title", "expanded"],
  tabs: ["selected"],
  tab: ["label"],
  text: ["text", "tone", "style", "wrap"],
  link: ["label", "url"],
  icon: ["name", "size", "tone"],
  image: ["asset", "source", "alt", "width", "height"],
  badge: ["text", "tone"],
  progress: ["value", "label", "indeterminate"],
  spinner: ["label"],
  alert: ["title", "message", "tone"],
  emptyState: ["title", "message", "icon"],
  keyValue: ["items"],
  button: ["label", "action", "variant", "disabled"],
  textInput: ["label", "key", "placeholder", "default", "disabled", "password"],
  searchInput: ["label", "key", "placeholder", "default", "disabled"],
  textArea: ["label", "key", "placeholder", "default", "rows", "disabled"],
  numberInput: ["label", "key", "placeholder", "default", "min", "max", "step", "disabled"],
  toggle: ["label", "key", "default", "disabled"],
  select: ["label", "key", "options", "default", "disabled"],
  radioGroup: ["label", "key", "options", "default", "disabled"],
  slider: ["label", "key", "default", "min", "max", "step", "disabled"],
  list: ["items", "emptyText", "action", "actionLabel"],
  gallery: ["items", "emptyText", "action", "itemWidth", "imageHeight", "spacing"],
  dataTable: ["columns", "rows", "emptyText", "action"],
});
const uiContainers = new Set(["stack", "viewStack", "grid", "scroll", "card", "expander", "tabs", "tab"]);
const uiLayoutProperties = new Set([
  "column", "columnSpan", "row", "rowSpan", "horizontalAlignment", "verticalAlignment", "width",
]);
const uiTokens = Object.freeze({
  tone: new Set(["default", "muted", "accent", "success", "warning", "error"]),
  style: new Set(["body", "caption", "title", "heading", "mono"]),
  spacing: new Set(["none", "xs", "sm", "md", "lg"]),
  variant: new Set(["primary", "secondary", "quiet", "danger"]),
  size: new Set(["small", "medium", "large"]),
  orientation: new Set(["horizontal", "vertical"]),
  horizontalAlignment: new Set(["left", "center", "right", "stretch"]),
  verticalAlignment: new Set(["top", "center", "bottom", "stretch"]),
  name: new Set([
    "plugin", "home", "search", "globe", "library", "folder", "file", "link", "download", "upload",
    "play", "music", "video", "subtitles", "image", "list", "grid", "history", "favorite", "settings",
    "tools", "info", "warning", "pause", "check",
  ]),
});

export async function createMockHost(options = {}) {
  let manifest = options.manifest;
  if (!manifest && options.workspace) manifest = (await readManifest(options.workspace)).manifest;
  manifest ??= { permissions: [] };
  const permissionMap = new Map((manifest.permissions ?? []).map(permission => [permission.capability, permission.scopes ?? []]));
  const calls = [];
  const storageValues = new Map();
  const secretValues = new Map(Object.entries(options.secrets ?? {}));
  const registeredServices = new Map();
  const contributions = new Map();
  const subscriptions = new Map();
  const downloadPrompts = [];
  const mediaStreams = new Map();
  let pageState = null;
  let configurationState = null;
  let pageGestureActive = false;
  let previousDescriptor;
  let installed = false;
  let defaultHttpProxy = null;

  function validateHttpProxy(value) {
    if (value === null || value === "") return null;
    if (typeof value !== "string") {
      throw hostError("invalid_request", "HTTP proxy must be a string or null.");
    }
    let proxy;
    try { proxy = new URL(value); } catch {
      throw hostError("invalid_request", "HTTP proxy must be an absolute HTTP URL without credentials or a path.");
    }
    if (proxy.protocol !== "http:" || proxy.username || proxy.password ||
        (proxy.pathname !== "" && proxy.pathname !== "/") || proxy.search || proxy.hash) {
      throw hostError("invalid_request", "HTTP proxy must be an absolute HTTP URL without credentials or a path.");
    }
    demandHost(proxy.hostname);
    return value;
  }

  const api = deepFreeze({
    apiVersion: "2.0",
    tools: {
      invoke: async (name, input = {}) => {
        demandScope("tools.invoke", name);
        calls.push({ area: "tools", action: name, input });
        const handler = options.tools?.[name];
        if (!handler) throw hostError("tool_not_found", `Mock Tool '${name}' is not registered.`);
        return handler(input);
      },
    },
    storage: {
      get: async key => (demand("storage"), clone(storageValues.get(validateName(key)) ?? null)),
      set: async (key, value) => (demand("storage"), storageValues.set(validateName(key), clone(value)), null),
      remove: async key => (demand("storage"), storageValues.delete(validateName(key))),
      keys: async () => (demand("storage"), [...storageValues.keys()].sort()),
      clear: async () => (demand("storage"), storageValues.clear(), null),
    },
    secrets: {
      get: async name => (demandScope("secrets", name), secretValues.get(name) ?? null),
      set: async (name, value) => {
        demandScope("secrets", name);
        if (typeof value !== "string") throw new TypeError("Secret value must be a string.");
        secretValues.set(name, value);
        return null;
      },
      remove: async name => (demandScope("secrets", name), secretValues.delete(name)),
    },
    http: {
      request: async request => {
        const url = new URL(request.url);
        demandHost(url.hostname);
        const proxy = request.proxy === undefined ? defaultHttpProxy : validateHttpProxy(request.proxy);
        const effectiveRequest = proxy ? { ...request, proxy } : { ...request };
        calls.push({ area: "http", action: request.method ?? "GET", input: effectiveRequest });
        if (!options.http) throw hostError("http_not_mocked", `No mock response for '${url}'.`);
        return options.http(effectiveRequest);
      },
      setDefaultProxy: async proxy => {
        demand("http");
        defaultHttpProxy = validateHttpProxy(proxy);
        return null;
      },
    },
    html: {
      extract: async request => {
        if (!request || typeof request !== "object" || Array.isArray(request)) {
          throw new TypeError("HTML extraction request must be an object.");
        }
        calls.push({ area: "html", action: "extract", input: request });
        if (!options.html) throw hostError("html_not_mocked", "No mock HTML extractor is registered.");
        return options.html(request);
      },
    },
    services: {
      register: async definition => {
        if (!definition || typeof definition !== "object" || Array.isArray(definition) ||
            typeof definition.id !== "string" || typeof definition.contract !== "string" ||
            typeof definition.contractVersion !== "string" || typeof definition.title !== "string" ||
            typeof definition.invoke !== "function" || !definition.inputSchema || !definition.outputSchema) {
          throw new TypeError("Service definition is invalid.");
        }
        demandServiceScope("services.provide", definition.contract, definition.contract);
        const id = `${manifest.id ?? "org.canx.mock"}/${definition.id}`;
        if (registeredServices.has(id)) throw hostError("service_exists", `Service '${id}' is already registered.`);
        const descriptor = clone({
          id,
          pluginId: manifest.id ?? "org.canx.mock",
          localId: definition.id,
          contract: definition.contract,
          contractVersion: definition.contractVersion,
          title: definition.title,
          ...(definition.description ? { description: definition.description } : {}),
          risk: definition.risk ?? "readOnly",
          audiences: definition.audiences ?? ["plugin"],
          inputSchema: definition.inputSchema,
          outputSchema: definition.outputSchema,
        });
        registeredServices.set(id, { descriptor, handler: definition.invoke });
        return { id };
      },
      list: async (query = {}) => listServices()
        .filter(service => service.descriptor.audiences.includes("plugin"))
        .filter(service => !query.contract || service.descriptor.contract === query.contract)
        .filter(service => query.majorVersion == null ||
          Number(service.descriptor.contractVersion.split(".")[0]) === query.majorVersion)
        .filter(service => allowsService(service.descriptor.id, service.descriptor.contract))
        .map(service => clone(service.descriptor)),
      describe: async serviceId => {
        const service = findService(serviceId);
        if (!service) throw hostError("service_not_found", `Service '${serviceId}' is not registered.`);
        demandServiceScope("services.invoke", serviceId, service.descriptor.contract);
        return clone(service.descriptor);
      },
      invoke: (serviceId, input = null) => invokeService(
        serviceId, input, [manifest.id ?? "org.canx.mock"]),
    },
    playback: createArea(
      "playback", "playback.read", "playback.control", playbackReadActions, playbackControlActions, options.playback),
    downloads: {
      ...createArea(
        "downloads", "downloads.read", "downloads.manage", downloadsReadActions, downloadsManageActions, options.downloads),
      requestAdd: async request => {
        demand("downloads.prompt");
        if (!pageGestureActive) {
          throw hostError("user_gesture_required", "Download confirmation requires a plugin page user event.");
        }
        if (!request || !["magnet", "http"].includes(request.kind) || typeof request.source !== "string") {
          throw hostError("invalid_download_request", "Download confirmation request is invalid.");
        }
        const prompt = clone(request);
        downloadPrompts.push(prompt);
        await options.downloadPrompt?.(prompt);
        return null;
      },
    },
    media: {
      stream: {
        create: async recipe => {
          demand("media.stream");
          const result = await createMediaStream(recipe, false);
          return clone(result);
        },
        release: async id => {
          demand("media.stream");
          return mediaStreams.delete(validateName(id));
        },
        play: async recipe => {
          demand("media.stream");
          if (!pageGestureActive) {
            throw hostError("user_gesture_required", "Managed media playback requires a plugin page user event.");
          }
          const result = await createMediaStream(recipe, true);
          return clone(result);
        },
      },
    },
    ui: {
      configuration: {
        mount: async definition => {
          demand("ui.configuration");
          if (!manifest.configuration) {
            throw hostError("configuration_not_declared", "Manifest does not declare plugin configuration.");
          }
          if (configurationState) throw hostError("configuration_mounted", "Plugin configuration is already mounted.");
          validatePageDefinition(definition, permissionMap);
          if (typeof definition.onEvent !== "function") {
            throw new TypeError("Plugin configuration requires an onEvent handler.");
          }
          configurationState = {
            content: clone(definition.content),
            closeRequested: false,
            definition,
          };
          return Object.freeze({
            render: async content => {
              demand("ui.configuration");
              validatePageContent(content, permissionMap);
              configurationState.content = clone(content);
              return null;
            },
            update: async patches => {
              demand("ui.configuration");
              configurationState.content = applyPagePatches(configurationState.content, patches, permissionMap);
              return null;
            },
            close: async () => {
              demand("ui.configuration");
              configurationState.closeRequested = true;
              return null;
            },
          });
        },
      },
      page: {
        mount: async definition => {
          demand("ui.page");
          if (!manifest.ui) throw hostError("page_not_declared", "Manifest does not declare a plugin page.");
          if (pageState) throw hostError("page_mounted", "Plugin page is already mounted.");
          validatePageDefinition(definition, permissionMap);
          pageState = {
            content: clone(definition.content),
            badge: null,
            definition,
          };
          return Object.freeze({
            render: async content => {
              demand("ui.page");
              validatePageContent(content, permissionMap);
              pageState.content = clone(content);
              return null;
            },
            update: async patches => {
              demand("ui.page");
              pageState.content = applyPagePatches(pageState.content, patches, permissionMap);
              return null;
            },
            setBadge: async badge => {
              demand("ui.page");
              if (!badge || typeof badge !== "object" || Array.isArray(badge) ||
                  typeof badge.text !== "string") {
                throw new TypeError("Plugin page badge must contain string text.");
              }
              pageState.badge = clone({ text: badge.text, tone: badge.tone ?? "default" });
              return null;
            },
            clearBadge: async () => {
              demand("ui.page");
              pageState.badge = null;
              return null;
            },
          });
        },
      },
      contribute: async definition => {
        demand("ui.contribute");
        if (!definition?.id || contributions.has(definition.id)) throw hostError("invalid_ui", "UI contribution id is missing or duplicated.");
        contributions.set(definition.id, definition);
        return { id: definition.id };
      },
    },
  });

  function createArea(area, readCapability, writeCapability, readActions, writeActions, handler) {
    return {
      invoke: async (action, input = {}) => {
        if (readActions.has(action)) demand(readCapability);
        else if (writeActions.has(action)) demand(writeCapability);
        else throw hostError("invalid_action", `Action '${action}' is not part of the ${area} SDK.`);
        calls.push({ area, action, input });
        if (!handler) throw hostError("action_not_mocked", `No ${area} mock handles '${action}'.`);
        return handler(action, input);
      },
      subscribe: async (listener, settings = {}) => {
        demand(readCapability);
        const id = `${area}-${subscriptions.size + 1}`;
        subscriptions.set(id, { area, listener, settings });
        return { cancel: async () => subscriptions.delete(id) };
      },
    };
  }

  async function createMediaStream(recipe, play) {
    if (!recipe || typeof recipe !== "object" || Array.isArray(recipe) || typeof recipe.url !== "string") {
      throw hostError("invalid_media_stream", "Managed media stream recipe is invalid.");
    }
    const url = new URL(recipe.url);
    demandHost(url.hostname);
    for (const hostname of recipe.allowedHosts ?? []) demandHost(hostname);
    const id = `stream-${mediaStreams.size + 1}`;
    const result = { id, source: `canx://stream/mock-${id}/${encodeURIComponent(recipe.displayName ?? "media")}` };
    mediaStreams.set(id, clone(recipe));
    calls.push({ area: "media.stream", action: play ? "play" : "create", input: recipe });
    await options.mediaStream?.(play ? "play" : "create", recipe, result);
    return result;
  }

  function listServices() {
    return [
      ...registeredServices.values(),
      ...Object.values(options.services ?? {}),
    ];
  }

  function findService(serviceId) {
    return registeredServices.get(serviceId) ?? options.services?.[serviceId];
  }

  async function invokeService(serviceId, input, chain) {
    const service = findService(serviceId);
    if (!service) throw hostError("service_not_found", `Service '${serviceId}' is not registered.`);
    demandServiceScope("services.invoke", serviceId, service.descriptor.contract);
    if (!service.descriptor.audiences.includes("plugin")) {
      throw hostError("service_not_visible", `Service '${serviceId}' is not exposed to plugins.`);
    }
    if (chain.includes(service.descriptor.pluginId)) {
      throw hostError("service_call_cycle", `Service call cycle detected at '${serviceId}'.`);
    }
    const nextChain = [...chain, service.descriptor.pluginId];
    calls.push({ area: "services", action: serviceId, input });
    const handler = service.invoke ?? service.handler;
    return clone(await handler(clone(input), Object.freeze({
      invoke: (nestedId, nestedInput = null) => invokeService(nestedId, nestedInput, nextChain),
    })));
  }

  function demand(capability) {
    if (!permissionMap.has(capability)) throw hostError("permission_denied", `Manifest does not grant '${capability}'.`);
  }

  function demandScope(capability, scope) {
    demand(capability);
    if (!permissionMap.get(capability).includes(scope)) throw hostError("permission_denied", `Manifest does not grant '${capability}:${scope}'.`);
  }

  function allowsService(serviceId, contract) {
    const scopes = permissionMap.get("services.invoke") ?? [];
    return scopes.includes("*") || scopes.includes(serviceId) || scopes.includes(contract);
  }

  function demandServiceScope(capability, serviceId, contract) {
    demand(capability);
    const scopes = permissionMap.get(capability) ?? [];
    if (!scopes.includes("*") && !scopes.includes(serviceId) && !scopes.includes(contract)) {
      throw hostError("permission_denied", `Manifest does not grant '${capability}:${serviceId}'.`);
    }
  }

  function demandHost(hostname) {
    demand("http");
    if (!isAllowedHost(hostname, permissionMap.get("http"), true)) throw hostError("permission_denied", `Manifest does not allow HTTP host '${hostname}'.`);
  }

  return {
    api,
    calls,
    get services() {
      return new Map(listServices().map(service => [service.descriptor.id, clone(service.descriptor)]));
    },
    contributions,
    downloadPrompts,
    get page() {
      return pageState
        ? { content: clone(pageState.content), badge: clone(pageState.badge) }
        : null;
    },
    get configuration() {
      return configurationState
        ? { content: clone(configurationState.content), closeRequested: configurationState.closeRequested }
        : null;
    },
    storage: storageValues,
    secrets: secretValues,
    install() {
      if (installed) return;
      previousDescriptor = Object.getOwnPropertyDescriptor(globalThis, "canx");
      Object.defineProperty(globalThis, "canx", { value: api, configurable: true, enumerable: true });
      installed = true;
    },
    async loadPlugin(entry) {
      this.install();
      return import(`${pathToFileURL(entry).href}?canx_mock=${Date.now()}_${Math.random()}`);
    },
    async invokeService(id, input) {
      const service = findService(id);
      if (!service) throw new Error(`Service '${id}' is not registered.`);
      const handler = service.invoke ?? service.handler;
      return clone(await handler(clone(input), Object.freeze({
        invoke: (nestedId, nestedInput = null) => invokeService(
          nestedId, nestedInput, [manifest.id ?? "org.canx.mock"]),
      })));
    },
    async invokeContribution(id, input) {
      const contribution = contributions.get(id);
      if (!contribution) throw new Error(`UI contribution '${id}' is not registered.`);
      const handler = contribution.onInvoke ?? contribution.onChange;
      if (!handler) throw new Error(`UI contribution '${id}' is not interactive.`);
      return handler(clone(input));
    },
    async invokePage(event) {
      if (!pageState) throw new Error("Plugin page is not mounted.");
      const input = clone(event);
      if (input?.type === "activate") return pageState.definition.onActivate?.();
      if (input?.type === "deactivate") return pageState.definition.onDeactivate?.();
      pageGestureActive = input?.type === "action" || input?.type === "change";
      try {
        return await pageState.definition.onEvent?.(input);
      } finally {
        pageGestureActive = false;
      }
    },
    async invokeConfiguration(event) {
      if (!configurationState) throw new Error("Plugin configuration is not mounted.");
      const input = clone(event);
      if (input?.type === "open") return configurationState.definition.onOpen?.();
      if (input?.type === "close") return configurationState.definition.onClose?.();
      return configurationState.definition.onEvent(input);
    },
    async emit(area, value) {
      for (const subscription of subscriptions.values()) {
        if (subscription.area === area) await subscription.listener(clone(value));
      }
    },
    dispose() {
      if (!installed) return;
      if (previousDescriptor) Object.defineProperty(globalThis, "canx", previousDescriptor);
      else delete globalThis.canx;
      installed = false;
      subscriptions.clear();
      pageState = null;
      configurationState = null;
    },
  };
}

function validatePageDefinition(definition, permissionMap) {
  if (!definition || typeof definition !== "object" || Array.isArray(definition)) {
    throw new TypeError("Plugin page definition must be an object.");
  }
  validatePageContent(definition.content, permissionMap);
  for (const callback of ["onEvent", "onActivate", "onDeactivate"]) {
    if (definition[callback] !== undefined && typeof definition[callback] !== "function") {
      throw new TypeError(`Plugin page ${callback} must be a function.`);
    }
  }
}

function validatePageContent(content, permissionMap) {
  if (!content || typeof content !== "object" || Array.isArray(content)) {
    throw new TypeError("Plugin page content must be an object.");
  }
  if (new TextEncoder().encode(JSON.stringify(content)).length > 4 * 1024 * 1024) {
    invalidUi("Plugin page content exceeds 4194304 bytes.");
  }
  validateUiNode(content, null, 1, { count: 0, ids: new Set() }, permissionMap);
}

function validateUiNode(node, parentType, depth, state, permissionMap) {
  demandPlainObject(node, "UI component");
  if (depth > 8 || ++state.count > 256) invalidUi("UI component tree exceeds its depth or node limit.");
  demandOnlyProperties(node, new Set(["id", "type", "props", "children", "layout"]));
  if (typeof node.type !== "string" || !Object.hasOwn(uiComponentProperties, node.type)) {
    invalidUi(`UI component type '${String(node.type)}' is not supported.`);
  }
  if (node.type === "tab" && parentType !== "tabs") invalidUi("Tab components may only be direct children of tabs.");
  if (parentType === "tabs" && node.type !== "tab") invalidUi("Tabs may only contain tab components.");
  if (node.id !== undefined) {
    if (typeof node.id !== "string" || !/^[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?$/.test(node.id)) {
      invalidUi("UI component id must be a lowercase identifier of at most 64 characters.");
    }
    if (state.ids.has(node.id)) invalidUi(`UI component id '${node.id}' is duplicated.`);
    state.ids.add(node.id);
  }
  if (node.props !== undefined) {
    demandPlainObject(node.props, "UI component props");
    demandOnlyProperties(node.props, new Set(uiComponentProperties[node.type]));
    for (const [name, value] of Object.entries(node.props)) {
      validateUiProperty(node.type, name, value, permissionMap);
    }
    if (node.type === "image" && node.props.asset !== undefined && node.props.source !== undefined) {
      invalidUi("Image components cannot specify both an asset and a source.");
    }
  }
  if (node.layout !== undefined) {
    demandPlainObject(node.layout, "UI component layout");
    demandOnlyProperties(node.layout, uiLayoutProperties);
    for (const [name, value] of Object.entries(node.layout)) {
      validateUiProperty(node.type, name, value, permissionMap);
    }
  }
  if (node.children !== undefined) {
    if (!Array.isArray(node.children) || !uiContainers.has(node.type)) {
      invalidUi(`UI component '${node.type}' cannot contain children.`);
    }
    if (["scroll", "expander"].includes(node.type) && node.children.length > 1) {
      invalidUi(`UI component '${node.type}' accepts at most one child.`);
    }
    for (const child of node.children) validateUiNode(child, node.type, depth + 1, state, permissionMap);
  }
  if (node.type === "viewStack") {
    if (!Array.isArray(node.children) || node.children.length === 0 || typeof node.props?.active !== "string") {
      invalidUi("View stack components require props.active and at least one child view.");
    }
    if (node.children.some(child => typeof child.id !== "string") ||
        !node.children.some(child => child.id === node.props.active)) {
      invalidUi("View stack props.active must match a direct child id.");
    }
  }
}

function validateUiProperty(componentType, name, value, permissionMap) {
  if (name === "value" && componentType === "progress") return demandNumber(name, value, 0, 1);
  if ((name === "default" && componentType === "toggle") ||
      ["expanded", "disabled", "indeterminate", "password"].includes(name)) {
    if (typeof value !== "boolean") invalidUi(`UI property '${name}' must be boolean.`);
    return;
  }
  if (name === "options") {
    if (!Array.isArray(value) || value.length < 1 || value.length > 50 ||
        value.some(option => typeof option !== "string" || option.length < 1 || option.length > 100)) {
      invalidUi("Select options must contain 1 to 50 short strings.");
    }
    return;
  }
  if (name === "columns" && componentType === "grid") return demandInteger(name, value, 1, 12);
  if (["selected", "column", "row"].includes(name) || (name === "rows" && componentType === "textArea")) {
    return demandInteger(name, value, 0, 1000);
  }
  if (["columnSpan", "rowSpan"].includes(name)) return demandInteger(name, value, 1, 12);
  if (["width", "height", "maxHeight"].includes(name)) return demandNumber(name, value, 1, 4096);
  if (name === "itemWidth") return demandNumber(name, value, 96, 360);
  if (name === "imageHeight") return demandNumber(name, value, 96, 540);
  if ((name === "default" && ["numberInput", "slider"].includes(componentType)) ||
      ["min", "max", "step"].includes(name)) {
    return demandNumber(name, value, -1_000_000_000, 1_000_000_000);
  }
  if (name === "items") return validateUiItems(componentType, value, permissionMap);
  if (name === "columns") return validateTableColumns(value);
  if (name === "rows") return validateTableRows(value);
  if (name === "asset") return validateRasterAsset(value);
  if (name === "source" && componentType === "image") return validateImageSource(value, permissionMap);
  if (name === "url" && componentType === "link") return validateHttpsLink(value);
  if (typeof value !== "string" || value.length > 1024) {
    invalidUi(`UI property '${name}' must be a string of at most 1024 characters.`);
  }
  if (uiTokens[name] && !uiTokens[name].has(value)) invalidUi(`UI token '${value}' is not allowed for '${name}'.`);
}

function validateUiItems(componentType, items, permissionMap) {
  if (!Array.isArray(items) || items.length > 1000) {
    invalidUi(`UI component '${componentType}' items must be an array of at most 1000 entries.`);
  }
  const keyValue = componentType === "keyValue";
  const allowed = new Set(keyValue ? ["key", "value"] : ["id", "title", "description", "badge", "image"]);
  for (const item of items) {
    demandPlainObject(item, `UI component '${componentType}' item`);
    demandOnlyProperties(item, allowed);
    for (const [name, value] of Object.entries(item)) {
      if (["list", "gallery"].includes(componentType) && name === "image") {
        validateImageSource(value, permissionMap);
      }
      else if (typeof value !== "string" || value.length > 1024) {
        invalidUi(`UI component '${componentType}' item values must be short strings.`);
      }
    }
    if (!(keyValue ? ("key" in item && "value" in item) : ("id" in item && "title" in item))) {
      invalidUi(`UI component '${componentType}' item is missing required fields.`);
    }
  }
}

function validateTableColumns(columns) {
  if (!Array.isArray(columns) || columns.length < 1 || columns.length > 20) {
    invalidUi("Data table columns must contain 1 to 20 entries.");
  }
  for (const column of columns) {
    demandPlainObject(column, "Data table column");
    demandOnlyProperties(column, new Set(["key", "title", "width"]));
    demandShortString("key", column.key, 64);
    demandShortString("title", column.title, 100);
    if (column.width !== undefined) demandNumber("width", column.width, 40, 1000);
  }
}

function validateTableRows(rows) {
  if (!Array.isArray(rows) || rows.length > 1000) invalidUi("Data table rows must be an array of at most 1000 entries.");
  for (const row of rows) {
    demandPlainObject(row, "Data table row");
    for (const [name, value] of Object.entries(row)) {
      const scalar = value === null || ["string", "number", "boolean"].includes(typeof value);
      if (name.length > 64 || !scalar || JSON.stringify(value).length > 1024) {
        invalidUi("Data table cells must contain short scalar values.");
      }
    }
  }
}

function validateRasterAsset(value) {
  if (typeof value !== "string" || value.length < 1 || value.length > 256 || value.includes("\\") ||
      value.startsWith("/") || value.split("/").some(part => !part || part === "." || part === "..") ||
      !/\.(?:png|jpe?g|webp)$/i.test(value)) {
    invalidUi("Image asset must be a portable package-relative PNG, JPEG, or WebP path.");
  }
}

function validateInlineImage(value) {
  const match = typeof value === "string" ? /^data:image\/(png|jpeg|webp);base64,([A-Za-z0-9+/]+={0,2})$/.exec(value) : null;
  if (!match || value.length > 128 * 1024 * 4 / 3 + 64) {
    invalidUi("List item image must be a base64 PNG, JPEG, or WebP data URI.");
  }
  const bytes = Buffer.from(match[2], "base64");
  const valid = bytes.length > 0 && bytes.length <= 128 * 1024 && (
    match[1] === "png" && bytes.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])) ||
    match[1] === "jpeg" && bytes.subarray(0, 3).equals(Buffer.from([0xff, 0xd8, 0xff])) ||
    match[1] === "webp" && bytes.subarray(0, 4).toString() === "RIFF" && bytes.subarray(8, 12).toString() === "WEBP"
  );
  if (!valid) invalidUi("List item image must contain matching raster data of at most 131072 bytes.");
}

function validateImageSource(value, permissionMap) {
  if (typeof value === "string" && value.startsWith("data:")) return validateInlineImage(value);
  let url;
  try { url = new URL(value); } catch {
    invalidUi("Remote image source must be an absolute HTTP(S) URL without embedded credentials.");
  }
  if (typeof value !== "string" || value.length < 1 || value.length > 2048 ||
      !["http:", "https:"].includes(url.protocol) || !url.hostname || url.username || url.password) {
    invalidUi("Remote image source must be an absolute HTTP(S) URL without embedded credentials.");
  }
  const scopes = permissionMap.get("ui.remoteImages") ?? [];
  if (!isAllowedHost(url.hostname, scopes, true)) {
    throw hostError("permission_denied", `Manifest does not allow remote image host '${url.hostname}'.`);
  }
}

function validateHttpsLink(value) {
  let url;
  try { url = new URL(value); } catch { invalidUi("Link URL must be an absolute HTTPS URL of at most 2048 characters."); }
  if (typeof value !== "string" || value.length < 1 || value.length > 2048 || url.protocol !== "https:" || !url.hostname) {
    invalidUi("Link URL must be an absolute HTTPS URL of at most 2048 characters.");
  }
}

function demandPlainObject(value, subject) {
  if (!value || typeof value !== "object" || Array.isArray(value)) invalidUi(`${subject} must be an object.`);
}

function demandOnlyProperties(value, allowed) {
  for (const name of Object.keys(value)) {
    if (!allowed.has(name)) invalidUi(`Property '${name}' is not supported in declarative UI.`);
  }
}

function demandShortString(name, value, maximum) {
  if (typeof value !== "string" || !value.trim() || value.length > maximum) {
    invalidUi(`UI property '${name}' must be a string of at most ${maximum} characters.`);
  }
}

function demandInteger(name, value, minimum, maximum) {
  if (!Number.isInteger(value) || value < minimum || value > maximum) {
    invalidUi(`UI property '${name}' must be an integer from ${minimum} to ${maximum}.`);
  }
}

function demandNumber(name, value, minimum, maximum) {
  if (typeof value !== "number" || !Number.isFinite(value) || value < minimum || value > maximum) {
    invalidUi(`UI property '${name}' must be a finite number from ${minimum} to ${maximum}.`);
  }
}

function invalidUi(message) {
  throw hostError("invalid_ui_contribution", message);
}

function applyPagePatches(current, patches, permissionMap) {
  if (!Array.isArray(patches) || patches.length === 0 || patches.length > 64) {
    throw new TypeError("Plugin page update requires 1 to 64 patches.");
  }
  const content = clone(current);
  const targets = indexPageNodes(content);
  const updated = new Set();
  for (const patch of patches) {
    if (!patch || typeof patch.target !== "string" ||
        !patch.props || typeof patch.props !== "object" || Array.isArray(patch.props) ||
        updated.has(patch.target) || !targets.has(patch.target)) {
      throw hostError("invalid_ui_contribution", "Plugin page patch is invalid.");
    }
    updated.add(patch.target);
    const target = targets.get(patch.target);
    target.props ??= {};
    for (const [key, value] of Object.entries(patch.props)) {
      if (value === null) delete target.props[key];
      else target.props[key] = clone(value);
    }
  }
  validatePageContent(content, permissionMap);
  return content;
}

function indexPageNodes(root) {
  const result = new Map();
  const pending = [root];
  while (pending.length) {
    const node = pending.pop();
    if (typeof node.id === "string") result.set(node.id, node);
    if (Array.isArray(node.children)) pending.push(...node.children);
  }
  return result;
}

function validateName(value) {
  if (typeof value !== "string" || !value) throw new TypeError("Name must be a non-empty string.");
  return value;
}

function hostError(code, message) {
  const error = new Error(message);
  error.code = code;
  return error;
}

function clone(value) {
  return value === undefined ? undefined : structuredClone(value);
}

function deepFreeze(value) {
  for (const child of Object.values(value)) {
    if (child && typeof child === "object") deepFreeze(child);
  }
  return Object.freeze(value);
}
