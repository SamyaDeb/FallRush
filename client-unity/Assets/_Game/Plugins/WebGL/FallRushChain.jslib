// P3-B: the C# side's bridge to StreamingAssets/fallrush-chain.js (packages/chain, globalThis.FallRushChain).
// Loaded lazily by on-chain play only (a <script> tag), so Classic play never fetches it. All calls are synchronous
// polls; async bundle calls (init, join) park their result for the next poll.
mergeInto(LibraryManager.library, {
  $FRC: { state: 0, err: null, join: null, joining: false, econ: {}, gen: 0 },

  // P4 economy: one async bundle call per id; the reply is parked until C# takes it
  fr_econ__deps: ['$FRC'],
  fr_econ: function (id, aPtr, argsPtr) {
    var a = UTF8ToString(aPtr), args = UTF8ToString(argsPtr);
    window.FallRushChain.econ(a, args).then(function (r) { FRC.econ[id] = r; })
      .catch(function (e) { FRC.econ[id] = JSON.stringify({ ok: false, error: String(e) }); });
  },

  fr_econ_result__deps: ['$FRC'],
  fr_econ_result: function (id) {
    var v = FRC.econ[id];
    if (v === undefined) return 0;
    delete FRC.econ[id];
    var n = lengthBytesUTF8(v) + 1, b = _malloc(n); stringToUTF8(v, b, n); return b;
  },

  fr_boot__deps: ['$FRC'],
  fr_boot: function (bundlePtr, configPtr, urlPtr) {
    if (FRC.state !== 0) return;
    FRC.state = 2; // loading
    var bundle = UTF8ToString(bundlePtr), cfgUrl = UTF8ToString(configPtr), url = UTF8ToString(urlPtr);
    var load = window.FallRushChain ? Promise.resolve() : new Promise(function (ok, bad) {
      var s = document.createElement('script');
      s.src = bundle; s.onload = ok; s.onerror = function () { bad(new Error('cannot load ' + bundle)); };
      document.head.appendChild(s);
    });
    load.then(function () { return fetch(cfgUrl); }).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status + ' for ' + cfgUrl);
      return r.json();
    }).then(function (cfg) {
      if (url && cfg.emulator) cfg.emulator.url = url;
      return window.FallRushChain.init(JSON.stringify(cfg));
    }).then(function (res) {
      var j = JSON.parse(res);
      if (!j.ok) throw new Error(j.err || res);
      FRC.state = 1;
      console.log('[frc] chain bundle ready: ' + j.name + ' via ' + j.transport + ', burner ' + j.burner + ' (public key)');
    }).catch(function (e) { FRC.state = -1; FRC.err = String(e); console.error('[frc] ' + e); });
  },

  fr_state__deps: ['$FRC'],
  fr_state: function () { return FRC.state; },

  fr_now: function () { return performance.now(); },

  fr_error__deps: ['$FRC'],
  fr_error: function () {
    var v = FRC.err || '';
    var n = lengthBytesUTF8(v) + 1, b = _malloc(n); stringToUTF8(v, b, n); return b;
  },

  fr_join__deps: ['$FRC'],
  fr_join: function (argsPtr) {
    var a = UTF8ToString(argsPtr);
    // harden #13: each join (and each fr_stop) starts a new generation; a result from an older one (a join still pending
    // when the session stopped, or the first join when C# re-joins as a spectator) is dropped, not handed to the next
    var g = ++FRC.gen;
    FRC.join = null; FRC.joining = true;
    window.FallRushChain.join(a).then(function (r) { if (g !== FRC.gen) return; FRC.join = r; FRC.joining = false; })
      .catch(function (e) { if (g !== FRC.gen) return; FRC.join = JSON.stringify({ ok: false, err: String(e) }); FRC.joining = false; });
  },

  fr_join_result__deps: ['$FRC'],
  fr_join_result: function () {
    if (!FRC.join) return 0;
    var v = FRC.join; FRC.join = null;
    var n = lengthBytesUTF8(v) + 1, b = _malloc(n); stringToUTF8(v, b, n); return b;
  },

  fr_subscribe: function (addrPtr) {
    var r = window.FallRushChain.subscribe(UTF8ToString(addrPtr) || '$world');
    if (r && r.indexOf('"ok":false') >= 0) console.warn('[frc] subscribe: ' + r);
  },

  fr_send_inputs: function (ptr, len) {
    return window.FallRushChain.sendInputs(HEAPU8.slice(ptr, ptr + len)) >>> 0;
  },

  fr_drain_worlds: function (ptr, cap, maxItems) {
    return window.FallRushChain.drainWorlds(HEAPU8.subarray(ptr, ptr + cap), maxItems);
  },

  fr_latest_world_b64: function () {
    var v = window.FallRushChain.latestWorldBase64() || '';
    var n = lengthBytesUTF8(v) + 1, b = _malloc(n); stringToUTF8(v, b, n); return b;
  },

  fr_drain_events: function () {
    var v = window.FallRushChain.drainEvents() || '[]';
    var n = lengthBytesUTF8(v) + 1, b = _malloc(n); stringToUTF8(v, b, n); return b;
  },

  fr_stats: function () {
    var v = window.FallRushChain.stats() || '{}';
    var n = lengthBytesUTF8(v) + 1, b = _malloc(n); stringToUTF8(v, b, n); return b;
  },

  // P3-E (OC-6): the menu's Connect Wallet = Phantom connect only (no SDK).
  fr_phantom_connect__deps: ['$FRC'],
  fr_phantom_connect: function () {
    FRC.phantom = null;
    var p = window.solana;
    if (!p || !p.isPhantom) { FRC.phantom = 'err: no Phantom wallet in this browser'; return; }
    p.connect().then(function (r) { FRC.phantom = r.publicKey.toString(); })
      .catch(function (e) { FRC.phantom = 'err: ' + (e && e.message ? e.message : e); });
  },

  fr_phantom_result__deps: ['$FRC'],
  fr_phantom_result: function () {
    if (!FRC.phantom) return 0;
    var v = FRC.phantom; FRC.phantom = null;
    var n = lengthBytesUTF8(v) + 1, b = _malloc(n); stringToUTF8(v, b, n); return b;
  },

  // fullchain (production canary): stop() drops the bundle's transport, so the next session must boot it again (state 0);
  // before, fr_boot returned early ("already booted") and the next match's join ran against a stopped transport
  fr_stop__deps: ['$FRC'],
  fr_stop: function () { if (window.FallRushChain) window.FallRushChain.stop(); FRC.state = 0; FRC.join = null; FRC.joining = false; FRC.gen++; },
});
