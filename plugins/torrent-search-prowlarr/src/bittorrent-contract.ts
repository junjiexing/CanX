// SPDX-License-Identifier: GPL-3.0-or-later

export function bittorrentSearchInputSchema(optionsSchema: CanX.JsonObject): CanX.JsonObject {
  return {
    type: "object",
    additionalProperties: false,
    required: ["query"],
    properties: {
      query: { type: "string", minLength: 1, maxLength: 500 },
      limit: { type: "integer", minimum: 1, maximum: 100 },
      cursor: { type: "string", maxLength: 512 },
      options: optionsSchema,
    },
  };
}

export const bittorrentSearchOutputSchema: CanX.JsonObject = {
  type: "object",
  additionalProperties: false,
  required: ["items"],
  properties: {
    items: {
      type: "array",
      maxItems: 100,
      items: {
        type: "object",
        additionalProperties: false,
        required: ["id", "title"],
        properties: {
          id: { type: "string", minLength: 1, maxLength: 256 },
          title: { type: "string", minLength: 1, maxLength: 1000 },
          description: { type: "string", maxLength: 4096 },
          infoHash: {
            type: "string",
            pattern: "^(?:[0-9A-Fa-f]{40}|[A-Z2-7a-z2-7]{32})$",
          },
          magnetUri: { type: "string", maxLength: 8192 },
          torrentUrl: { type: "string", format: "uri", maxLength: 8192 },
          detailsUrl: { type: "string", format: "uri", maxLength: 8192 },
          thumbnailUrl: { type: "string", format: "uri", maxLength: 8192 },
          sizeBytes: { type: "integer", minimum: 0 },
          seeders: { type: "integer", minimum: 0 },
          leechers: { type: "integer", minimum: 0 },
          completed: { type: "integer", minimum: 0 },
          publishedAt: { type: "string", format: "date-time" },
          categories: {
            type: "array",
            maxItems: 32,
            items: { type: "string", maxLength: 128 },
          },
          source: { type: "string", maxLength: 256 },
          attributes: true,
        },
      },
    },
    nextCursor: { type: "string", maxLength: 512 },
    total: { type: "integer", minimum: 0 },
  },
};

