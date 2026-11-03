// Location sign-in / re-sign-in (contract 6.1, 6.5): at submit fetch a fresh nonce, ask the agent (3 s timeout), then post.
// External same-origin file so the page CSP needs no 'unsafe-inline' (TL 9:38 PM). Config comes from data-* on form#f.
(function () {
  var f = document.getElementById('f'); if (!f) return;
  f.addEventListener('submit', async function (ev) {
    if (f.dataset.ready) return; ev.preventDefault();
    try {
      var fd = new FormData(); fd.append('__RequestVerificationToken', f.__RequestVerificationToken.value);
      var n = await (await fetch(f.dataset.nonceUrl, { method: 'POST', body: fd, credentials: 'same-origin' })).json();
      var ctl = new AbortController(); setTimeout(function () { ctl.abort(); }, 3000);
      var r = await fetch(f.dataset.agentUrl, { method: 'POST', signal: ctl.signal,
        headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ nonce: n.nonce }) });
      if (r.ok) f.device_attestation.value = (await r.json()).device_attestation;
    } catch (e) { }
    f.dataset.ready = '1'; f.submit();
  });
})();
