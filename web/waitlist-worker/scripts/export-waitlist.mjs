// Export the live waitlist to CSV:  npm run export:emails  (from web/site)  -> backups/waitlist-YYYY-MM-DD.csv
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync, chmodSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const wrangler = resolve(here, '../../site/node_modules/.bin/wrangler');
const out = execFileSync(wrangler, ['d1', 'execute', 'fallrush-waitlist', '--remote', '--json', '--command',
  'SELECT seq AS joined_order, email, rushling, code, referred_by, referrals, created_at FROM waitlist ORDER BY seq'],
  { cwd: resolve(here, '..'), encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });
const rows = JSON.parse(out)[0].results;
const cols = ['joined_order', 'email', 'rushling', 'code', 'referred_by', 'referrals', 'created_at'];
const esc = (v) => (v == null ? '' : /[",\n]/.test(String(v)) ? `"${String(v).replace(/"/g, '""')}"` : String(v));
const csv = [cols.join(','), ...rows.map((r) => cols.map((c) => esc(r[c])).join(','))].join('\n') + '\n';
const dir = resolve(here, '../../../backups');
mkdirSync(dir, { recursive: true, mode: 0o700 });
const file = resolve(dir, `waitlist-${new Date().toISOString().slice(0, 10)}.csv`);
writeFileSync(file, csv);
chmodSync(file, 0o600);
console.log(`Saved ${rows.length} signups to ${file}`);
