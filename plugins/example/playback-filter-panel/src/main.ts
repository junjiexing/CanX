// SPDX-License-Identifier: GPL-3.0-or-later

export {};

const filters: Readonly<Record<string, string>> = Object.freeze({
  denoise: "hqdn3d=1.5:1.5:6:6",
  sharpen: "unsharp=5:5:0.8:3:3:0.4",
  grayscale: "format=gray",
});

await canx.ui.contribute({
  id: "filter-panel",
  title: "Video filters",
  kind: "panel",
  slot: "playback.sidebar",
  content: {
    type: "stack",
    props: { orientation: "vertical", spacing: "sm" },
    children: [
      { type: "text", props: { text: "Safe mpv filter presets", style: "title" } },
      { type: "button", props: { label: "Denoise", action: "denoise", variant: "secondary" } },
      { type: "button", props: { label: "Sharpen", action: "sharpen", variant: "secondary" } },
      { type: "button", props: { label: "Grayscale", action: "grayscale", variant: "secondary" } },
      { type: "button", props: { label: "Clear filters", action: "clear", variant: "quiet" } },
    ],
  },
  onInvoke: applyInput,
});

await canx.ui.contribute({
  id: "filter-setting",
  title: "Default video filter",
  kind: "setting",
  slot: "settings.plugins",
  content: {
    type: "select",
    props: {
      label: "Filter preset",
      key: "preset",
      options: ["none", "denoise", "sharpen", "grayscale"],
      default: await loadPreset(),
    },
  },
  onChange: async input => {
    const preset = readValue(input);
    await canx.storage.set("preset", preset);
    return applyPreset(preset);
  },
});

await canx.ui.contribute({
  id: "clear-filters",
  title: "Clear playback filters",
  kind: "command",
  slot: "command.palette",
  content: {
    type: "button",
    props: { label: "Clear playback filters", action: "clear", variant: "quiet" },
  },
  onInvoke: () => applyPreset("clear"),
});

async function applyInput(input: unknown): Promise<unknown> {
  if (!input || typeof input !== "object" || !("action" in input)) {
    throw new TypeError("Filter action is missing.");
  }
  return applyPreset(String(input.action));
}

async function applyPreset(preset: string): Promise<unknown> {
  if (preset === "none" || preset === "clear") {
    await canx.playback.invoke("filter.clear", { type: "video" });
    return { preset: "none" };
  }
  const filter = filters[preset];
  if (!filter) throw new TypeError(`Unknown filter preset '${preset}'.`);
  await canx.playback.invoke("filter.video.set", { filters: filter });
  return { preset };
}

async function loadPreset(): Promise<string> {
  const value = await canx.storage.get("preset");
  return typeof value === "string" && (value === "none" || value in filters) ? value : "none";
}

function readValue(input: unknown): string {
  if (!input || typeof input !== "object" || !("value" in input)) {
    throw new TypeError("Filter setting value is missing.");
  }
  return String(input.value);
}
