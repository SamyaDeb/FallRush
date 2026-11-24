// P7-UX: copy / share a room invite link from the play hub (clipboard API with an execCommand fallback; Web Share on
// phones). Called from a click handler, so the browser treats it as a user gesture.
mergeInto(LibraryManager.library, {
  FallRushCopy: function (textPtr) {
    var t = UTF8ToString(textPtr);
    try {
      if (navigator.clipboard && window.isSecureContext) { navigator.clipboard.writeText(t); return 1; }
    } catch (e) {}
    try {
      var a = document.createElement("textarea");
      a.value = t; a.style.position = "fixed"; a.style.opacity = "0";
      document.body.appendChild(a); a.select();
      var ok = document.execCommand("copy");
      document.body.removeChild(a);
      return ok ? 1 : 0;
    } catch (e) { return 0; }
  },
  FallRushShare: function (titlePtr, textPtr, urlPtr) {
    try {
      if (navigator.share) {
        navigator.share({ title: UTF8ToString(titlePtr), text: UTF8ToString(textPtr), url: UTF8ToString(urlPtr) }).catch(function () {});
        return 1;
      }
    } catch (e) {}
    return 0;
  },
  FallRushOrigin: function () {
    var v = window.location.origin || "";
    var n = lengthBytesUTF8(v) + 1;
    var buf = _malloc(n);
    stringToUTF8(v, buf, n);
    return buf;
  },
});
