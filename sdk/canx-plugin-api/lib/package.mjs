// SPDX-License-Identifier: GPL-3.0-or-later

import { createHash, createPrivateKey, createPublicKey, sign } from "node:crypto";
import { lstat, mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, join, relative, resolve, sep } from "node:path";
import { lintWorkspace, ManifestError } from "./manifest.mjs";

const signatureFile = "canx-signature.json";

export async function createPluginPackage(workspace, outputPath, signingKeyPath) {
  const { root, manifest } = await lintWorkspace(workspace);
  const includePaths = await readIncludePaths(root, manifest);
  const signingKey = signingKeyPath ? resolve(signingKeyPath) : undefined;
  const files = new Map();
  files.set("canx-plugin.json", await readFile(join(root, "canx-plugin.json")));
  for (const includePath of includePaths) await addPath(root, includePath, files, signingKey);
  if (!files.has(manifest.entry)) throw new ManifestError([`package files do not include entry '${manifest.entry}'`]);
  if (signingKey) files.set(signatureFile, await createSignature(files, signingKey));
  const archive = createZip(files);
  const destination = resolve(outputPath ?? join(root, "artifacts", `${manifest.id}-${manifest.version}.canxplugin`));
  await mkdir(dirname(destination), { recursive: true });
  await writeFile(destination, archive);
  return { path: destination, sha256: sha256(archive), files: [...files.keys()].sort() };
}

export async function createSignature(files, signingKeyPath) {
  const key = createPrivateKey(await readFile(resolve(signingKeyPath)));
  if (key.asymmetricKeyType !== "ec" || key.asymmetricKeyDetails?.namedCurve !== "prime256v1") {
    throw new Error("Signing key must be an ECDSA P-256 private key.");
  }
  const publicKey = createPublicKey(key).export({ type: "spki", format: "der" });
  const hashes = new Map([...files.entries()]
    .filter(([path]) => path !== signatureFile)
    .map(([path, content]) => [path, sha256(content)]));
  const payload = Buffer.from(
    `CanXPluginSignatureV1\n${[...hashes.entries()].sort(([left], [right]) => compareText(left, right))
      .map(([path, hash]) => `${path}\t${hash}\n`).join("")}`,
    "utf8");
  const signature = sign("sha256", payload, { key, dsaEncoding: "ieee-p1363" });
  return Buffer.from(JSON.stringify({
    formatVersion: 1,
    algorithm: "ecdsa-p256-sha256",
    keyId: sha256(publicKey),
    publicKey: publicKey.toString("base64"),
    signature: signature.toString("base64"),
    files: [...hashes.entries()].sort(([left], [right]) => compareText(left, right))
      .map(([path, hash]) => ({ path, sha256: hash })),
  }, null, 2));
}

