// SPDX-License-Identifier: GPL-3.0-or-later

import { lstat, readFile, readdir } from "node:fs/promises";
import { join, relative, resolve, sep } from "node:path";

const capabilities = new Set([
  "tools.invoke",
  "services.provide",
  "services.invoke",
  "ui.page",
  "ui.contribute",
  "ui.configuration",
  "ui.remoteImages",
  "playback.read",
  "playback.control",
  "downloads.read",
  "downloads.manage",
  "downloads.prompt",
  "media.stream",
  "http",
  "storage",
  "secrets",
]);
const scopedCapabilities = new Set([
  "tools.invoke", "services.provide", "services.invoke", "ui.remoteImages", "http", "secrets",
]);
const manifestFields = new Set([
  "manifestVersion",
  "id",
  "name",
  "description",
  "version",
  "apiVersion",
  "entry",
  "ui",
  "configuration",
  "permissions",
]);
const idPattern = /^[a-z0-9](?:[a-z0-9-]{0,62})(?:\.[a-z0-9](?:[a-z0-9-]{0,62})){1,7}$/;
const versionPattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;
const domainPattern = /^(?:\*\.)?(?:[a-z0-9](?:[a-z0-9-]{0,62})\.)+[a-z0-9](?:[a-z0-9-]{0,62})$/;
const scopePattern = /^[A-Za-z0-9](?:[A-Za-z0-9._:-]{0,254}[A-Za-z0-9])?$/;
const serviceScopePattern = /^[a-z0-9](?:[a-z0-9.-]{0,126}[a-z0-9])?(?:\/[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?)?$/;
const portableUiAssetPattern = /^assets\/(?!.*(?:^|\/)\.{1,2}(?:\/|$))(?!.*[\\:])(?!.*\/\/).{1,249}$/;
const builtinUiIcons = new Set([
  "plugin", "home", "search", "globe", "library", "folder", "file", "link",
  "download", "upload", "play", "music", "video", "subtitles", "image",
  "list", "grid", "history", "favorite", "settings", "tools", "info", "warning",
]);
const workspaceDirectories = new Set([".git", "artifacts", "coverage", "node_modules"]);

export class ManifestError extends Error {
  constructor(errors) {
    super(`Invalid CanX plugin manifest:\n- ${errors.join("\n- ")}`);
    this.name = "ManifestError";
    this.errors = errors;
  }
}

export function validateManifest(value) {
  const errors = [];
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new ManifestError(["manifest must be a JSON object"]);
  }
  for (const field of Object.keys(value)) {
    if (!manifestFields.has(field)) errors.push(`unknown field '${field}'`);
  }
  if (value.manifestVersion !== 1) errors.push("manifestVersion must be 1");
  const id = requiredString(value.id, "id", 128, errors);
  if (id && !idPattern.test(id)) errors.push("id must be a lowercase reverse-domain identifier");
  requiredString(value.name, "name", 80, errors);
  optionalString(value.description, "description", 500, errors);
  const version = requiredString(value.version, "version", 64, errors);
  if (version && !versionPattern.test(version)) errors.push("version must be a SemVer 2.0 version");
  if (value.apiVersion !== "2.0") {
    errors.push("apiVersion must be 2.0");
  }
  const entry = requiredString(value.entry, "entry", 256, errors);
  if (entry) validateEntry(entry, errors);
  validatePermissions(value.permissions, errors);
  validateUi(value.ui, value.permissions, errors);
  validateConfiguration(value.configuration, value.permissions, errors);
  if (errors.length) throw new ManifestError(errors);
  return value;
}

export async function readManifest(workspace) {
  const root = resolve(workspace);
  const path = join(root, "canx-plugin.json");
  const bytes = await readFile(path);
  if (bytes.length > 256 * 1024) throw new ManifestError(["manifest exceeds 262144 bytes"]);
  let value;
  try {
    value = JSON.parse(bytes.toString("utf8"));
  } catch (error) {
    throw new ManifestError([`manifest is not valid JSON: ${error.message}`]);
  }
  return { root, path, manifest: validateManifest(value) };
}

