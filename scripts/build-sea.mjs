#!/usr/bin/env node
// Build a single-file SEA (Node Single Executable Application) for the current platform.
// Output: dist/<platform>-<arch>/ZenProxy.exe (Windows) or dist/<platform>-<arch>/zen-proxy (Linux/macOS)

import { spawnSync } from "node:child_process";
import { chmodSync, copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const projDir = dirname(dirname(fileURLToPath(import.meta.url)));
const platform = process.platform === "win32" ? "windows" : process.platform === "darwin" ? "macos" : "linux";
const rid = `${platform}-${process.arch}`;
const distDir = join(projDir, "dist", rid);
const outName = process.platform === "win32" ? "ZenProxy.exe" : "zen-proxy";
const outPath = join(distDir, outName);
const FUSE = "NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2";

function findNpxCli() {
  if (process.platform !== "win32") return "npx";
  const r = spawnSync("where.exe", ["npx"], { encoding: "utf8" });
  const line = (r.stdout || "").split(/\r?\n/).find((l) => /\.cmd$/i.test(l.trim()));
  if (!line) return "npx";
  const cli = join(dirname(line.trim()), "node_modules", "npm", "bin", "npx-cli.js");
  return existsSync(cli) ? cli : "npx";
}

const npxCli = findNpxCli();

function run(cmd, args, opts = {}) {
  const r = spawnSync(cmd, args, { stdio: "inherit", cwd: projDir, ...opts });
  if (r.status !== 0) process.exit(r.status ?? 1);
}

console.log(`[build-sea] platform=${rid}`);

// 1. bundle with esbuild
run(process.execPath, [
  npxCli, "--yes", "esbuild",
  join(projDir, "zen-proxy-entry.js"),
  "--bundle", "--platform=node", "--format=cjs",
  `--outfile=${join(projDir, "zen-proxy-bundle.cjs")}`,
  "--external:node:*",
]);

// 2. write sea-config.json
writeFileSync(
  join(projDir, "sea-config.json"),
  JSON.stringify({ main: "zen-proxy-bundle.cjs", output: "sea-prep.blob", disableExperimentalSEAWarning: true }, null, 2),
);

// 3. generate the SEA blob
run(process.execPath, ["--experimental-sea-config", join(projDir, "sea-config.json")]);

// 4. copy the current node binary
mkdirSync(distDir, { recursive: true });
copyFileSync(process.execPath, outPath);

// 5. inject the SEA blob
run(process.execPath, [npxCli, "--yes", "postject", outPath, "NODE_SEA_BLOB", join(projDir, "sea-prep.blob"), "--sentinel-fuse", FUSE]);

// 6. Windows only: switch PE subsystem to GUI (no console window)
if (process.platform === "win32") {
  const buf = readFileSync(outPath);
  const peOffset = buf.readInt32LE(0x3c);
  const subsystemOffset = peOffset + 24 + 0x44;
  const old = buf.readUInt16LE(subsystemOffset);
  buf.writeUInt16LE(2, subsystemOffset);
  writeFileSync(outPath, buf);
  console.log(`[build-sea] PE subsystem ${old} -> 2 (GUI)`);
} else {
  chmodSync(outPath, 0o755);
}

console.log(`[build-sea] done: ${outPath}`);
