import test from "node:test";
import assert from "node:assert/strict";
import { chmodSync, copyFileSync, mkdtempSync, symlinkSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { spawnSync } from "node:child_process";

test("claude-zen prints setup guidance when .env.zen is missing", () => {
  const tempDir = mkdtempSync(join(tmpdir(), "claude-zen-missing-env-"));
  const scriptPath = join(tempDir, "claude-zen.sh");
  copyFileSync(new URL("../claude-zen.sh", import.meta.url), scriptPath);
  chmodSync(scriptPath, 0o755);

  const result = spawnSync("zsh", [scriptPath, "--version"], {
    encoding: "utf8",
  });

  assert.equal(result.status, 1);
  assert.match(result.stderr, /claude-zen: missing environment file/);
  assert.match(result.stderr, /cp \.env\.zen\.example \.env\.zen/);
});

test("claude-zen resolves its real script directory when invoked via symlink", () => {
  const targetDir = mkdtempSync(join(tmpdir(), "claude-zen-target-"));
  const linkDir = mkdtempSync(join(tmpdir(), "claude-zen-link-"));
  const targetScriptPath = join(targetDir, "claude-zen.sh");
  const linkedScriptPath = join(linkDir, "claude-zen");

  copyFileSync(new URL("../claude-zen.sh", import.meta.url), targetScriptPath);
  chmodSync(targetScriptPath, 0o755);
  symlinkSync(targetScriptPath, linkedScriptPath);

  const result = spawnSync("zsh", [linkedScriptPath, "--version"], {
    encoding: "utf8",
  });

  assert.equal(result.status, 1);
  assert.match(result.stderr, new RegExp(`missing environment file: ${targetDir}/\\.env\\.zen`));
  assert.doesNotMatch(
    result.stderr,
    new RegExp(`missing environment file: ${linkDir}/\\.env\\.zen`),
  );
});
