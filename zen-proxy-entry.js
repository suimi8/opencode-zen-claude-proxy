import { readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { startServer } from "./src/server.js";

const exeDir = dirname(process.execPath);
let baseDir = exeDir;
if (!existsSync(join(exeDir, ".env.local"))) {
  try {
    baseDir = dirname(fileURLToPath(import.meta.url));
  } catch {
    baseDir = process.cwd();
  }
}

const envFile = join(baseDir, ".env.local");
if (existsSync(envFile)) {
  for (const line of readFileSync(envFile, "utf8").split(/\r?\n/)) {
    const m = line.match(/^([^#=][^=]*)=(.*)$/);
    if (m && m[1].trim() && !(m[1].trim() in process.env)) {
      process.env[m[1].trim()] = m[2];
    }
  }
}

startServer();