export async function lintWorkspace(workspace) {
  const result = await readManifest(workspace);
  const files = await enumerateFiles(result.root);
  if (!files.includes(result.manifest.entry)) {
    throw new ManifestError([`entry module '${result.manifest.entry}' does not exist with exact casing`]);
  }
  const iconAsset = result.manifest.ui?.icon?.asset;
  if (iconAsset && !files.includes(iconAsset)) {
    throw new ManifestError([`UI icon '${iconAsset}' does not exist with exact casing`]);
  }
  return { ...result, files };
}

export async function enumerateFiles(root, directory = root) {
  const files = [];
  const entries = await readdir(directory, { withFileTypes: true });
  for (const entry of entries.sort((left, right) => left.name.localeCompare(right.name, "en"))) {
    if (directory === root && entry.isDirectory() && workspaceDirectories.has(entry.name)) continue;
    const fullPath = join(directory, entry.name);
    const info = await lstat(fullPath);
    if (info.isSymbolicLink()) throw new ManifestError([`symbolic link is not allowed: ${portable(root, fullPath)}`]);
    if (info.isDirectory()) files.push(...await enumerateFiles(root, fullPath));
    else if (info.isFile()) files.push(portable(root, fullPath));
  }
  return files;
}

export function isAllowedHost(hostname, scopes, allowGlobalWildcard = false) {
  const host = hostname.toLowerCase().replace(/\.$/, "");
  return (scopes ?? []).some(scope => {
    if (allowGlobalWildcard && scope === "*") return true;
    return scope.startsWith("*.")
      ? host.endsWith(scope.slice(1)) && host !== scope.slice(2)
      : host === scope;
  });
}

function validatePermissions(value, errors) {
  if (value === undefined) return;
  if (!Array.isArray(value)) {
    errors.push("permissions must be an array");
    return;
  }
  if (value.length > 64) errors.push("permissions cannot contain more than 64 entries");
  const seenCapabilities = new Set();
  value.forEach((permission, index) => {
    const prefix = `permissions[${index}]`;
    if (!permission || typeof permission !== "object" || Array.isArray(permission)) {
      errors.push(`${prefix} must be an object`);
      return;
    }
    for (const field of Object.keys(permission)) {
      if (field !== "capability" && field !== "scopes") errors.push(`${prefix} has unknown field '${field}'`);
    }
    const capability = requiredString(permission.capability, `${prefix}.capability`, 64, errors);
    if (capability && !capabilities.has(capability)) errors.push(`${prefix}.capability '${capability}' is not supported`);
    if (capability && seenCapabilities.has(capability)) errors.push(`permission '${capability}' is duplicated`);
    seenCapabilities.add(capability);
    const scopes = permission.scopes ?? [];
    if (!Array.isArray(scopes)) {
      errors.push(`${prefix}.scopes must be an array`);
      return;
    }
    if (scopedCapabilities.has(capability) && scopes.length === 0) errors.push(`permission '${capability}' requires at least one scope`);
    if (!scopedCapabilities.has(capability) && scopes.length > 0) errors.push(`permission '${capability}' does not accept scopes`);
    if (scopes.length > 128) errors.push(`${prefix}.scopes cannot contain more than 128 entries`);
    const seenScopes = new Set();
    scopes.forEach(scopeValue => {
      const scope = typeof scopeValue === "string" ? scopeValue.trim() : "";
      if (!scope || scope.length > 256) {
        errors.push(`${prefix}.scopes contains an empty or oversized value`);
        return;
      }
      if (seenScopes.has(scope)) errors.push(`${prefix}.scopes contains duplicate '${scope}'`);
      seenScopes.add(scope);
      if (capability === "http" && scope !== "*" && !domainPattern.test(scope)) errors.push(`${prefix}.scopes HTTP value '${scope}' must be '*', a hostname, or a wildcard subdomain`);
      if (capability === "ui.remoteImages" && scope !== "*" && !domainPattern.test(scope)) errors.push(`${prefix}.scopes remote image value '${scope}' must be '*', a hostname, or a wildcard subdomain`);
      if ((capability === "tools.invoke" || capability === "secrets") && !scopePattern.test(scope)) errors.push(`${prefix}.scopes value '${scope}' is not a valid scoped name`);
      if (["services.provide", "services.invoke"].includes(capability) && scope !== "*" && !serviceScopePattern.test(scope)) errors.push(`${prefix}.scopes value '${scope}' must be '*', a service contract, or a full service id`);
    });
  });
}

