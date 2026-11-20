// Prints the sha256 of the canonical (sorted-key, no-whitespace) JSON of each config file.
// Usage: pnpm --filter @fall-rush/shared hash
import { canonicalize, loadConfigJson, sha256, RULESET_V1_FILE, MAP_POOL_V1_FILE, MOVEMENT_V1_FILE, MOVEMENT_V2_FILE, MODIFIERS_V1_FILE } from "./canonical";

for (const file of [RULESET_V1_FILE, MAP_POOL_V1_FILE, MOVEMENT_V1_FILE, MOVEMENT_V2_FILE, MODIFIERS_V1_FILE]) {
  const canon = canonicalize(loadConfigJson(file));
  console.log(`${file}  sha256=${sha256(canon).toString("hex")}  bytes=${Buffer.byteLength(canon)}`);
}
