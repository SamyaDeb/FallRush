// P3-A: rush-core in the browser. StreamingAssets/rush_core.wasm (P2's rush-core-wasm, C ABI, no wasm-bindgen) is
// instantiated once at boot (async); C# (RushCoreWasmCore.cs) polls rcw_state() and then calls in synchronously.
// One scratch region in the core's linear memory holds World + inputs + player records, so each call copies once.
mergeInto(LibraryManager.library, {
  $RCW: { x: null, state: 0, err: null, base: 0, W: 0, I: 0, P: 0, MAXN: 64 },

  rcw_boot__deps: ['$RCW'],
  rcw_boot: function (urlPtr) {
    if (RCW.state !== 0 || RCW.x) return;
    var url = UTF8ToString(urlPtr);
    fetch(url).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status + ' for ' + url);
      return r.arrayBuffer();
    }).then(function (b) {
      RCW.bytes = b.byteLength;
      return WebAssembly.instantiate(b, {});
    }).then(function (res) {
      var x = res.instance.exports;
      // World 4096 | inputs 64 × 72 | players 12 × 36 (one tick at a time)
      var size = 4096 + RCW.MAXN * 72 + 12 * 36;
      RCW.base = x['rc_alloc'](size);
      RCW.W = RCW.base;
      RCW.I = RCW.base + 4096;
      RCW.P = RCW.I + RCW.MAXN * 72;
      RCW.x = x;
      RCW.state = 1;
      console.log('[rcw] rush_core.wasm ready, rc_version 0x' + (x['rc_version']() >>> 0).toString(16) + ', ' + RCW.bytes + ' B');
    }).catch(function (e) {
      RCW.state = -1;
      RCW.err = String(e);
      console.error('[rcw] rush_core.wasm failed: ' + e);
    });
  },

  rcw_state__deps: ['$RCW'],
  rcw_state: function () { return RCW.state; },

  rcw_version__deps: ['$RCW'],
  rcw_version: function () { return RCW.x['rc_version']() >>> 0; },

  rcw_init__deps: ['$RCW'],
  rcw_init: function (map, mapLen, cfg) {
    var x = RCW.x;
    var mp = x['rc_alloc'](mapLen);
    var cp = x['rc_alloc'](24);
    var m = new Uint8Array(x['memory'].buffer);
    m.set(HEAPU8.subarray(map, map + mapLen), mp);
    m.set(HEAPU8.subarray(cfg, cfg + 24), cp);
    var h = x['rc_init'](mp, mapLen, cp);
    x['rc_dealloc'](mp, mapLen);
    x['rc_dealloc'](cp, 24);
    return h;
  },

  rcw_free__deps: ['$RCW'],
  rcw_free: function (h) { RCW.x['rc_free'](h); },

  rcw_set_world__deps: ['$RCW'],
  rcw_set_world: function (h, world, len) {
    var x = RCW.x;
    if (len > 4096) return -1;
    new Uint8Array(x['memory'].buffer).set(HEAPU8.subarray(world, world + len), RCW.W);
    return x['rc_set_world'](h, RCW.W, len);
  },

  rcw_world_bytes__deps: ['$RCW'],
  rcw_world_bytes: function (h, dst, cap) {
    var x = RCW.x;
    var n = x['rc_world_bytes'](h, RCW.W, 4096) >>> 0;
    if (n > cap) n = cap;
    HEAPU8.set(new Uint8Array(x['memory'].buffer, RCW.W, n), dst);
    return n;
  },

  rcw_run__deps: ['$RCW'],
  rcw_run: function (h, world, worldLen, n, inputs, inputsLen, slotMask, outPlayers, outLen) {
    var x = RCW.x;
    if (worldLen > 0) {
      if (worldLen > 4096) return 0;
      new Uint8Array(x['memory'].buffer).set(HEAPU8.subarray(world, world + worldLen), RCW.W);
      if (x['rc_set_world'](h, RCW.W, worldLen) !== 0) return 0;
    }
    if (n > RCW.MAXN) n = RCW.MAXN;
    var mem = x['memory'].buffer;
    if (inputs && inputsLen >= n * 72) new Uint8Array(mem).set(HEAPU8.subarray(inputs, inputs + n * 72), RCW.I);
    for (var k = 0; k < n; k++) {
      x['rc_step'](h, (inputs && inputsLen >= (k + 1) * 72) ? RCW.I + k * 72 : 0);
      if (!outPlayers || !slotMask) continue;
      var pv = null; // smooth run: every active slot is written now; one view per tick (rc_player does not grow memory)
      for (var s = 0; s < 12; s++) {
        if (((slotMask >>> s) & 1) === 0) continue;
        var o = (k * 12 + s) * 36;
        if (o + 36 > outLen) continue;
        x['rc_player'](h, s, RCW.P);
        if (!pv || pv.buffer !== x['memory'].buffer) pv = new Uint8Array(x['memory'].buffer, RCW.P, 36);
        HEAPU8.set(pv, outPlayers + o);
      }
    }
    return n;
  },

  rcw_players__deps: ['$RCW'],
  rcw_players: function (h, out) {
    var x = RCW.x;
    for (var s = 0; s < 12; s++) {
      x['rc_player'](h, s, RCW.P);
      HEAPU8.set(new Uint8Array(x['memory'].buffer, RCW.P, 36), out + s * 36);
    }
  },

  rcw_hash__deps: ['$RCW'],
  rcw_hash: function (h, out8) {
    var v = BigInt.asUintN(64, BigInt(RCW.x['rc_hash'](h)));
    new DataView(HEAPU8.buffer, out8, 8).setBigUint64(0, v, true);
  },

  rcw_clock__deps: ['$RCW'],
  rcw_clock: function (h, out8) {
    var v = BigInt.asUintN(64, BigInt(RCW.x['rc_clock'](h)));
    new DataView(HEAPU8.buffer, out8, 8).setBigUint64(0, v, true);
  },

  rcw_vectors__deps: ['$RCW'],
  rcw_vectors: function (kind, seedLo, seedHi, n, map, mapLen, out8) {
    var x = RCW.x;
    var mp = 0;
    if (mapLen > 0) {
      mp = x['rc_alloc'](mapLen);
      new Uint8Array(x['memory'].buffer).set(HEAPU8.subarray(map, map + mapLen), mp);
    }
    var seed = (BigInt(seedHi >>> 0) << 32n) | BigInt(seedLo >>> 0);
    var v = BigInt.asUintN(64, BigInt(x['rc_vectors'](kind, seed, n, mp, mapLen)));
    if (mp) x['rc_dealloc'](mp, mapLen);
    new DataView(HEAPU8.buffer, out8, 8).setBigUint64(0, v, true);
  },
});