export function createZip(files) {
  const names = [...files.keys()].sort(compareText);
  if (names.length > 1024) throw new Error("Plugin package cannot contain more than 1024 files.");
  const caseInsensitive = new Set();
  const localParts = [];
  const centralParts = [];
  let offset = 0;
  let totalBytes = 0;
  for (const name of names) {
    validateArchivePath(name);
    const folded = name.toLowerCase();
    if (caseInsensitive.has(folded)) throw new Error(`Duplicate package path differs only by case: ${name}`);
    caseInsensitive.add(folded);
    const data = Buffer.from(files.get(name));
    if (data.length > 16 * 1024 * 1024) throw new Error(`Package file exceeds 16 MiB: ${name}`);
    totalBytes += data.length;
    if (totalBytes > 64 * 1024 * 1024) throw new Error("Plugin package cannot exceed 64 MiB.");
    const fileName = Buffer.from(name, "utf8");
    const checksum = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6);
    local.writeUInt16LE(0, 8);
    local.writeUInt16LE(0, 10);
    local.writeUInt16LE(0x0021, 12);
    local.writeUInt32LE(checksum, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(fileName.length, 26);
    local.writeUInt16LE(0, 28);
    localParts.push(local, fileName, data);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(0x0314, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(0, 10);
    central.writeUInt16LE(0, 12);
    central.writeUInt16LE(0x0021, 14);
    central.writeUInt32LE(checksum, 16);
    central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(fileName.length, 28);
    central.writeUInt16LE(0, 30);
    central.writeUInt16LE(0, 32);
    central.writeUInt16LE(0, 34);
    central.writeUInt16LE(0, 36);
    central.writeUInt32LE(0x81a40000, 38);
    central.writeUInt32LE(offset, 42);
    centralParts.push(central, fileName);
    offset += local.length + fileName.length + data.length;
  }
  const centralDirectory = Buffer.concat(centralParts);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(0, 4);
  end.writeUInt16LE(0, 6);
  end.writeUInt16LE(names.length, 8);
  end.writeUInt16LE(names.length, 10);
  end.writeUInt32LE(centralDirectory.length, 12);
  end.writeUInt32LE(offset, 16);
  end.writeUInt16LE(0, 20);
  return Buffer.concat([...localParts, centralDirectory, end]);
}

async function readIncludePaths(root, manifest) {
  try {
    const packageJson = JSON.parse(await readFile(join(root, "package.json"), "utf8"));
    if (packageJson.files !== undefined && (!Array.isArray(packageJson.files) || packageJson.files.some(path => typeof path !== "string"))) {
      throw new Error("package.json files must be an array of relative paths.");
    }
    return [...new Set([
      manifest.entry,
      ...(manifest.ui?.icon?.asset ? [manifest.ui.icon.asset] : []),
      ...(packageJson.files ?? []),
    ])];
  } catch (error) {
    if (error.code === "ENOENT") {
      return [manifest.entry, ...(manifest.ui?.icon?.asset ? [manifest.ui.icon.asset] : [])];
    }
    throw error;
  }
}

async function addPath(root, relativePath, files, excludedPath) {
  validateArchivePath(relativePath);
  const fullPath = resolve(root, ...relativePath.split("/"));
  const rootPrefix = root.endsWith(sep) ? root : `${root}${sep}`;
  if (!fullPath.startsWith(rootPrefix)) throw new Error(`Package path escapes workspace: ${relativePath}`);
  if (excludedPath && fullPath === excludedPath) return;
  const info = await lstat(fullPath);
  if (info.isSymbolicLink()) throw new Error(`Package path cannot be a symbolic link: ${relativePath}`);
  if (info.isDirectory()) {
    const { readdir } = await import("node:fs/promises");
    for (const child of (await readdir(fullPath)).sort()) {
      await addPath(root, `${relativePath.replace(/\/$/, "")}/${child}`, files, excludedPath);
    }
  } else if (info.isFile()) {
    const portable = relative(root, fullPath).split(sep).join("/");
    if (portable === signatureFile) throw new Error(`${signatureFile} is generated and cannot be supplied by the workspace.`);
    if (portable !== "canx-plugin.json") files.set(portable, await readFile(fullPath));
  }
}

function validateArchivePath(path) {
  if (!path || path.startsWith("/") || path.includes("\\") || path.includes(":") ||
      path.split("/").some(segment => !segment || segment === "." || segment === "..")) {
    throw new Error(`Unsafe package path: ${path}`);
  }
}

function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}

function compareText(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}

const crcTable = Array.from({ length: 256 }, (_, index) => {
  let value = index;
  for (let bit = 0; bit < 8; bit++) value = (value & 1) ? (0xedb88320 ^ (value >>> 1)) : (value >>> 1);
  return value >>> 0;
});

function crc32(buffer) {
  let value = 0xffffffff;
  for (const byte of buffer) value = crcTable[(value ^ byte) & 0xff] ^ (value >>> 8);
  return (value ^ 0xffffffff) >>> 0;
}
