# Writes <dist>/unity/Build/build.json (the WebGL file names; parallel = Decompression Fallback .unityweb files).
import json, os, sys
d = os.path.join(sys.argv[1] if len(sys.argv) > 1 else 'web/app/dist', 'unity/Build'); fs = os.listdir(d)
pick = lambda suf: next((f for f in fs if f.endswith(suf)), None)
j = {'loader': pick('.loader.js'), 'data': pick('.data.unityweb') or pick('.data.br'), 'framework': pick('.framework.js.unityweb') or pick('.framework.js.br'), 'code': pick('.wasm.unityweb') or pick('.wasm.br')}
j['parallel'] = bool(j['data'] and j['data'].endswith('.unityweb'))
# beta audit: a build version (names + sizes + mtimes) that /play appends as ?v= to every build file, so a new deploy
# never meets a cached copy of the old one (Cloudflare kept WebGL.loader.js 4 h; browsers kept the rest 1 h)
import hashlib
h = hashlib.sha1()
for f in sorted(x for x in fs if x != 'build.json'):
    st = os.stat(os.path.join(d, f)); h.update(f'{f}:{st.st_size}:{int(st.st_mtime)}'.encode())
j['v'] = h.hexdigest()[:12]
json.dump(j, open(os.path.join(d, 'build.json'), 'w')); print('build.json', j)
