/* The host's first word, under a host that has none to say.

   A host with a window (the WebView2 form) hands the page its version and its language
   before any script in index.html runs, so under it this file is empty on purpose and loads
   as nothing. A host with no window (the Linux service) answers this same address with a
   boot.js of its own, ahead of the static files, naming window.BAKA_HOST, BAKA_VERSION and
   BAKA_LANG. This file is here so the include in index.html is never a missing file under
   the host that has no need of it, and the mock preview off a plain file server is not one
   request short. */
