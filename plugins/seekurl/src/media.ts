// SPDX-License-Identifier: GPL-3.0-or-later

import { siteOrigin, userAgent } from "./api.js";

const maximumPlaylists = 16;
const maximumHosts = 32;
const maximumPlaylistBytes = 1024 * 1024;

// Recipes only describe how to read the original media. CanX/libmpv handles decoding.
export async function prepareMedia(url: string, displayName: string): Promise<CanX.ManagedMediaStreamRecipe> {
  const source = absoluteHttpUrl(url);
  const hosts = new Set([hostOf(source)]);
  const headers = { "User-Agent": userAgent, "Referer": `${siteOrigin}/` };
  const isHls = /\.m3u8(?:[?#]|$)/i.test(source);
  if (/\.mpd(?:[?#]|$)/i.test(source)) throw new Error("该资源使用暂不支持的 DASH 播放地址，请选择其他资源。");
  if (isHls) {
    const pending = [source];
    const visited = new Set<string>();
    while (pending.length) {
      const playlistUrl = pending.shift()!;
      if (visited.has(playlistUrl)) continue;
      if (visited.size >= maximumPlaylists) throw new Error("该资源的播放列表层级或数量过多，请选择其他资源。");
      visited.add(playlistUrl);
      const response = await canx.http.request({
        url: playlistUrl,
        headers,
        maximumResponseBytes: maximumPlaylistBytes,
      });
      if (response.status < 200 || response.status >= 300) {
        throw new Error(`片源返回 HTTP ${response.status}，请换一个资源或重新搜索。`);
      }
      if (response.bodyEncoding !== "text" || response.truncated || !response.body.trimStart().startsWith("#EXTM3U")) {
        throw new Error("片源未返回有效的 HLS 播放列表，请换一个资源。");
      }
      const finalUrl = absoluteHttpUrl(response.url || playlistUrl);
      addHost(finalUrl);
      visited.add(finalUrl);
      for (const line of response.body.split(/\r?\n/)) {
        const value = line.trim();
        if (!value) continue;
        const references = value.startsWith("#")
          ? [...value.matchAll(/\bURI="([^"]+)"/g)].map(match => match[1])
          : [value];
        for (const reference of references) {
          const target = absoluteHttpUrl(reference, finalUrl);
          addHost(target);
          if (/\.m3u8(?:[?#]|$)/i.test(target) && !visited.has(target) && !pending.includes(target)) pending.push(target);
        }
      }
    }
  }
  return {
    url: source,
    displayName: `${displayName.slice(0, 220)}${isHls ? ".m3u8" : ""}`,
    format: isHls ? "hls" : "auto",
    allowedHosts: [...hosts],
    headers,
  };

  function addHost(target: string): void {
    hosts.add(hostOf(target));
    if (hosts.size > maximumHosts) throw new Error("该资源涉及的媒体域名过多，请选择其他资源。");
  }
}

function hostOf(url: string): string {
  return /^https?:\/\/([^/:?#]+)/i.exec(url)![1].toLowerCase();
}

// Avoid browser-only URL APIs: plugins also run in the native QuickJS host.
function absoluteHttpUrl(value: string, base?: string): string {
  const input = value.trim();
  if (!input || /[\s\\\u0000-\u001f\u007f]/.test(input)) throw new Error("片源包含无效的媒体地址。");
  let result = input;
  if (!/^https?:\/\//i.test(result)) {
    if (!base || /^[a-z][a-z0-9+.-]*:/i.test(input)) throw new Error("片源包含不支持的媒体协议。");
    const origin = /^https?:\/\/[^/]+/i.exec(base)![0];
    if (input.startsWith("//")) result = `${base.split(":")[0]}:${input}`;
    else if (input.startsWith("/")) result = origin + input;
    else if (input.startsWith("?")) result = base.replace(/[?#].*$/, "") + input;
    else result = base.replace(/[?#].*$/, "").replace(/\/[^/]*$/, "/") + input;
  }
  const match = /^(https?):\/\/([a-z0-9.-]+)(?::([0-9]{1,5}))?([/?#].*)?$/i.exec(result);
  if (!match || !match[2].includes(".") || match[2].includes("..") ||
      (match[3] && (Number(match[3]) < 1 || Number(match[3]) > 65535))) {
    throw new Error("片源必须使用不含嵌入式凭据的 HTTP(S) 域名地址。");
  }
  const suffix = match[4] ?? "/";
  const marker = suffix.search(/[?#]/);
  const path = marker < 0 ? suffix : suffix.slice(0, marker);
  const tail = marker < 0 ? "" : suffix.slice(marker);
  const parts: string[] = [];
  for (const part of path.split("/")) {
    if (part === "..") parts.pop();
    else if (part && part !== ".") parts.push(part);
  }
  return `${match[1].toLowerCase()}://${match[2].toLowerCase()}${match[3] ? `:${match[3]}` : ""}/${parts.join("/")}${tail}`;
}
