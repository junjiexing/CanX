// SPDX-License-Identifier: GPL-3.0-or-later

export {};

await canx.ui.contribute({
  id: "welcome",
  title: "Welcome",
  kind: "statusCard",
  slot: "home.status",
  content: {
    type: "stack",
    children: [
      { type: "text", props: { text: "CanX plugin is running." } },
      { type: "badge", props: { text: canx.apiVersion, tone: "success" } },
    ],
  },
});
