// The offline queue. Pure logic with injected storage and network so it can be tested without a browser.
//
// Each queued action carries a clientEventId. The server remembers every id it has applied, so sending the same
// action twice (the phone lost signal after the request left) is harmless. Photos are uploaded once and their
// upload ids are saved on the item, so a retry never uploads them again.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.NexaSync = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  function newId() {
    if (typeof crypto !== 'undefined' && crypto.randomUUID) return crypto.randomUUID();
    return 'ev-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 10);
  }

  function bodyFor(item) {
    return Object.assign({}, item.fields, {
      type: item.eventType,
      clientEventId: item.clientEventId,
      uploadIds: item.photos.map(p => p.uploadId).filter(Boolean)
    });
  }

  function describe(item, res) {
    const b = res.body || {};
    if (item.eventType === 'otp') {
      if (res.status === 422) return `Wrong code. ${b.attemptsLeft ?? 0} attempt(s) left.`;
      if (res.status === 423) return 'Too many wrong codes. The order was sent to ops.';
      if (res.status === 410) return 'The buyer code expired. Ask ops to send a new one.';
    }
    return b.detail || b.title || `The server refused this action (${res.status}).`;
  }

  /**
   * Sends queued actions oldest first. Stops at the first problem that a retry could fix, so order is kept.
   * Returns { sent, stopped } where stopped is null, 'offline', 'auth' or 'server'.
   */
  async function drain({ store, api, onResult }) {
    let sent = 0;
    const items = (await store.list()).sort((a, b) => a.id - b.id);

    for (const item of items) {
      try {
        for (const p of item.photos) {
          if (p.uploadId) continue;
          const slot = await api.presign(p.type || 'image/jpeg');
          if (slot.status === 401) return { sent, stopped: 'auth' };
          if (slot.status >= 500 || slot.status === 429) return { sent, stopped: 'server' };
          if (slot.status >= 400) throw refusal(slot);
          const up = await api.upload(slot.body.url, p.blob);
          if (up.status >= 500 || up.status === 429) return { sent, stopped: 'server' };
          if (up.status >= 400) throw refusal(up);
          p.uploadId = slot.body.uploadId;
          await store.put(item);                         // remember it, so a retry skips the upload
        }

        const res = await api.postEvent(item.orderId, bodyFor(item));
        if (res.status === 401) return { sent, stopped: 'auth' };
        if (res.status >= 500 || res.status === 429) return { sent, stopped: 'server' };

        await store.remove(item.id);
        if (res.status >= 200 && res.status < 300) { sent++; onResult({ ok: true, item, body: res.body }); }
        else onResult({ ok: false, item, message: describe(item, res), status: res.status, body: res.body });
      } catch (e) {
        if (e && e.network) return { sent, stopped: 'offline' };
        if (e && e.refused) { await store.remove(item.id); onResult({ ok: false, item, message: describe(item, e.refused), status: e.refused.status }); continue; }
        throw e;
      }
    }
    return { sent, stopped: null };
  }

  function refusal(res) { return { refused: res }; }

  return { drain, newId, bodyFor, describe };
});
