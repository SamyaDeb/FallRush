import json,sys
MM={'H9va5AgT1ceQzGCMpvFumAghnA76uLW1Mcy2475ZX9h'}
d=json.load(open(sys.argv[1]))['summary']; r=d['receipt']
print('match',d['match'],'champion',r['champion'],'humanMask',r['humanMask'],'sealed n/rev/forf',r.get('sealedN'),r.get('sealedRevealed'),r.get('sealedForfeits'),'seasons',{k:len(v.get('sigs',[])) for k,v in (d.get('seasons') or {}).items()}, d.get('seasonError',''))
ok=True
for k in range(5):
    m=d.get(f'market{k}')
    if not m or not m['bets']: continue
    pool=int(m['pool']); ws=int(m['winningStake'])
    paid_hand=0
    for b in m['bets']:
        o = int(b['amount']) if m['refund']==1 else (int(b['amount'])*pool//ws if (m['winners']>>b['outcome'])&1 and ws else 0)
        paid_hand+=o
    ok = ok and (paid_hand==int(m['paidTotal']))
    print(f' market{k} pool {pool/1e6} winners {m["winners"]} label {m["label"]} paid {int(m["paidTotal"])/1e6} hand {paid_hand/1e6} {"OK" if paid_hand==int(m["paidTotal"]) else "MISMATCH"}')
print('PAYOUTS = HAND CALC' if ok else 'MISMATCH')
