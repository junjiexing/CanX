// SPDX-License-Identifier: GPL-3.0-or-later

export interface SearchConfigurationDefinition {
  readonly description: string;
  readonly token?: {
    readonly label: string;
    readonly secretName: string;
    readonly helpLabel: string;
    readonly helpUrl: string;
    readonly required: boolean;
  };
}

export interface SearchRuntimeConfiguration {
  readonly proxy?: string;
  readonly token?: string;
}

const proxyStorageKey = "httpProxy";

export async function mountSearchConfiguration(
  definition: SearchConfigurationDefinition,
): Promise<SearchRuntimeConfiguration> {
  let proxy = normalizeProxy(await canx.storage.get<unknown>(proxyStorageKey)) ?? "";
  let token = definition.token ? (await canx.secrets.get(definition.token.secretName) ?? "") : "";
  let draftProxy = proxy;
  let draftToken = "";
  let configuration: CanX.UiConfigurationHandle;

  configuration = await canx.ui.configuration.mount({
    content: content(definition, proxy, tokenStatus(definition, token)),
    async onOpen() {
      proxy = normalizeProxy(await canx.storage.get<unknown>(proxyStorageKey)) ?? "";
      token = definition.token ? (await canx.secrets.get(definition.token.secretName) ?? "") : "";
      draftProxy = proxy;
      draftToken = "";
      await configuration.render(content(definition, proxy, tokenStatus(definition, token)));
    },
    onClose() {
      draftProxy = proxy;
      draftToken = "";
    },
    async onEvent(event) {
      if (event.type === "change" && event.key === "httpProxy") {
        draftProxy = String(event.value ?? "").trim();
        return;
      }
      if (event.type === "change" && event.key === "apiToken" && definition.token) {
        draftToken = String(event.value ?? "").trim();
        return;
      }
      if (event.type !== "action") return;
      if (event.action === "cancel") {
        await configuration.close();
        return;
      }
      if (event.action === "clear-token" && definition.token) {
        await canx.secrets.remove(definition.token.secretName);
        token = "";
        draftToken = "";
        await configuration.render(content(definition, draftProxy, tokenStatus(definition, token)));
        return;
      }
      if (event.action !== "save") return;

      const normalized = normalizeProxy(draftProxy);
      if (normalized === null) {
        await configuration.update([
          { target: "http-proxy", props: { default: draftProxy } },
          {
            target: "configuration-status",
            props: { text: "请输入类似 http://127.0.0.1:7890 的本机 HTTP 代理，或留空。", tone: "error" },
          },
        ]);
        return;
      }
      proxy = normalized;
      if (proxy) await canx.storage.set(proxyStorageKey, proxy);
      else await canx.storage.remove(proxyStorageKey);
      if (definition.token && draftToken) {
        await canx.secrets.set(definition.token.secretName, draftToken);
        token = draftToken;
      }
      draftToken = "";
      await configuration.close();
    },
  });

  return {
    get proxy() { return proxy || undefined; },
    get token() { return token || undefined; },
  };
}

function content(
  definition: SearchConfigurationDefinition,
  proxy: string,
  status: string,
): CanX.UiComponent {
  const children: CanX.UiComponent[] = [
    { type: "text", props: { text: definition.description, tone: "muted" } },
    {
      id: "http-proxy",
      type: "textInput",
      props: {
        label: "HTTP 代理",
        key: "httpProxy",
        placeholder: "http://127.0.0.1:7890",
        default: proxy,
      },
    },
  ];
  if (definition.token) {
    children.push(
      {
        id: "api-token",
        type: "textInput",
        props: {
          label: definition.token.label,
          key: "apiToken",
          placeholder: tokenPlaceholder(definition, status),
          default: "",
          password: true,
        },
      },
      { type: "link", props: { label: definition.token.helpLabel, url: definition.token.helpUrl } },
    );
  }
  children.push(
    { id: "configuration-status", type: "text", props: { text: status, tone: "muted" } },
    {
      type: "stack",
      props: { orientation: "horizontal", spacing: "sm" },
      children: [
        { type: "button", props: { label: "保存", action: "save", variant: "primary" } },
        ...(definition.token
          ? [{ type: "button", props: { label: "清除 Token", action: "clear-token", variant: "danger" } } as CanX.UiComponent]
          : []),
        { type: "button", props: { label: "取消", action: "cancel", variant: "secondary" } },
      ],
    },
  );
  return { type: "stack", props: { spacing: "md" }, children };
}

function tokenPlaceholder(definition: SearchConfigurationDefinition, status: string): string {
  if (!definition.token) return "";
  return status.startsWith("已保存") ? "已保存；留空保持不变" : definition.token.required ? "必填" : "可选";
}

function tokenStatus(definition: SearchConfigurationDefinition, token: string): string {
  if (!definition.token) return "代理留空表示直连。";
  if (token) return "已保存 Token；输入新值并保存可替换，Token 不会显示在界面中。";
  return definition.token.required ? "尚未配置必需的 Token。" : "Token 未配置；当前服务允许不使用 Token。";
}

function normalizeProxy(value: unknown): string | null {
  if (typeof value !== "string" || !value.trim()) return "";
  const trimmed = value.trim();
  const match = /^http:\/\/(127\.0\.0\.1)(?::([0-9]{1,5}))?\/?$/i.exec(trimmed);
  if (!match || (match[2] && (Number(match[2]) < 1 || Number(match[2]) > 65535))) return null;
  return trimmed.endsWith("/") ? trimmed.slice(0, -1) : trimmed;
}
