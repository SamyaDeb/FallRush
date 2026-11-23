# P5 7.2: payouts = hand calculation, per market, from the archived keeper summary (bets read from the Market account
# after settlement): owed(bet) = refund ? amount : (outcome ∈ winners ? ⌊amount · pool / winning_stake⌋ : 0);
# check Σ owed == the program's paid_total, every bet paid, and Σ owed ≤ pool.
import json, sys
s = json.load(open(sys.argv[1]))
s = s.get('summary', s)
ok = True; rows = []
for k in range(5):
    m = s.get(f'market{k}')
    if not m: continue
    pool, ws, win, ref = int(m['pool']), int(m['winningStake']), m['winners'], m.get('refund', 0)
    owed = 0
    for b in m['bets']:
        a = int(b['amount'])
        owed += a if ref else ((a * pool) // ws if (win >> b['outcome']) & 1 and ws else 0)
    good = owed == int(m['paidTotal']) and all(b['paid'] for b in m['bets']) and owed <= pool
    ok &= good
    rows.append(f"k{k} label {m['label']} pool {pool/1e6:g} bets {len(m['bets'])} hand {owed/1e6:g} program {int(m['paidTotal'])/1e6:g} {'OK' if good else 'MISMATCH'}")
print('\n'.join(rows)); print('PAYOUTS', 'OK' if ok and rows else 'FAIL')
