// M4-6: the reconnect token lives in this tab's sessionStorage (one per tab, gone when the tab closes).
// Unity WebGL browser interop (Unity manual: "Interaction with browser scripting").
mergeInto(LibraryManager.library, {
  FallRushSessionGet: function (keyPtr) {
    try {
      var v = window.sessionStorage.getItem(UTF8ToString(keyPtr));
      if (v === null) return 0;
      var n = lengthBytesUTF8(v) + 1;
      var buf = _malloc(n);
      stringToUTF8(v, buf, n);
      return buf;
    } catch (e) { return 0; }
  },
  FallRushSessionSet: function (keyPtr, valuePtr) {
    try { window.sessionStorage.setItem(UTF8ToString(keyPtr), UTF8ToString(valuePtr)); } catch (e) {}
  },
});
