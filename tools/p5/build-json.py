# Writes <dist>/unity/Build/build.json (the WebGL file names; parallel = Decompression Fallback .unityweb files).
import json, os, sys
d = os.path.join(sys.argv[1] if len(sys.argv) > 1 else 'web/app/dist', 'unity/Build'); fs = os.listdir(d)
pick = lambda suf: next((f for f in fs if f.endswith(suf)), None)
j = {'loader': pick('.loader.js'), 'data': pick('.data.unityweb') or pick('.data.br'), 'framework': pick('.framework.js.unityweb') or pick('.framework.js.br'), 'code': pick('.wasm.unityweb') or pick('.wasm.br')}
j['parallel'] = bool(j['data'] and j['data'].endswith('.unityweb'))
json.dump(j, open(os.path.join(d, 'build.json'), 'w')); print('build.json', j)
