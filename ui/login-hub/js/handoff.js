// Hand-off interstitial (5.8b): fetch the hand-off nonce, ask the agent (3 s timeout), then post. Config from data-* on form#handoff.
(async function () {
  var f = document.getElementById('handoff'); if (!f) return;
  try {
    var fd = new FormData(); fd.append('__RequestVerificationToken', f.__RequestVerificationToken.value);
    var n = await (await fetch(f.dataset.nonceUrl, { method: 'POST', body: fd, credentials: 'same-origin' })).json();
    var ctl = new AbortController(); setTimeout(function () { ctl.abort(); }, 3000);
    var r = await fetch(f.dataset.agentUrl, { method: 'POST', signal: ctl.signal,
      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ nonce: n.nonce }) });
    if (r.ok) f.device_attestation.value = (await r.json()).device_attestation;
  } catch (e) { }
  f.submit();
})();
