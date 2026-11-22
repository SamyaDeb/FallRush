// tools/p3/devnet-summary.mjs — one row per devnet Final run (docs/proof/p3/devnet/<run>/): client feel numbers
// while playing, the champion shown vs the on-chain Match, explorer links.   node tools/p3/devnet-summary.mjs [runs…]
import { readFileSync, readdirSync, existsSync } from 'node:fs';
const D = 'docs/proof/p3/devnet';
const runs = process.argv.slice(2).length ? process.argv.slice(2) : readdirSync(D).filter((d) => d.startsWith('p3-k-')).sort();
const num = (l, k) => { const m = l.match(new RegExp(k + ' (-?[\\d.]+)')); return m ? +m[1] : NaN; };
const rows = [];
for (const r of runs) {
  const cf = `${D}/${r}/client.txt`; if (!existsSync(cf)) continue;
  const lines = readFileSync(cf, 'utf8').split('\n').filter((l) => l.startsWith('[onchain] tick'));
  const play = lines.filter((l) => /playing True/.test(l) && /phase Round(Intro|Active)/.test(l) && num(l, 'upd/s') > 0);
  const corr = play.filter((l) => /RoundActive/.test(l)).map((l) => num(l, 'corr/5s'));
  const last = lines[lines.length - 1] || '';
  const res = (readFileSync(cf, 'utf8').match(/RESULT champion (\d+) order ([\d,]*) own slot (\d+)/) || []);
  let k = {}; try { k = JSON.parse(readFileSync(`${D}/${r}/keeper-summary.json`, 'utf8')); } catch {}
  const upd = play.map((l) => num(l, 'upd/s'));
  rows.push({
    run: r, ownSlot: res[3], playingWindows: corr.length,
    corrPer5s: corr.length ? { mean: +(corr.reduce((a, b) => a + b, 0) / corr.length).toFixed(2), max: Math.max(...corr) } : null,
    updPerS: upd.length ? +(upd.reduce((a, b) => a + b, 0) / upd.length).toFixed(1) : null,
    rtt: num(last, 'rtt'), confirmP50: num(last, 'confirm p50'), confirmP95: num(last, 'p95'), late: num(last, 'late'),
    rejected: num(last, 'rejected'), gaps: num(last, 'gaps'), lag: num(last, 'lag'), lead: (last.match(/lead \+(\d)/) || [])[1],
    championShown: res[1], order: res[2], championOnSolana: k.onSolana?.champion, solanaStatus: k.onSolana?.status, finalists: k.finalists,
    tickGapClientP95: k.realTime?.tickGapClientMs?.p95, gameWall: k.realTime?.ratio && +k.realTime.ratio.toFixed(3),
    match: k.matchPda, explorer: k.matchPda && `https://explorer.solana.com/address/${k.matchPda}?cluster=devnet`,
    finalizeTx: k.sigs?.finalize && `https://explorer.solana.com/tx/${k.sigs.finalize}?cluster=custom&customUrl=https%3A%2F%2Fdevnet-as.magicblock.app`,
  });
}
console.log(JSON.stringify(rows, null, 1));
