// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { lintWorkspace, ManifestError, validateManifest } from "../lib/manifest.mjs";

const valid = {
  manifestVersion: 1,
  id: "org.canx.test",
  name: "Test",
  version: "1.0.0",
  apiVersion: "2.0",
  entry: "dist/main.mjs",
  permissions: [
    { capability: "http", scopes: ["api.example.org", "*.cdn.example.org"] },
    { capability: "storage" },
  ],
};

test("accepts a strict v1 manifest", () => {
  assert.equal(validateManifest(valid), valid);
});

test("validates service scopes", () => {
  assert.doesNotThrow(() => validateManifest({
    ...valid,
    permissions: [{ capability: "services.invoke", scopes: ["org.canx.bittorrent.search", "*"] }],
  }));
  assert.throws(() => validateManifest({
    ...valid,
    permissions: [{ capability: "services.invoke", scopes: ["Invalid"] }],
  }), ManifestError);
});

test("validates remote image host scopes including the global wildcard", () => {
  for (const scope of ["images.example.org", "*.cdn.example.org", "*"]) {
    assert.doesNotThrow(() => validateManifest({
      ...valid,
      permissions: [{ capability: "ui.remoteImages", scopes: [scope] }],
    }));
  }
  for (const scope of ["https://images.example.org", "cdn.*.example.org"]) {
    assert.throws(() => validateManifest({
      ...valid,
      permissions: [{ capability: "ui.remoteImages", scopes: [scope] }],
    }), ManifestError);
  }
});

test("accepts the global HTTP host scope but still rejects URL scopes", () => {
  assert.doesNotThrow(() => validateManifest({
    ...valid,
    permissions: [{ capability: "http", scopes: ["*"] }],
  }));
  assert.throws(() => validateManifest({
    ...valid,
    permissions: [{ capability: "http", scopes: ["https://example.org"] }],
  }), ManifestError);
});

test("published contract matches manifest schema and validator", async () => {
  const contract = JSON.parse(await readFile(new URL("../contract.json", import.meta.url), "utf8"));
  const schema = JSON.parse(await readFile(new URL("../manifest.schema.json", import.meta.url), "utf8"));

  assert.equal(contract.manifestVersion, valid.manifestVersion);
  assert.equal(contract.apiVersion, valid.apiVersion);
  assert.equal(schema.properties.manifestVersion.const, contract.manifestVersion);
  assert.equal(schema.properties.apiVersion.const, contract.apiVersion);
  assert.deepEqual(
    new Set(schema.properties.permissions.items.properties.capability.enum),
    new Set(contract.capabilities),
  );
  for (const apiVersion of ["0.9", "1.0", "1.1", "1.2", "2.1"]) {
    assert.throws(() => validateManifest({ ...valid, apiVersion }), ManifestError);
  }
});

test("accepts built-in and packaged raster UI icons", () => {
  for (const icon of [{ builtin: "search" }, { asset: "assets/icon.png" }]) {
    const manifest = validateManifest({
      ...valid,
      ui: { title: "Resource Search", icon },
      permissions: [...valid.permissions, { capability: "ui.page" }],
    });
    assert.deepEqual(manifest.ui.icon, icon);
  }
});

test("rejects unsafe, vector and inconsistent UI declarations", () => {
  const withUi = icon => ({
    ...valid,
    ui: { title: "Resource Search", icon },
    permissions: [...valid.permissions, { capability: "ui.page" }],
  });
  assert.throws(() => validateManifest({ ...valid, ui: withUi({ builtin: "search" }).ui }), ManifestError);
  assert.throws(() => validateManifest(withUi({ builtin: "unknown" })), ManifestError);
  assert.throws(() => validateManifest(withUi({ asset: "../icon.png" })), ManifestError);
  assert.throws(() => validateManifest(withUi({ asset: "assets/icon.svg" })), ManifestError);
  assert.throws(() => validateManifest(withUi({ builtin: "search", asset: "assets/icon.png" })), ManifestError);
});

test("rejects unknown fields and permission escalation shapes", () => {
  assert.throws(
    () => validateManifest({
      ...valid,
      unexpected: true,
      permissions: [{ capability: "storage", scopes: ["other-plugin"] }],
    }),
    error => error instanceof ManifestError &&
      error.errors.some(message => message.includes("unknown field")) &&
      error.errors.some(message => message.includes("does not accept scopes")),
  );
});

test("rejects nonportable entry paths", () => {
  assert.throws(() => validateManifest({ ...valid, entry: "../main.mjs" }), ManifestError);
});

test("ignores dependency links while retaining package path checks", async () => {
  const root = await mkdtemp(join(tmpdir(), "canx-lint-test-"));
  try {
    await mkdir(join(root, "dist"));
    await mkdir(join(root, "node_modules"));
    await writeFile(join(root, "canx-plugin.json"), JSON.stringify(valid));
    await writeFile(join(root, "dist", "main.mjs"), "export {};");
    await symlink(join(root, "dist"), join(root, "node_modules", "linked-package"), "junction");

    const result = await lintWorkspace(root);

    assert.ok(result.files.includes("dist/main.mjs"));
    assert.ok(!result.files.some(path => path.startsWith("node_modules/")));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
