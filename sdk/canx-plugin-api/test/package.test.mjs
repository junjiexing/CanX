// SPDX-License-Identifier: GPL-3.0-or-later

import assert from "node:assert/strict";
import { generateKeyPairSync, verify } from "node:crypto";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, test } from "node:test";
import { createPluginPackage, createSignature } from "../lib/package.mjs";

let root;
afterEach(async () => {
  if (root) await rm(root, { recursive: true, force: true });
});

test("creates byte-for-byte deterministic unsigned packages", async () => {
  root = await workspace();
  const first = join(root, "first.canxplugin");
  const second = join(root, "second.canxplugin");

  const firstResult = await createPluginPackage(root, first);
  const secondResult = await createPluginPackage(root, second);

  assert.equal(firstResult.sha256, secondResult.sha256);
  assert.deepEqual(await readFile(first), await readFile(second));
  assert.deepEqual(firstResult.files, ["canx-plugin.json", "dist/helper.mjs", "dist/main.mjs"]);
});

test("automatically packages a manifest-declared raster icon", async () => {
  root = await workspace();
  await mkdir(join(root, "assets"));
  await writeFile(join(root, "assets", "icon.png"), Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]));
  const manifestPath = join(root, "canx-plugin.json");
  const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
  manifest.apiVersion = "2.0";
  manifest.ui = { title: "SDK Test", icon: { asset: "assets/icon.png" } };
  manifest.permissions = [{ capability: "ui.page" }];
  await writeFile(manifestPath, JSON.stringify(manifest));

  const result = await createPluginPackage(root, join(root, "ui.canxplugin"));

  assert.ok(result.files.includes("assets/icon.png"));
});

test("creates a verifiable P-256 signature document", async () => {
  root = await workspace();
  const keyPath = join(root, "signing.pem");
  const { privateKey } = generateKeyPairSync("ec", {
    namedCurve: "prime256v1",
    privateKeyEncoding: { type: "pkcs8", format: "pem" },
  });
  await writeFile(keyPath, privateKey);
  const files = new Map([
    ["canx-plugin.json", Buffer.from("manifest")],
    ["dist/main.mjs", Buffer.from("module")],
  ]);

  const document = JSON.parse((await createSignature(files, keyPath)).toString("utf8"));
  const payload = Buffer.from(`CanXPluginSignatureV1\n${document.files.map(file => `${file.path}\t${file.sha256}\n`).join("")}`);

  assert.equal(document.algorithm, "ecdsa-p256-sha256");
  assert.equal(verify(
    "sha256",
    payload,
    { key: Buffer.from(document.publicKey, "base64"), format: "der", type: "spki", dsaEncoding: "ieee-p1363" },
    Buffer.from(document.signature, "base64")), true);
});

test("never packages the private key used for signing", async () => {
  root = await workspace();
  const keyPath = join(root, "dist", "signing.pem");
  const { privateKey } = generateKeyPairSync("ec", {
    namedCurve: "prime256v1",
    privateKeyEncoding: { type: "pkcs8", format: "pem" },
  });
  await writeFile(keyPath, privateKey);

  const result = await createPluginPackage(root, join(root, "signed.canxplugin"), keyPath);

  assert.ok(result.files.includes("canx-signature.json"));
  assert.ok(!result.files.includes("dist/signing.pem"));
});

async function workspace() {
  const directory = await mkdtemp(join(tmpdir(), "canx-sdk-test-"));
  await mkdir(join(directory, "dist"));
  await writeFile(join(directory, "canx-plugin.json"), JSON.stringify({
    manifestVersion: 1,
    id: "org.canx.sdk-test",
    name: "SDK Test",
    version: "1.0.0",
    apiVersion: "2.0",
    entry: "dist/main.mjs",
  }));
  await writeFile(join(directory, "package.json"), JSON.stringify({ files: ["dist"] }));
  await writeFile(join(directory, "dist", "main.mjs"), "import './helper.mjs';");
  await writeFile(join(directory, "dist", "helper.mjs"), "export const value = 42;");
  return directory;
}
