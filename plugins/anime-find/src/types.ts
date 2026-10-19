// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from anime-find, Copyright (c) 2026 cocofhu, MIT.

export type SourceId = "mikan" | "anibt" | "garden";

export interface SourceError {
  readonly source: SourceId | "bangumi" | "stream";
  readonly message: string;
}

export interface AnimeCard {
  id: string;
  title: string;
  score?: number;
  cover?: string;
  pageUrl?: string;
  nameOrig?: string;
  bgmId?: string;
  season?: string;
  format?: string;
  subgroup?: string;
  resourceCount?: number;
  ratingCount?: number;
  tags?: string[];
  sources: SourceId[];
  refs: Partial<Record<SourceId, string>>;
}

export interface TorrentItem {
  title: string;
  displayTitle?: string;
  tags?: string[];
  episode?: string;
  size?: string;
  createdAt?: string;
  magnet?: string;
  torrent?: string;
}

export interface Subgroup {
  label: string;
  source: SourceId;
  updateDay?: string;
  rss?: string;
  items: TorrentItem[];
}

export interface AnimeDetail {
  id: string;
  title: string;
  cover?: string;
  pageUrl?: string;
  bgmId?: string;
  summary?: string;
  nameOrig?: string;
  score?: number;
  ratingCount?: number;
  tags?: string[];
  comments?: readonly {
    readonly nickname: string;
    readonly comment: string;
    readonly rate?: number;
    readonly updatedAt?: string;
  }[];
  sources: SourceId[];
  groups: Subgroup[];
}

export interface SearchResult {
  query: string;
  items: AnimeCard[];
  errors: SourceError[];
  total: number;
  offset: number;
  hasMore: boolean;
}

export interface StreamRule {
  id: string;
  name: string;
  enabled: boolean;
  baseURL: string;
  searchURL: string;
  searchList: string;
  searchName: string;
  searchResult: string;
  chapterRoads: string;
  chapterResult: string;
  chapterName?: string;
  playURL?: string;
  playURLs?: string;
  mediaHosts?: string[];
  headers?: Record<string, string>;
  mediaHeaders?: Record<string, string | Record<string, string>>;
  useWebview?: boolean;
}

export interface StreamEpisode {
  id: string;
  name: string;
  pageUrl: string;
}

export interface StreamSource {
  id: string;
  animeTitle: string;
  ruleId: string;
  ruleName: string;
  lineName: string;
  sourceUrl: string;
  episodes: StreamEpisode[];
}

export interface PluginConfig {
  mikanHost: string;
  anibtHost: string;
  gardenHost: string;
  maxResults: number;
  sources: SourceId[];
  streamEnabled: boolean;
  streamRules: StreamRule[];
}
