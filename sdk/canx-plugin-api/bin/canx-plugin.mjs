#!/usr/bin/env node
// SPDX-License-Identifier: GPL-3.0-or-later

import { generateKeyPairSync } from "node:crypto";
import { cp, mkdir, readFile, stat, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { lintWorkspace } from "../lib/manifest.mjs";
import { createPluginPackage } from "../lib/package.mjs";

const packageRoot = fileURLToPath(new URL("..", import.meta.url));

try {
  await main(process.argv.slice(2));
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
}

async function main(arguments_) {
  const [command, ...argumentsForCommand] = arguments_;
  switch (command) {
    case "init":
      await initializePlugin(argumentsForCommand);
      break;
    case "lint":
      await lintPlugin(argumentsForCommand);
      break;
    case "pack":
      await packPlugin(argumentsForCommand);
      break;
    case "keygen":
      await generateSigningKey(argumentsForCommand);
      break;
    case "help":
    case "--help":
    case "-h":
    case undefined:
      printUsage();
      break;
    default:
      throw new Error(`Unknown command '${command}'. Run canx-plugin --help for usage.`);
  }
}

async function initializePlugin(arguments_) {
  const { positionals, options } = parseArguments(arguments_, new Set(["id", "name"]));
  if (positionals.length !== 1 || !options.id || !options.name) {
    throw new Error("Usage: canx-plugin init <directory> --id <reverse-domain-id> --name <name>");
  }

  const destination = resolve(positionals[0]);
  if (await exists(destination)) throw new Error(`Destination already exists: ${destination}`);

  const template = join(packageRoot, "templates", "basic");
  await cp(template, destination, { recursive: true, errorOnExist: true, force: false });

  const manifestPath = join(destination, "canx-plugin.json");
  const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
  manifest.id = options.id;
  manifest.name = options.name;
  await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);

  const packagePath = join(destination, "package.json");
  const packageJson = JSON.parse(await readFile(packagePath, "utf8"));
  packageJson.name = options.id.replaceAll(".", "-");
  await writeFile(packagePath, `${JSON.stringify(packageJson, null, 2)}\n`);

  await lintWorkspace(destination);
  console.log(`CANX_PLUGIN_INIT_OK output=${destination}`);
}

async function lintPlugin(arguments_) {
  const { positionals, options } = parseArguments(arguments_, new Set());
  rejectOptions(options);
  if (positionals.length > 1) throw new Error("Usage: canx-plugin lint [workspace]");
  const result = await lintWorkspace(positionals[0] ?? ".");
  console.log(
    `CANX_PLUGIN_LINT_OK id=${result.manifest.id} version=${result.manifest.version} files=${result.files.length}`,
  );
}

async function packPlugin(arguments_) {
  const { positionals, options } = parseArguments(arguments_, new Set(["key", "out", "output"]));
  if (positionals.length > 1) {
    throw new Error("Usage: canx-plugin pack [workspace] [--output <path>] [--key <private-key.pem>]");
  }
  if (options.output && options.out) throw new Error("Use either '--out' or '--output', not both.");
  const result = await createPluginPackage(
    positionals[0] ?? ".",
    options.output ?? options.out,
    options.key,
  );
  console.log(
    `CANX_PLUGIN_PACKAGE_OK output=${result.path} files=${result.files.length} sha256=${result.sha256}`,
  );
}

async function generateSigningKey(arguments_) {
  const { positionals, options } = parseArguments(arguments_, new Set());
  rejectOptions(options);
  if (positionals.length !== 1) throw new Error("Usage: canx-plugin keygen <private-key.pem>");

  const destination = resolve(positionals[0]);
  if (await exists(destination)) throw new Error(`Refusing to overwrite existing key: ${destination}`);
  const { privateKey } = generateKeyPairSync("ec", {
    namedCurve: "prime256v1",
    privateKeyEncoding: { type: "pkcs8", format: "pem" },
  });
  await mkdir(dirname(destination), { recursive: true });
  await writeFile(destination, privateKey, { mode: 0o600, flag: "wx" });
  console.log(`CANX_PLUGIN_KEYGEN_OK output=${destination}`);
}

function parseArguments(arguments_, allowedOptions) {
  const positionals = [];
  const options = {};
  for (let index = 0; index < arguments_.length; index++) {
    const argument = arguments_[index];
    if (!argument.startsWith("--")) {
      positionals.push(argument);
      continue;
    }

    const separator = argument.indexOf("=");
    const name = argument.slice(2, separator === -1 ? undefined : separator);
    if (!allowedOptions.has(name)) throw new Error(`Unknown option '--${name}'.`);
    const value = separator === -1 ? arguments_[++index] : argument.slice(separator + 1);
    if (!value || value.startsWith("--")) throw new Error(`Option '--${name}' requires a value.`);
    if (options[name] !== undefined) throw new Error(`Option '--${name}' was specified more than once.`);
    options[name] = value;
  }
  return { positionals, options };
}

function rejectOptions(options) {
  const names = Object.keys(options);
  if (names.length) throw new Error(`Unexpected option '--${names[0]}'.`);
}

async function exists(path) {
  try {
    await stat(path);
    return true;
  } catch (error) {
    if (error?.code === "ENOENT") return false;
    throw error;
  }
}

function printUsage() {
  console.log(`CanX plugin development CLI

Usage:
  canx-plugin init <directory> --id <id> --name <name>
  canx-plugin lint [workspace]
  canx-plugin pack [workspace] [--output <path>] [--key <private-key.pem>]
  canx-plugin keygen <private-key.pem>`);
}
