import { readFile, stat } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { globby } from "globby";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const publicDir = resolve(root, "public");
const files = await globby(["index.html", "src/**/*.{ts,tsx,html}"], { cwd: root, absolute: true });

const attrPattern = /(?:src|href)\s*=\s*["'](\/[^"'?#]+)/g;
const misses = [];

for (const file of files) {
  const text = await readFile(file, "utf8");
  for (const match of text.matchAll(attrPattern)) {
    const url = match[1];
    if (url.startsWith("/src/")) continue;
    const asset = resolve(publicDir, url.slice(1));
    try {
      await stat(asset);
    } catch {
      misses.push({ file: file.slice(root.length + 1), url });
    }
  }
}

if (misses.length > 0) {
  console.error("Missing public asset references:");
  for (const m of misses) console.error(`  ${m.file}  ->  ${m.url}`);
  process.exit(1);
}
