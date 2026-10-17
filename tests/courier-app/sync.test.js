const assert = require('assert');
const S = require(require('path').join(__dirname, '../../src/CourierEscrow.Api/wwwroot/courier/sync.js'));

function memStore(items = []) {
  let n = 0; const rows = items.map(i => ({ ...i, id: ++n }));
  return { rows, list: async () => rows.map(r => r), put: async i => { const k = rows.findIndex(r => r.id === i.id); rows[k] = i; },
           remove: async id => { const k = rows.findIndex(r => r.id === id); if (k >= 0) rows.splice(k, 1); }, add: async i => { i.id = ++n; rows.push(i); } };
}
const item = (type, extra = {}) => ({ orderId: 'o1', eventType: type, clientEventId: 'ev-' + type, fields: {}, photos: [], ...extra });
function api(script = {}) {
  const calls = { events: [], presigns: 0, uploads: 0 };
  return { calls,
    presign: async () => { calls.presigns++; if (script.presign) return script.presign(); return { status: 200, body: { uploadId: 'up' + calls.presigns, url: 'u' + calls.presigns } }; },
    upload: async () => { calls.uploads++; if (script.upload) return script.upload(); return { status: 204 }; },
    postEvent: async (o, b) => { calls.events.push(b); const r = script.event ? script.event(b, calls.events.length) : { status: 200, body: {} }; if (r.network) throw { network: true }; return r; } };
}

(async () => {
  const run = async (store, a) => { const results = []; const r = await S.drain({ store, api: a, onResult: x => results.push(x) }); return { ...r, results }; };

  // sends oldest first and removes what was accepted
  { const st = memStore([item('pickup'), item('arrived')]); const a = api(); const r = await run(st, a);
    assert.deepStrictEqual(a.calls.events.map(e => e.type), ['pickup', 'arrived']); assert.strictEqual(st.rows.length, 0); assert.strictEqual(r.sent, 2); assert.strictEqual(r.stopped, null); }

  // photos are uploaded first and their ids go into the event
  { const st = memStore([item('pickup', { photos: [{ blob: 1 }, { blob: 2 }, { blob: 3 }] })]); const a = api(); await run(st, a);
    assert.strictEqual(a.calls.uploads, 3); assert.deepStrictEqual(a.calls.events[0].uploadIds, ['up1', 'up2', 'up3']); }

  // no signal: nothing is lost, order is kept, and the retry reuses the same clientEventId and the uploaded photos
  { const st = memStore([item('pickup', { photos: [{ blob: 1 }] }), item('arrived')]);
    const down = api({ event: () => ({ network: true }) }); const r1 = await run(st, down);
    assert.strictEqual(r1.stopped, 'offline'); assert.strictEqual(st.rows.length, 2); assert.strictEqual(down.calls.events.length, 1);
    assert.strictEqual(st.rows[0].photos[0].uploadId, 'up1');
    const up = api(); const r2 = await run(st, up);
    assert.strictEqual(up.calls.uploads, 0); assert.strictEqual(up.calls.presigns, 0);
    assert.strictEqual(up.calls.events[0].clientEventId, 'ev-pickup'); assert.strictEqual(r2.sent, 2); assert.strictEqual(st.rows.length, 0); }

  // an expired session pauses the queue and keeps everything
  { const st = memStore([item('pickup'), item('arrived')]); const r = await run(st, api({ event: () => ({ status: 401 }) }));
    assert.strictEqual(r.stopped, 'auth'); assert.strictEqual(st.rows.length, 2); }

  // server trouble pauses too
  { const st = memStore([item('pickup')]); for (const code of [500, 503, 429]) { const r = await run(st, api({ event: () => ({ status: code }) })); assert.strictEqual(r.stopped, 'server'); assert.strictEqual(st.rows.length, 1); } }

  // a wrong code is reported with the attempts left and is not retried
  { const st = memStore([item('otp', { fields: { otp: '000000' } })]); const r = await run(st, api({ event: () => ({ status: 422, body: { result: 'Wrong', attemptsLeft: 4 } }) }));
    assert.strictEqual(st.rows.length, 0); assert.strictEqual(r.results[0].ok, false); assert.match(r.results[0].message, /Wrong code\. 4 attempt/); }
  { const st = memStore([item('otp')]); const r = await run(st, api({ event: () => ({ status: 423, body: {} }) })); assert.match(r.results[0].message, /sent to ops/); }
  { const st = memStore([item('otp')]); const r = await run(st, api({ event: () => ({ status: 410, body: {} }) })); assert.match(r.results[0].message, /expired/); }

  // a business refusal is shown with the server's own words and dropped
  { const st = memStore([item('pickup')]); const r = await run(st, api({ event: () => ({ status: 422, body: { detail: 'At least 3 photos of the item are required.' } }) }));
    assert.strictEqual(st.rows.length, 0); assert.strictEqual(r.results[0].message, 'At least 3 photos of the item are required.'); }

  // a refused photo upload fails the item cleanly; later items still go
  { const st = memStore([item('pickup', { photos: [{ blob: 1 }] }), item('arrived')]); let n = 0;
    const r = await run(st, api({ upload: () => (n++ === 0 ? { status: 413 } : { status: 204 }) }));
    assert.strictEqual(r.results[0].ok, false); assert.strictEqual(r.results[1].ok, true); assert.strictEqual(st.rows.length, 0); }

  // idempotent ids
  { const a = S.newId(), b = S.newId(); assert.notStrictEqual(a, b); assert.ok(a.length >= 8); }

  console.log('sync.js: all checks passed');
})().catch(e => { console.error(e); process.exit(1); });
