// SPDX-License-Identifier: GPL-3.0-or-later

export {};

interface ArchiveSearchDocument {
  readonly identifier?: string;
  readonly title?: string;
  readonly description?: string;
}

interface ArchiveSearchResponse {
  readonly response?: {
    readonly docs?: readonly ArchiveSearchDocument[];
    readonly numFound?: number;
  };
}

interface ArchiveFile {
  readonly name?: string;
  readonly format?: string;
  readonly size?: string;
}

interface ArchiveMetadata {
  readonly files?: readonly ArchiveFile[];
}

interface ArchiveResolveData {
  readonly identifier: string;
}

interface SearchRequest {
  readonly query: string;
  readonly limit: number;
  readonly cursor?: string;
}

interface ResolveRequest {
  readonly id: string;
  readonly data?: Partial<ArchiveResolveData>;
}

interface SearchPage {
  readonly items: readonly {
    readonly id: string;
    readonly title: string;
    readonly description?: string;
    readonly thumbnailUrl: string;
    readonly resolveData: ArchiveResolveData;
  }[];
  readonly nextCursor?: string;
}

interface MediaResolution {
  readonly sources: readonly {
    readonly kind: "http";
    readonly uri: string;
    readonly mediaType: "video";
    readonly label: string;
    readonly mimeType: "video/mp4";
  }[];
}

await canx.services.register<SearchRequest, SearchPage>({
  id: "search",
  contract: "org.canx.media.search",
  contractVersion: "1.0",
  title: "Internet Archive video search",
  description: "Searches freely accessible video metadata on Internet Archive.",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: {
    type: "object",
    additionalProperties: false,
    required: ["query", "limit"],
    properties: {
      query: { type: "string", minLength: 1, maxLength: 500 },
      limit: { type: "integer", minimum: 1, maximum: 50 },
      cursor: { type: "string", maxLength: 32 },
    },
  },
  outputSchema: {
    type: "object",
    additionalProperties: false,
    required: ["items"],
    properties: {
      items: {
        type: "array",
        maxItems: 50,
        items: {
          type: "object",
          additionalProperties: false,
          required: ["id", "title", "thumbnailUrl", "resolveData"],
          properties: {
            id: { type: "string", minLength: 1, maxLength: 256 },
            title: { type: "string", minLength: 1, maxLength: 1000 },
            description: { type: "string", maxLength: 500 },
            thumbnailUrl: { type: "string", format: "uri" },
            resolveData: {
              type: "object",
              additionalProperties: false,
              required: ["identifier"],
              properties: { identifier: { type: "string", minLength: 1, maxLength: 256 } },
            },
          },
        },
      },
      nextCursor: { type: "string", maxLength: 32 },
    },
  },
  async invoke(request) {
    const limit = Math.max(1, Math.min(50, Math.trunc(request.limit)));
    const page = parseCursor(request.cursor);
    const query = encodeURIComponent(`mediatype:movies AND (${request.query})`);
    const url = "https://archive.org/advancedsearch.php" +
      `?q=${query}&fl[]=identifier&fl[]=title&fl[]=description&rows=${limit}&page=${page}&output=json`;
    const response = await canx.http.request({ url, headers: { accept: "application/json" } });
    if (response.status !== 200) throw new Error(`Internet Archive search returned HTTP ${response.status}.`);
    const payload = JSON.parse(response.body) as ArchiveSearchResponse;
    const items = (payload.response?.docs ?? [])
      .filter((item): item is ArchiveSearchDocument & { identifier: string; title: string } =>
        typeof item.identifier === "string" && typeof item.title === "string")
      .map(item => ({
        id: item.identifier,
        title: item.title,
        description: text(item.description),
        thumbnailUrl: `https://archive.org/services/img/${encodeURIComponent(item.identifier)}`,
        resolveData: { identifier: item.identifier },
      }));
    const consumed = page * limit;
    const nextCursor = consumed < (payload.response?.numFound ?? 0) ? String(page + 1) : undefined;
    return { items, nextCursor };
  },
});

await canx.services.register<ResolveRequest, MediaResolution>({
  id: "resolve",
  contract: "org.canx.media.resolve",
  contractVersion: "1.0",
  title: "Internet Archive MP4 resolver",
  description: "Resolves an Internet Archive item to its largest playable MP4 files.",
  risk: "network",
  audiences: ["plugin", "ai"],
  inputSchema: {
    type: "object",
    additionalProperties: false,
    required: ["id"],
    properties: {
      id: { type: "string", minLength: 1, maxLength: 256 },
      data: {
        type: "object",
        additionalProperties: false,
        properties: { identifier: { type: "string", minLength: 1, maxLength: 256 } },
      },
    },
  },
  outputSchema: {
    type: "object",
    additionalProperties: false,
    required: ["sources"],
    properties: {
      sources: {
        type: "array",
        minItems: 1,
        maxItems: 4,
        items: {
          type: "object",
          additionalProperties: false,
          required: ["kind", "uri", "mediaType", "label", "mimeType"],
          properties: {
            kind: { const: "http" },
            uri: { type: "string", pattern: "^https://" },
            mediaType: { const: "video" },
            label: { type: "string", minLength: 1 },
            mimeType: { const: "video/mp4" },
          },
        },
      },
    },
  },
  async invoke(request) {
    const data = request.data;
    const identifier = typeof data?.identifier === "string" ? data.identifier : request.id;
    if (!identifier) throw new Error("Internet Archive identifier is missing.");
    const response = await canx.http.request({
      url: `https://archive.org/metadata/${encodeURIComponent(identifier)}`,
      headers: { accept: "application/json" },
    });
    if (response.status !== 200) throw new Error(`Internet Archive metadata returned HTTP ${response.status}.`);
    const metadata = JSON.parse(response.body) as ArchiveMetadata;
    const files = (metadata.files ?? [])
      .filter((file): file is ArchiveFile & { name: string } =>
        typeof file.name === "string" && /\.mp4$/i.test(file.name))
      .sort((left, right) => numericSize(right.size) - numericSize(left.size));
    if (files.length === 0) throw new Error("This item has no playable MP4 file.");
    return {
      sources: files.slice(0, 4).map(file => ({
        kind: "http" as const,
        uri: `https://archive.org/download/${encodeURIComponent(identifier)}/${encodePath(file.name)}`,
        mediaType: "video" as const,
        label: file.format || file.name,
        mimeType: "video/mp4",
      })),
    };
  },
});

function parseCursor(cursor: string | undefined): number {
  const page = Number.parseInt(cursor ?? "1", 10);
  return Number.isSafeInteger(page) && page > 0 ? page : 1;
}

function numericSize(value: string | undefined): number {
  const size = Number.parseInt(value ?? "0", 10);
  return Number.isSafeInteger(size) ? size : 0;
}

function encodePath(path: string): string {
  return path.split("/").map(encodeURIComponent).join("/");
}

function text(value: string | undefined): string | undefined {
  return typeof value === "string" && value.trim() ? value.trim().slice(0, 500) : undefined;
}