function validateUi(value, permissions, errors) {
  const hasPagePermission = Array.isArray(permissions) &&
    permissions.some(permission => permission?.capability === "ui.page");
  if (value === undefined) {
    if (hasPagePermission) errors.push("permission 'ui.page' requires a manifest ui declaration");
    return;
  }
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    errors.push("ui must be an object");
    return;
  }
  for (const field of Object.keys(value)) {
    if (field !== "title" && field !== "icon") errors.push(`ui has unknown field '${field}'`);
  }
  requiredString(value.title, "ui.title", 32, errors);
  if (!hasPagePermission) errors.push("manifest ui declaration requires permission 'ui.page'");
  const icon = value.icon;
  if (!icon || typeof icon !== "object" || Array.isArray(icon)) {
    errors.push("ui.icon must be an object");
    return;
  }
  for (const field of Object.keys(icon)) {
    if (field !== "builtin" && field !== "asset") errors.push(`ui.icon has unknown field '${field}'`);
  }
  const builtin = typeof icon.builtin === "string" && icon.builtin.trim() ? icon.builtin.trim() : undefined;
  const asset = typeof icon.asset === "string" && icon.asset.trim() ? icon.asset.trim() : undefined;
  if ((builtin === undefined) === (asset === undefined)) {
    errors.push("ui.icon must declare exactly one of builtin or asset");
    return;
  }
  if (builtin && !builtinUiIcons.has(builtin)) {
    errors.push(`ui.icon builtin '${builtin}' is not supported`);
  }
  if (asset && (!portableUiAssetPattern.test(asset) || !/\.(?:png|jpe?g|webp)$/i.test(asset))) {
    errors.push("ui.icon asset must be a portable PNG, JPEG, or WebP path below assets/");
  }
}

function validateConfiguration(value, permissions, errors) {
  const hasPermission = Array.isArray(permissions) &&
    permissions.some(permission => permission?.capability === "ui.configuration");
  if (value === undefined) {
    if (hasPermission) errors.push("permission 'ui.configuration' requires a manifest configuration declaration");
    return;
  }
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    errors.push("configuration must be an object");
    return;
  }
  for (const field of Object.keys(value)) {
    if (field !== "title") errors.push(`configuration has unknown field '${field}'`);
  }
  optionalString(value.title, "configuration.title", 48, errors);
  if (value.title !== undefined && typeof value.title === "string" && !value.title.trim()) {
    errors.push("configuration.title cannot be empty");
  }
  if (!hasPermission) errors.push("manifest configuration declaration requires permission 'ui.configuration'");
}

function validateEntry(entry, errors) {
  if (entry.startsWith("/") || entry.includes("\\") || entry.includes(":")) {
    errors.push("entry must be a portable relative path");
    return;
  }
  if (entry.split("/").some(segment => !segment || segment === "." || segment === "..")) errors.push("entry cannot contain empty, '.' or '..' path segments");
  if (!/\.m?js$/i.test(entry)) errors.push("entry must point to a .js or .mjs module");
}

function requiredString(value, field, maximumLength, errors) {
  const normalized = typeof value === "string" ? value.trim() : "";
  if (!normalized) errors.push(`${field} is required`);
  else if (normalized.length > maximumLength) errors.push(`${field} cannot exceed ${maximumLength} characters`);
  return normalized;
}

function optionalString(value, field, maximumLength, errors) {
  if (value === undefined || value === null) return;
  if (typeof value !== "string") errors.push(`${field} must be a string`);
  else if (value.trim().length > maximumLength) errors.push(`${field} cannot exceed ${maximumLength} characters`);
}

function portable(root, path) {
  return relative(root, path).split(sep).join("/");
}
