#!/usr/bin/env python3
"""P2 0.2: sBPF self-time profile from a LiteSVM register trace.
usage: sbf-prof.py <trace.regs> <unstripped.so> [top N]
Each trace record is 12 u64 registers (r0..r10, pc in r11, instruction index into .text). Instructions ≈ CU."""
import sys, subprocess, bisect, numpy as np
regs, so = sys.argv[1], sys.argv[2]
top = int(sys.argv[3]) if len(sys.argv) > 3 else 40
NM = "/Users/samya/.cache/solana/v1.52/platform-tools/rust/lib/rustlib/aarch64-apple-darwin/bin/llvm-nm"
OBJ = NM.replace("llvm-nm", "llvm-objdump")
hdr = subprocess.run([OBJ, "-h", so], capture_output=True, text=True).stdout
text_addr = next(int(l.split()[3], 16) for l in hdr.splitlines() if " .text " in l)
syms = []
for l in subprocess.run([NM, "--defined-only", "-n", "-S", "-C", so], capture_output=True, text=True).stdout.splitlines():
    p = l.split(None, 3)
    if len(p) == 4 and p[2].lower() == "t":
        syms.append((int(p[0], 16), int(p[1], 16), p[3]))
syms.sort()
starts = [s[0] for s in syms]
a = np.fromfile(regs, dtype=np.uint64).reshape(-1, 12)
pcs = a[:, 11]
u, c = np.unique(pcs, return_counts=True)
tot = int(c.sum())
agg = {}
for pc, n in zip(u.tolist(), c.tolist()):
    addr = text_addr + pc * 8
    i = bisect.bisect_right(starts, addr) - 1
    name = syms[i][2] if i >= 0 and addr < syms[i][0] + max(syms[i][1], 8) else f"?{addr:x}"
    name = name.rsplit("::h", 1)[0]
    agg[name] = agg.get(name, 0) + n
print(f"instructions {tot}")
for name, n in sorted(agg.items(), key=lambda x: -x[1])[:top]:
    print(f"{n:10d} {100*n/tot:5.1f}%  {name}")
