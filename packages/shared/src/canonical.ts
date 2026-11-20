import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";

/**
 * Canonical JSON (doc 03 §5): object keys sorted (UTF-16 code-unit order, i.e. JS default
 * sort), no whitespace, arrays kept in order. Only integers, strings, booleans, arrays and
 * objects are allowed, so every language produces the same bytes (no float formatting).
 */
export function canonicalize(value: unknown): string {
  if (value === null || typeof value === "boolean" || typeof value === "string") {
    return JSON.stringify(value);
  }
  if (typeof value === "number") {
    if (!Number.isSafeInteger(value)) throw new Error(`canonical JSON allows only safe integers, got ${value}`);
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) return `[${value.map(canonicalize).join(",")}]`;
  if (typeof value === "object") {
    const obj = value as Record<string, unknown>;
    const keys = Object.keys(obj).sort();
    return `{${keys.map((k) => `${JSON.stringify(k)}:${canonicalize(obj[k])}`).join(",")}}`;
  }
  throw new Error(`unsupported JSON value: ${typeof value}`);
}

export function sha256(data: Buffer | Uint8Array | string): Buffer {
  return createHash("sha256").update(data).digest();
}

export const CONFIG_DIR = join(__dirname, "..", "config");

export function loadConfigJson(file: string): unknown {
  return JSON.parse(readFileSync(join(CONFIG_DIR, file), "utf8"));
}

export function canonicalHash(file: string): Buffer {
  return sha256(canonicalize(loadConfigJson(file)));
}

export const RULESET_V1_FILE = "ruleset_v1.json";
export const MAP_POOL_V1_FILE = "map_pool_v1.json";
export const RULESET_V1_VERSION = 1;
export const MAP_POOL_V1_VERSION = 1;

export function rulesetV1Hash(): Buffer {
  return canonicalHash(RULESET_V1_FILE);
}
export function mapPoolV1Hash(): Buffer {
  return canonicalHash(MAP_POOL_V1_FILE);
}
