// P3-B: the C# side's bridge to StreamingAssets/fallrush-chain.js (packages/chain, globalThis.FallRushChain).
// Loaded lazily by on-chain play only (a <script> tag), so Classic play never fetches it. All calls are synchronous
// polls; async bundle calls (init, join) park their result for the next poll.
mergeInto(LibraryManager.library, {
  $FRC: { state: 0, err: null, join: null, joining: false },

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
      console.log('[frc] chain bundle ready: ' + j.name + ' via ' + j.transport);
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
    FRC.join = null; FRC.joining = true;
    window.FallRushChain.join(a).then(function (r) { FRC.join = r; FRC.joining = false; })
      .catch(function (e) { FRC.join = JSON.stringify({ ok: false, err: String(e) }); FRC.joining = false; });
  },

  fr_join_result__deps: ['$FRC'],
  fr_join_result: function () {
    if (!FRC.join) return 0;
    var v = FRC.join; FRC.join = null;
    var n = lengthBytesUTF8(v) + 1, b = _malloc(n); stringToUTF8(v, b, n); return b;
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

  fr_stop: function () { if (window.FallRushChain) window.FallRushChain.stop(); },
});
