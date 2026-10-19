// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from anime-find, Copyright (c) 2026 cocofhu, MIT.

import type { AnimeCard } from "./types.js";

export const userAgent = "CanX-Anime-Find/1.0 (+https://github.com/junjiexing/CanX)";

export async function requestText(
  url: string,
  init: {
    method?: "GET" | "POST";
    headers?: Record<string, string>;
    body?: string;
    maximumResponseBytes?: number;
    truncateResponse?: boolean;
  } = {},
): Promise<{ body: string; url: string }> {
  const response = await canx.http.request({
    url,
    method: init.method,
    headers: { accept: "*/*", "user-agent": userAgent, ...init.headers },
    body: init.body,
    maximumResponseBytes: init.maximumResponseBytes,
    truncateResponse: init.truncateResponse,
  });
  if (response.status < 200 || response.status >= 300) {
    throw new Error(`HTTP ${response.status} ${url}`);
  }
  if (response.bodyEncoding !== "text") throw new Error("上游返回了非文本响应。");
  return { body: response.body, url: response.url };
}

export async function requestJson<T>(
  url: string,
  init: { method?: "GET" | "POST"; body?: string } = {},
): Promise<T> {
  const response = await requestText(url, {
    ...init,
    headers: {
      accept: "application/json",
      ...(init.body ? { "content-type": "application/json" } : {}),
    },
  });
  return JSON.parse(response.body) as T;
}

export function absoluteUrl(base: string, value: string | undefined): string {
  const target = String(value ?? "").trim().replace(/&amp;/g, "&");
  if (!target) return "";
  if (/^https?:\/\//i.test(target)) return target;
  const origin = originOf(base);
  if (!origin) return "";
  if (target.startsWith("//")) return `${origin.slice(0, origin.indexOf(":"))}:${target}`;
  if (target.startsWith("/")) return origin + normalizePath(target);
  const path = base.replace(/[?#].*$/, "").replace(/\/[^/]*$/, "/");
  return normalizeAbsolute(path + target);
}

export function originOf(value: string): string {
  return /^(https?):\/\/([^/?#]+)/i.exec(value)?.slice(1, 3).join("://") ?? "";
}

export function hostOf(value: string): string {
  return /^(?:https?):\/\/([^/?#:]+)/i.exec(value)?.[1]?.toLowerCase() ?? "";
}

function normalizeAbsolute(value: string): string {
  const origin = originOf(value);
  if (!origin) return "";
  const suffix = value.slice(origin.length);
  const marker = suffix.search(/[?#]/);
  const pathname = marker < 0 ? suffix : suffix.slice(0, marker);
  const tail = marker < 0 ? "" : suffix.slice(marker);
  return origin + normalizePath(pathname) + tail;
}

function normalizePath(value: string): string {
  const output: string[] = [];
  for (const part of value.split("/")) {
    if (!part || part === ".") continue;
    if (part === "..") output.pop();
    else output.push(part);
  }
  return "/" + output.join("/");
}

export function hostMatches(candidate: string, allowed: string): boolean {
  const host = candidate.toLowerCase();
  const scope = allowed.toLowerCase();
  return host === scope || host.endsWith(`.${scope}`);
}

export function stableId(...parts: string[]): string {
  let first = 0x811c9dc5;
  let second = 0x9e3779b9;
  const input = parts.join("\u0000");
  for (let index = 0; index < input.length; index++) {
    const code = input.charCodeAt(index);
    first = Math.imul(first ^ code, 0x01000193) >>> 0;
    second = Math.imul(second ^ (code + index), 0x85ebca6b) >>> 0;
  }
  return first.toString(16).padStart(8, "0") + second.toString(16).padStart(8, "0");
}

export function text(value: unknown, maximum = 1000): string | undefined {
  const result = typeof value === "string" ? value.trim() : "";
  return result ? result.slice(0, maximum) : undefined;
}

export function number(value: unknown): number | undefined {
  const result = Number(value);
  return Number.isFinite(result) && result >= 0 ? result : undefined;
}

export function integer(value: unknown): number | undefined {
  const result = number(value);
  return result !== undefined && Number.isSafeInteger(result) ? result : undefined;
}

export function clamp(value: unknown, minimum: number, maximum: number, fallback: number): number {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? Math.max(minimum, Math.min(maximum, Math.trunc(parsed))) : fallback;
}

export function errorMessage(error: unknown): string {
  if (error && typeof error === "object" && "message" in error) return String(error.message).slice(0, 300);
  return String(error).slice(0, 300);
}

export function normalizeTitle(title: string): string {
  return title
    .toLowerCase()
    .replace(/[（(].*?[）)]/g, "")
    .replace(/[~～].*$/u, "")
    .replace(/第[一二三四五六七八九十\d]+季/g, "")
    .replace(/season\s*\d+/gi, "")
    .replace(/[^\p{L}\p{N}]+/gu, "")
    .trim();
}

export function mergeCards(lists: readonly AnimeCard[][]): AnimeCard[] {
  const map = new Map<string, AnimeCard>();
  for (const card of lists.flat()) {
    const key = card.bgmId ? `bgm:${card.bgmId}` : `title:${normalizeTitle(card.title) || card.id}`;
    const current = map.get(key);
    if (!current) {
      map.set(key, { ...card, refs: { ...card.refs }, sources: [...card.sources] });
      continue;
    }
    current.sources = [...new Set([...current.sources, ...card.sources])];
    current.refs = { ...card.refs, ...current.refs };
    current.cover ||= card.cover;
    current.pageUrl ||= card.pageUrl;
    current.bgmId ||= card.bgmId;
    current.nameOrig ||= card.nameOrig;
    current.score ??= card.score;
    current.ratingCount ??= card.ratingCount;
    current.tags ||= card.tags;
    current.resourceCount = (current.resourceCount ?? 0) + (card.resourceCount ?? 0) || undefined;
    if (card.title.length < current.title.length) current.title = card.title;
  }
  return [...map.values()].sort((left, right) =>
    right.sources.length - left.sources.length || (right.score ?? 0) - (left.score ?? 0));
}

export function parseSize(value: string | undefined): number | undefined {
  const match = String(value ?? "").match(/([\d.]+)\s*(B|KB|KiB|MB|MiB|GB|GiB|TB|TiB)/i);
  if (!match) return undefined;
  const unit = match[2].toLowerCase().replace("ib", "b");
  const power = ["b", "kb", "mb", "gb", "tb"].indexOf(unit);
  const bytes = Number(match[1]) * 1024 ** power;
  return Number.isSafeInteger(Math.round(bytes)) ? Math.round(bytes) : undefined;
}

export function infoHash(magnet: string | undefined): string | undefined {
  const hash = String(magnet ?? "").match(/[?&]xt=urn:btih:([A-Za-z0-9]+)/i)?.[1];
  return hash && (/^[0-9a-f]{40}$/i.test(hash) || /^[A-Z2-7]{32}$/i.test(hash))
    ? hash.toUpperCase()
    : undefined;
}
