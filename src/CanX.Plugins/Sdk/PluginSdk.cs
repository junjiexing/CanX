// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Sdk;

public static class PluginSdk
{
    public const string ApiVersion = "2.0";
}

internal static class PluginSdkBootstrap
{
    internal const string FileName = "canx:sdk/v2";

    internal const string Source = """
        (() => {
          "use strict";
          const bridge = globalThis.canx;
          if (!bridge || typeof bridge.invokeTool !== "function") {
            throw new Error("CanX native Tool bridge is unavailable");
          }
          const nativeInvokeTool = bridge.invokeTool.bind(bridge);
          const parseJson = value => JSON.parse(String(value));
          const callHost = (name, input) => {
            let inputJson;
            try {
              inputJson = JSON.stringify(input === undefined ? null : input);
              if (typeof inputJson !== "string") {
                throw new TypeError("Host input did not produce JSON.");
              }
            } catch (_) {
              return Promise.reject(Object.freeze({
                code: "invalid_host_input",
                message: "Host input must be JSON serializable."
              }));
            }
            return nativeInvokeTool(name, inputJson).then(
              parseJson,
              error => Promise.reject(parseJson(error)));
          };
          const invoke = (name, input = {}) => {
            if (typeof name !== "string" || name.length === 0) {
              return Promise.reject(Object.freeze({
                code: "invalid_tool_name",
                message: "Tool name must be a non-empty string."
              }));
            }
            if (name.startsWith("canx:")) {
              return Promise.reject(Object.freeze({
                code: "reserved_tool_name",
                message: "Tool names beginning with 'canx:' are reserved by the host."
              }));
            }
            return callHost(name, input);
          };
          const validateStorageKey = key => {
            if (typeof key !== "string" || key.length === 0) {
              throw new TypeError("Storage key must be a non-empty string.");
            }
            return key;
          };
          const storage = Object.freeze({
            get: key => callHost("canx:storage/get", { key: validateStorageKey(key) }),
            set: (key, value) => callHost("canx:storage/set", { key: validateStorageKey(key), value }),
            remove: key => callHost("canx:storage/remove", { key: validateStorageKey(key) }),
            keys: () => callHost("canx:storage/keys", {}),
            clear: () => callHost("canx:storage/clear", {})
          });
          const validateSecretName = name => {
            if (typeof name !== "string" || name.length === 0) {
              throw new TypeError("Secret name must be a non-empty string.");
            }
            return name;
          };
          const secrets = Object.freeze({
            get: name => callHost("canx:secrets/get", { name: validateSecretName(name) }),
            set: (name, value) => {
              if (typeof value !== "string") {
                throw new TypeError("Secret value must be a string.");
              }
              return callHost("canx:secrets/set", { name: validateSecretName(name), value });
            },
            remove: name => callHost("canx:secrets/remove", { name: validateSecretName(name) })
          });
          const http = Object.freeze({
            request: request => {
              if (!request || typeof request !== "object" || Array.isArray(request)) {
                throw new TypeError("HTTP request must be an object.");
              }
              return callHost("canx:http/request", request);
            },
            setDefaultProxy: proxy => {
              if (proxy !== null && typeof proxy !== "string") {
                throw new TypeError("HTTP default proxy must be a string or null.");
              }
              return callHost("canx:http/set-default-proxy", { proxy });
            }
          });
          const html = Object.freeze({
            extract: request => {
              if (!request || typeof request !== "object" || Array.isArray(request)) {
                throw new TypeError("HTML extraction request must be an object.");
              }
              return callHost("canx:html/extract", request);
            }
          });
          const callbackHandlers = new Map();
          const services = Object.freeze({
            register: definition => {
              if (!definition || typeof definition !== "object" || Array.isArray(definition) ||
                  typeof definition.id !== "string" || typeof definition.contract !== "string" ||
                  typeof definition.contractVersion !== "string" || typeof definition.title !== "string" ||
                  !definition.inputSchema || typeof definition.inputSchema !== "object" ||
                  !definition.outputSchema || typeof definition.outputSchema !== "object" ||
                  typeof definition.invoke !== "function") {
                throw new TypeError("Service definition is invalid.");
              }
              const registrationId = `service:${definition.id}`;
              if (callbackHandlers.has(registrationId)) {
                return Promise.reject(Object.freeze({
                  code: "service_exists",
                  message: "Service is already registered."
                }));
              }
              callbackHandlers.set(registrationId, (input, invocationId) => {
                const context = Object.freeze({
                  invoke: (serviceId, nestedInput) => callHost("canx:services/invoke", {
                    serviceId,
                    input: nestedInput === undefined ? null : nestedInput,
                    parentInvocationId: invocationId
                  })
                });
                return definition.invoke(input, context);
              });
              return callHost("canx:services/register", {
                id: definition.id,
                contract: definition.contract,
                contractVersion: definition.contractVersion,
                title: definition.title,
                description: definition.description,
                risk: definition.risk ?? "readOnly",
                audiences: definition.audiences ?? ["plugin"],
                inputSchema: definition.inputSchema,
                outputSchema: definition.outputSchema
              }).catch(error => {
                callbackHandlers.delete(registrationId);
                throw error;
              });
            },
            list: query => callHost("canx:services/list", query ?? {}).then(result => result.services),
            describe: serviceId => {
              if (typeof serviceId !== "string" || serviceId.length === 0) {
                throw new TypeError("Service id must be a non-empty string.");
              }
              return callHost("canx:services/describe", { serviceId });
            },
            invoke: (serviceId, input = null) => {
              if (typeof serviceId !== "string" || serviceId.length === 0) {
                throw new TypeError("Service id must be a non-empty string.");
              }
              return callHost("canx:services/invoke", { serviceId, input });
            }
          });
          let nextSubscriptionId = 0;
          const invokeSdkAction = (area, action, input = {}) => {
            if (typeof action !== "string" || action.length === 0) {
              throw new TypeError("SDK action must be a non-empty string.");
            }
            return callHost(`canx:${area}/invoke`, { action, input });
          };
          const subscribe = (area, listener, options = {}) => {
            if (typeof listener !== "function") {
              throw new TypeError("Observation listener must be a function.");
            }
            const intervalMs = options.intervalMs === undefined ? 1000 : options.intervalMs;
            if (!Number.isInteger(intervalMs)) {
              throw new TypeError("Observation interval must be an integer.");
            }
            const id = `${area}-${++nextSubscriptionId}`;
            const registrationId = `observe:${id}`;
            callbackHandlers.set(registrationId, listener);
            return callHost("canx:observe/subscribe", { area, id, intervalMs }).then(
              () => Object.freeze({
                cancel: () => callHost("canx:observe/cancel", { id }).then(cancelled => {
                  callbackHandlers.delete(registrationId);
                  return cancelled;
                })
              }),
              error => {
                callbackHandlers.delete(registrationId);
                throw error;
              });
          };
          const playback = Object.freeze({
            invoke: (action, input = {}) => invokeSdkAction("playback", action, input),
            subscribe: (listener, options) => subscribe("playback", listener, options)
          });
          let currentPageGestureToken = null;
          const downloads = Object.freeze({
            invoke: (action, input = {}) => invokeSdkAction("downloads", action, input),
            subscribe: (listener, options) => subscribe("downloads", listener, options),
            requestAdd: request => {
              if (!request || typeof request !== "object" || Array.isArray(request) ||
                  !["magnet", "http"].includes(request.kind) || typeof request.source !== "string") {
                throw new TypeError("Download confirmation requires magnet/http kind and string source.");
              }
              if (currentPageGestureToken === null) {
                return Promise.reject(Object.freeze({
                  code: "user_gesture_required",
                  message: "Download confirmation must be requested directly from a plugin page user event."
                }));
              }
              return callHost("canx:downloads/requestAdd", {
                kind: request.kind,
                source: request.source,
                gestureToken: currentPageGestureToken
              });
            },
          });
          const mediaStreams = Object.freeze({
            create: recipe => {
              if (!recipe || typeof recipe !== "object" || Array.isArray(recipe)) {
                throw new TypeError("Managed media stream recipe must be an object.");
              }
              return callHost("canx:media/stream/create", recipe);
            },
            release: id => {
              if (typeof id !== "string" || id.length === 0) {
                throw new TypeError("Managed media stream id must be a non-empty string.");
              }
              return callHost("canx:media/stream/release", { id });
            },
            play: recipe => {
              if (!recipe || typeof recipe !== "object" || Array.isArray(recipe)) {
                throw new TypeError("Managed media stream recipe must be an object.");
              }
              if (currentPageGestureToken === null) {
                return Promise.reject(Object.freeze({
                  code: "user_gesture_required",
                  message: "Managed media playback must be requested directly from a plugin page user event."
                }));
              }
              return callHost("canx:media/stream/play", {
                recipe,
                gestureToken: currentPageGestureToken
              });
            }
          });
          const media = Object.freeze({ stream: mediaStreams });
          let pageMounted = false;
          let configurationMounted = false;
          const configuration = Object.freeze({
            mount: definition => {
              if (!definition || typeof definition !== "object" || Array.isArray(definition) ||
                  !definition.content || typeof definition.content !== "object" || Array.isArray(definition.content) ||
                  typeof definition.onEvent !== "function") {
                throw new TypeError("Plugin configuration requires content and an onEvent handler.");
              }
              if (definition.onOpen !== undefined && typeof definition.onOpen !== "function") {
                throw new TypeError("Plugin configuration onOpen must be a function.");
              }
              if (definition.onClose !== undefined && typeof definition.onClose !== "function") {
                throw new TypeError("Plugin configuration onClose must be a function.");
              }
              if (configurationMounted) {
                return Promise.reject(Object.freeze({
                  code: "configuration_mounted",
                  message: "Plugin configuration is already mounted."
                }));
              }
              const registrationId = "ui-configuration:main";
              callbackHandlers.set(registrationId, event => {
                if (event && event.type === "open") return definition.onOpen?.();
                if (event && event.type === "close") return definition.onClose?.();
                return definition.onEvent(event);
              });
              configurationMounted = true;
              return callHost("canx:ui/configuration/mount", { content: definition.content }).then(
                () => Object.freeze({
                  render: content => {
                    if (!content || typeof content !== "object" || Array.isArray(content)) {
                      throw new TypeError("Plugin configuration content must be an object.");
                    }
                    return callHost("canx:ui/configuration/render", { content });
                  },
                  update: patches => {
                    if (!Array.isArray(patches) || patches.length === 0) {
                      throw new TypeError("Plugin configuration update requires a non-empty patch array.");
                    }
                    return callHost("canx:ui/configuration/update", { patches });
                  },
                  close: () => callHost("canx:ui/configuration/close", {})
                }),
                error => {
                  configurationMounted = false;
                  callbackHandlers.delete(registrationId);
                  throw error;
                });
            }
          });
          const page = Object.freeze({
            mount: definition => {
              if (!definition || typeof definition !== "object" || Array.isArray(definition) ||
                  !definition.content || typeof definition.content !== "object" || Array.isArray(definition.content)) {
                throw new TypeError("Plugin page requires object content.");
              }
              for (const callback of ["onEvent", "onActivate", "onDeactivate"]) {
                if (definition[callback] !== undefined && typeof definition[callback] !== "function") {
                  throw new TypeError(`Plugin page ${callback} must be a function.`);
                }
              }
              if (pageMounted) {
                return Promise.reject(Object.freeze({
                  code: "page_mounted",
                  message: "Plugin page is already mounted."
                }));
              }
              const registrationId = "ui-page:main";
              const interactive = typeof definition.onEvent === "function" ||
                typeof definition.onActivate === "function" ||
                typeof definition.onDeactivate === "function";
              if (interactive) {
                callbackHandlers.set(registrationId, event => {
                  if (event && event.type === "activate") {
                    return definition.onActivate?.();
                  }
                  if (event && event.type === "deactivate") {
                    return definition.onDeactivate?.();
                  }
                  return definition.onEvent?.(event);
                });
              }
              pageMounted = true;
              return callHost("canx:ui/page/mount", {
                content: definition.content,
                interactive
              }).then(
                () => Object.freeze({
                  render: content => {
                    if (!content || typeof content !== "object" || Array.isArray(content)) {
                      throw new TypeError("Plugin page content must be an object.");
                    }
                    return callHost("canx:ui/page/render", { content });
                  },
                  update: patches => {
                    if (!Array.isArray(patches) || patches.length === 0) {
                      throw new TypeError("Plugin page update requires a non-empty patch array.");
                    }
                    return callHost("canx:ui/page/update", { patches });
                  },
                  setBadge: badge => {
                    if (!badge || typeof badge !== "object" || Array.isArray(badge)) {
                      throw new TypeError("Plugin page badge must be an object.");
                    }
                    return callHost("canx:ui/page/badge", {
                      text: badge.text,
                      tone: badge.tone ?? "default"
                    });
                  },
                  clearBadge: () => callHost("canx:ui/page/badge", { text: null, tone: "default" })
                }),
                error => {
                  pageMounted = false;
                  callbackHandlers.delete(registrationId);
                  throw error;
                });
            }
          });
          const ui = Object.freeze({
            page,
            configuration,
            contribute: definition => {
              if (!definition || typeof definition !== "object" || Array.isArray(definition) ||
                  typeof definition.id !== "string") {
                throw new TypeError("UI contribution must be an object with a string id.");
              }
              const handler = typeof definition.onInvoke === "function"
                ? definition.onInvoke
                : typeof definition.onChange === "function"
                  ? definition.onChange
                  : null;
              const registrationId = `ui:${definition.id}`;
              if (handler) {
                callbackHandlers.set(registrationId, handler);
              }
              const metadata = {
                id: definition.id,
                title: definition.title,
                kind: definition.kind,
                slot: definition.slot,
                content: definition.content,
                interactive: handler !== null
              };
              return callHost("canx:ui/contribute", metadata).catch(error => {
                callbackHandlers.delete(registrationId);
                throw error;
              });
            }
          });
          const formatConsoleValue = value => {
            if (typeof value === "string") return value;
            if (typeof value === "bigint") return `${value}n`;
            if (value instanceof Error) return value.stack || `${value.name}: ${value.message}`;
            if (value === undefined) return "undefined";
            if (typeof value === "function") return `[Function ${value.name || "anonymous"}]`;
            try {
              const seen = new WeakSet();
              return JSON.stringify(value, (_, item) => {
                if (typeof item === "bigint") return `${item}n`;
                if (item && typeof item === "object") {
                  if (seen.has(item)) return "[Circular]";
                  seen.add(item);
                }
                return item;
              });
            } catch (_) {
              try { return String(value); } catch (_) { return "[Unprintable]"; }
            }
          };
          const writeConsole = (level, values) => {
            const message = values.map(formatConsoleValue).join(" ");
            callHost("canx:log/write", { level, message }).catch(() => {});
          };
          const callbackErrorMessage = error => {
            try {
              if (error && (typeof error === "object" || typeof error === "function") &&
                  typeof error.message === "string" && error.message.length > 0) {
                return error.message;
              }
              if (typeof error === "string" && error.length > 0) return error;
            } catch (_) {
              // Fall through when a hostile object throws while reading message.
            }
            return "Callback failed.";
          };
          const consoleApi = Object.freeze({
            debug: (...values) => writeConsole("debug", values),
            log: (...values) => writeConsole("info", values),
            info: (...values) => writeConsole("info", values),
            warn: (...values) => writeConsole("warn", values),
            error: (...values) => writeConsole("error", values)
          });
          Object.defineProperty(globalThis, "__canxInvokeCallback", {
            value: (requestId, registrationId, inputJson) => {
              const handler = callbackHandlers.get(registrationId);
              const finish = payload => callHost("canx:callback/complete", payload).catch(() => {});
              if (!handler) {
                finish({ requestId, ok: false, message: "Callback handler is not registered." });
                return;
              }
              let input;
              try {
                input = JSON.parse(String(inputJson));
              } catch (_) {
                finish({ requestId, ok: false, message: "Callback input is invalid." });
                return;
              }
              Promise.resolve()
                .then(() => {
                  const previousGestureToken = currentPageGestureToken;
                  const isPageGesture = registrationId === "ui-page:main" && input &&
                    (input.type === "action" || input.type === "change");
                  if (isPageGesture) {
                    currentPageGestureToken = requestId;
                  }
                  try {
                    const result = handler(input, requestId);
                    if (isPageGesture && result && typeof result.then === "function") {
                      return Promise.resolve(result).finally(() => {
                        if (currentPageGestureToken === requestId) {
                          currentPageGestureToken = previousGestureToken;
                        }
                      });
                    }
                    currentPageGestureToken = previousGestureToken;
                    return result;
                  } catch (error) {
                    currentPageGestureToken = previousGestureToken;
                    throw error;
                  }
                })
                .then(
                  result => finish({ requestId, ok: true, result: result === undefined ? null : result }),
                  error => finish({
                    requestId,
                    ok: false,
                    message: callbackErrorMessage(error)
                  }));
            },
            writable: false,
            configurable: false,
            enumerable: false
          });
          const sdk = Object.freeze({
            apiVersion: "2.0",
            tools: Object.freeze({ invoke }),
            storage,
            secrets,
            http,
            html,
            services,
            playback,
            downloads,
            media,
            ui
          });
          Object.defineProperty(globalThis, "canx", {
            value: sdk,
            writable: false,
            configurable: false,
            enumerable: true
          });
          Object.defineProperty(globalThis, "console", {
            value: consoleApi,
            writable: false,
            configurable: false,
            enumerable: false
          });
        })();
        """;
}
