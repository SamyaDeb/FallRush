# Public-repo safety scan (P7 C4, D-SECRET) — report only, nothing published

Scanned 2026-11-23 ~20:05 UTC: **all refs of the main checkout** (`git rev-list --all`: 238 commits, 7,151 objects; pack 378 MB), via `git log --all -G<pattern>` (every commit that adds or removes a match, text files) and `git rev-list --objects --all` (file names). No secret is quoted here: path + commit + type only.

| Check | Method | Result |
|---|---|---|
| Key / secret **file names** (`.env`, `id.json`, `*keypair*`, `.keys/`, `*.local.md`, `*.pem`, `*_ed25519`, `front.env`, `vps.env`, wallet JSON) | grep over `rev-list --objects --all` | **none** (only `infra/prod/env.example`, a template without values) |
| Solana **keypair JSON arrays** (64 numbers) | `-G '\[(n, ){63}n\]'` | **none** |
| **base58 64-byte strings** (a secret key has the same length as a signature) | every 86–89-char base58 token in history (44,811 tokens → 10,053 decode to 64 bytes), each tested with `Keypair.fromSecretKey` (a real secret key's last 32 bytes = the public key of its first 32) | **0 valid secret keys**: all 10,053 are transaction signatures (proof files, keeper logs) |
| PEM private keys (RSA / OpenSSH / EC) | `-G 'BEGIN … PRIVATE KEY'` | **none** |
| `FRONT_KEY=`, `SECRET=`, `API_KEY=`, `TOKEN=`… with a value | `-G` | **none** |
| The **real VPS address** (dotted and `sslip.io` dashed forms, from `vps.env`) | `-G` on both forms | **none** in any text file in history (proof logs use `<VPS_IP>` / `<VPS_IP-dashed>`) |
| The real address **inside images** (screenshots under `docs/proof/`) | not machine-checkable here (no OCR) | **UNVERIFIED**. The P7 screenshots are page captures without an address bar, and the P7 build hides server URLs outside `?dev=1`; older proof screenshots of the lobby / menu (M1–P4) may show a `wss://` server URL — those were taken against `127.0.0.1` / local servers in those phases (UNVERIFIED for each file) |
| E-mail addresses in **file contents** | regex over `git log -p --all` | only `you@example.com` (placeholder), `noreply@anthropic.com` (commit trailer), a dependency author's address in a vendored licence header |
| E-mail addresses in **commit metadata** | `git log --all --format='%ae %ce'` | **all 476 author/committer fields use the owner's personal Gmail address.** Not a secret, but it becomes public with the repo (GitHub shows it on every commit) |
| `.gitignore` | `git check-ignore` | covers `client-unity/Library/`, `client-unity/Builds/`, `client-unity/Temp/`, `tools/keeper/.keys/`, `*.local.md`, `.env`, `web/app/dist/`, `programs/target/` ✓ |
| Repo size / big blobs | `count-objects -vH`, biggest blobs | 378 MB pack; the biggest blobs are old versions of the map **scenes** (`Map_BlockPartyDash.unity` 28 MB, `Map_GooTower.unity` 26 MB, …). GitHub accepts files < 100 MB; no LFS needed. A fresh clone downloads ~380 MB |

## Verdict
**History is clean of secrets and of the server address.** Two things for the owner to decide (`docs/prompts/P7-USER-STEPS.md` §5):
1. **The personal e-mail in commit metadata.** Options: (a) publish as is; (b) publish a **clean-history `public` branch** (one orphan commit of the RC tree, authored with a GitHub `noreply` address): it also drops the 378 MB history to the tree's size, but loses the commit history judges might read; (c) rewrite the author e-mail with `git filter-repo --mailmap` on a **copy** of the repo (never on `main` here). The agent recommends **(a) or (b)**; (b) is prepared as a command in the user steps, not executed.
2. Nothing needs rotating (no key, token or address was ever committed).
