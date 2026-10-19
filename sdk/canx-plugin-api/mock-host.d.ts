// SPDX-License-Identifier: GPL-3.0-or-later

import type { PluginManifestV1 } from "./index.d.ts";

export interface MockHostOptions {
  readonly workspace?: string;
  readonly manifest?: PluginManifestV1;
  readonly tools?: Readonly<Record<string, (input: Record<string, unknown>) => unknown | Promise<unknown>>>;
  readonly secrets?: Readonly<Record<string, string>>;
  readonly http?: (request: CanX.HttpRequest) => CanX.HttpResponse | Promise<CanX.HttpResponse>;
  readonly html?: (request: CanX.HtmlExtractRequest) => CanX.HtmlExtractResult | Promise<CanX.HtmlExtractResult>;
  readonly playback?: (action: string, input: Record<string, unknown>) => unknown | Promise<unknown>;
  readonly downloads?: (action: string, input: Record<string, unknown>) => unknown | Promise<unknown>;
  readonly downloadPrompt?: (request: CanX.DownloadAddRequest) => unknown | Promise<unknown>;
  readonly mediaStream?: (
    action: "create" | "play",
    recipe: CanX.ManagedMediaStreamRecipe,
    handle: CanX.ManagedMediaStreamHandle,
  ) => unknown | Promise<unknown>;
  readonly services?: Readonly<Record<string, {
    readonly descriptor: CanX.ServiceDescriptor;
    invoke(input: CanX.JsonValue, context: CanX.ServiceInvocationContext): CanX.JsonValue | Promise<CanX.JsonValue>;
  }>>;
}

export interface MockHost {
  readonly api: CanX.PluginApiV2;
  readonly calls: readonly Readonly<{ area: string; action: string; input: unknown }>[];
  readonly services: ReadonlyMap<string, CanX.ServiceDescriptor>;
  readonly contributions: ReadonlyMap<string, CanX.UiContributionDefinition>;
  readonly downloadPrompts: readonly CanX.DownloadAddRequest[];
  readonly page: Readonly<{
    readonly content: CanX.UiComponent;
    readonly badge: CanX.UiPageBadge | null;
  }> | null;
  readonly configuration: Readonly<{
    readonly content: CanX.UiComponent;
    readonly closeRequested: boolean;
  }> | null;
  readonly storage: ReadonlyMap<string, unknown>;
  readonly secrets: ReadonlyMap<string, string>;
  install(): void;
  loadPlugin(entry: string): Promise<unknown>;
  invokeService(id: string, input: CanX.JsonValue): Promise<CanX.JsonValue>;
  invokeContribution(id: string, input: unknown): Promise<unknown>;
  invokePage(event: CanX.UiPageEvent): Promise<unknown>;
  invokeConfiguration(event: CanX.UiPageEvent): Promise<unknown>;
  emit(area: "playback" | "downloads", value: unknown): Promise<void>;
  dispose(): void;
}

export function createMockHost(options?: MockHostOptions): Promise<MockHost>;
