// SPDX-License-Identifier: GPL-3.0-or-later

const secretName = "token";

export async function mountConfiguration(): Promise<void> {
  let saved = false;
  let draft = "";
  let configuration: CanX.UiConfigurationHandle;

  configuration = await canx.ui.configuration.mount({
    content: content(saved),
    async onOpen() {
      draft = "";
      try {
        saved = Boolean(await canx.secrets.get(secretName));
        await configuration.render(content(saved));
      } catch {
        await configuration.render(content(false, "无法读取 API Token，请检查系统凭据存储。"));
      }
    },
    onClose() { draft = ""; },
    async onEvent(event) {
      if (event.type === "change" && event.key === "token") {
        draft = String(event.value ?? "").trim();
        return;
      }
      if (event.type !== "action") return;
      if (event.action === "cancel") {
        await configuration.close();
        return;
      }
      try {
        if (event.action === "clear-token") {
          await canx.secrets.remove(secretName);
          saved = false;
          draft = "";
          await configuration.render(content(false));
          return;
        }
        if (event.action !== "save") return;
        if (draft) {
          await canx.secrets.set(secretName, draft);
          saved = true;
        }
        draft = "";
        if (saved) await configuration.close();
        else await configuration.update([{
          target: "token-status",
          props: { text: "请填写 ASSRT API Token。", tone: "error" },
        }]);
      } catch {
        await configuration.update([{
          target: "token-status",
          props: { text: "无法保存 API Token，请检查系统凭据存储。", tone: "error" },
        }]);
      }
    },
  });
}

function content(saved: boolean, error?: string): CanX.UiComponent {
  return {
    type: "stack",
    props: { spacing: "md" },
    children: [
      {
        type: "textInput",
        props: {
          label: "API Token",
          key: "token",
          placeholder: saved ? "已保存；留空则保持不变" : "粘贴 ASSRT API Token",
          default: "",
          password: true,
        },
      },
      { type: "link", props: { label: "在 ASSRT 用户面板获取 Token", url: "https://assrt.net/usercp.php" } },
      {
        id: "token-status",
        type: "text",
        props: {
          text: error ?? (saved ? "Token 已保存。" : "需要 API Token 才能搜索字幕。"),
          tone: error ? "error" : "muted",
        },
      },
      {
        type: "stack",
        props: { orientation: "horizontal", spacing: "sm" },
        children: [
          { type: "button", props: { label: "保存", action: "save", variant: "primary" } },
          { type: "button", props: { label: "清除 Token", action: "clear-token", variant: "danger" } },
          { type: "button", props: { label: "取消", action: "cancel", variant: "secondary" } },
        ],
      },
    ],
  };
}
