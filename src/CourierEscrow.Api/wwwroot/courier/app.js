(() => {
  'use strict';
  const S = window.NexaSync;
  const API = location.origin;                       // served by the API itself
  const $ = id => document.getElementById(id);
  const view = $('view');
  const put = (...kids) => view.replaceChildren(...kids.flat(Infinity).filter(kid => kid != null && kid !== false));
  const K = { token: 'nx_token', name: 'nx_name', device: 'nx_device', jobs: 'nx_jobs', results: 'nx_results' };
  const get = k => { try { return JSON.parse(localStorage.getItem(k)); } catch { return null; } };
  const set = (k, v) => localStorage.setItem(k, JSON.stringify(v));

  if (!localStorage.getItem(K.device)) set(K.device, S.newId());
  if ('serviceWorker' in navigator) navigator.serviceWorker.register('sw.js').catch(() => {});

  // small DOM helper
  function h(tag, attrs, ...kids) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (k === 'class') el.className = v; else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
      else if (v === true) el.setAttribute(k, ''); else if (v !== false && v != null) el.setAttribute(k, v);
    }
    for (const kid of kids.flat()) if (kid != null && kid !== false) el.append(kid.nodeType ? kid : document.createTextNode(kid));
    return el;
  }
  const money = minor => 'K' + (minor / 100).toLocaleString('en', { maximumFractionDigits: 2 });

  // storage for the queue (IndexedDB, so photos survive a closed app)
  const db = new Promise((res, rej) => {
    const r = indexedDB.open('nexa-courier', 1);
    r.onupgradeneeded = () => r.result.createObjectStore('queue', { keyPath: 'id', autoIncrement: true });
    r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error);
  });
  const tx = async (mode, fn) => { const d = await db; return new Promise((res, rej) => { const t = d.transaction('queue', mode); const o = fn(t.objectStore('queue')); t.oncomplete = () => res(o.result); t.onerror = () => rej(t.error); }); };
  const store = {
    list: () => tx('readonly', s => s.getAll()),
    add: i => tx('readwrite', s => s.add(i)),
    put: i => tx('readwrite', s => s.put(i)),
    remove: id => tx('readwrite', s => s.delete(id))
  };

  // network 
  const token = () => localStorage.getItem(K.token);
  async function call(path, opts = {}) {
    let res;
    try { res = await fetch(API + path, { ...opts, headers: { ...(opts.headers || {}), ...(token() ? { Authorization: 'Bearer ' + token() } : {}) } }); }
    catch { throw { network: true }; }
    let body = null; try { body = await res.json(); } catch { /* empty */ }
    return { status: res.status, body };
  }
  const json = (method, path, data) => call(path, { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
  const api = {
    presign: type => json('POST', '/courier/uploads/presign', { contentType: type }),
    upload: async (url, blob) => { try { const r = await fetch(url, { method: 'PUT', body: blob, headers: { 'Content-Type': blob.type || 'image/jpeg' } }); return { status: r.status }; } catch { throw { network: true }; } },
    postEvent: (orderId, body) => json('POST', `/courier/orders/${orderId}/events`, body)
  };

  // sync
  let syncing = false, lastStop = null;
  async function sync() {
    if (syncing || !token()) return;
    syncing = true;
    try {
      const r = await S.drain({ store, api, onResult: x => { const log = get(K.results) || []; log.unshift({ at: Date.now(), ok: x.ok, order: x.item.orderNumber, type: x.item.eventType, message: x.message || 'Done' }); set(K.results, log.slice(0, 20)); } });
      lastStop = r.stopped;
      if (r.stopped === 'auth') { localStorage.removeItem(K.token); }
    } catch (e) { lastStop = 'error'; }
    syncing = false;
    await refreshJobs();
    chrome();
    if (current.name === 'jobs' || current.name === 'sync') go(current.name, current.arg);
  }

  async function refreshJobs() {
    if (!token()) return;
    try { const r = await call('/courier/jobs'); if (r.status === 200) set(K.jobs, r.body); else if (r.status === 401) localStorage.removeItem(K.token); } catch { /* offline: keep the cached list */ }
  }

  async function queueAction(job, eventType, fields, photos) {
    await store.add({ orderId: job.id, orderNumber: job.orderNumber, eventType, clientEventId: S.newId(), fields, photos: photos.map(f => ({ blob: f, type: f.type })), createdAt: Date.now() });
    await chrome();
    sync();
  }

  // device helpers
  function gps() {
    return new Promise((res, rej) => {
      if (!navigator.geolocation) return rej(new Error('This phone cannot give a GPS position.'));
      navigator.geolocation.getCurrentPosition(p => res({ latitude: p.coords.latitude, longitude: p.coords.longitude }),
        () => rej(new Error('Turn on location (GPS) and try again.')), { enableHighAccuracy: true, timeout: 12000, maximumAge: 30000 });
    });
  }
  // Shrinks photos before they are queued: saves the courier's data and speeds up uploads.
  async function shrink(file) {
    try {
      const bmp = await createImageBitmap(file); const scale = Math.min(1, 1600 / Math.max(bmp.width, bmp.height));
      const c = document.createElement('canvas'); c.width = Math.round(bmp.width * scale); c.height = Math.round(bmp.height * scale);
      c.getContext('2d').drawImage(bmp, 0, 0, c.width, c.height);
      return await new Promise(r => c.toBlob(b => r(b || file), 'image/jpeg', 0.8));
    } catch { return file; }
  }

  function photoPicker(min, label) {
    const files = []; const grid = h('div', { class: 'photos' });
    const input = h('input', { type: 'file', accept: 'image/*', capture: 'environment', hidden: true, onchange: async e => {
      for (const f of e.target.files) files.push(await shrink(f)); e.target.value = ''; draw(); } });
    function draw() {
      grid.replaceChildren(...files.map(f => h('div', { class: 'photo' }, h('img', { src: URL.createObjectURL(f), alt: 'photo' }))),
        h('div', { class: 'photo add', onclick: () => input.click() }, '+ Add photo'));
    }
    draw();
    return { el: h('div', {}, h('label', {}, `${label} (min ${min})`), grid, input), files, ok: () => files.length >= min, min };
  }

  // screens 
  const current = { name: 'login', arg: null };
  function go(name, arg) { current.name = name; current.arg = arg; ({ login, jobs, job, sync: syncView, profile, reject })[name](arg); window.scrollTo(0, 0); chrome(); }

  function login() {
    const phone = h('input', { type: 'tel', placeholder: '+265 99 123 4567', autocomplete: 'tel' });
    const pin = h('input', { type: 'password', inputmode: 'numeric', placeholder: '••••', autocomplete: 'current-password' });
    const msg = h('div');
    const btn = h('button', { class: 'btn', onclick: async () => {
      msg.replaceChildren(); btn.disabled = true;
      try {
        const r = await json('POST', '/courier/login', { phone: phone.value, pin: pin.value, deviceId: get(K.device) });
        if (r.status !== 200) { msg.append(h('div', { class: 'err' }, r.body?.detail || 'Sign-in failed.')); return; }
        localStorage.setItem(K.token, r.body.token); localStorage.setItem(K.name, r.body.name);
        await refreshJobs(); chrome(); go('jobs'); sync();
      } catch { msg.append(h('div', { class: 'err' }, 'No signal. You need a connection to sign in.')); }
      finally { btn.disabled = false; }
    } }, 'Sign in');
    put(h('p', { class: 'muted' }, 'Nexa Circuit courier network'), h('h1', {}, 'Sign in'),
      h('label', {}, 'Phone'), phone, h('label', {}, 'PIN'), pin, msg, btn,
      h('p', { class: 'muted small' }, 'New phone? Ask ops to approve it before first use.'));
  }

  function statusPill(j) {
    return { Funded: h('span', { class: 'pill' }, 'FUNDED · Pick up'), PickedUp: h('span', { class: 'pill blue' }, 'PICKED UP · In transit'),
      Delivered: h('span', { class: 'pill warn' }, 'DELIVERED · Needs OTP'), Rejected: h('span', { class: 'pill bad' }, 'REJECTED · Return it') }[j.state] || h('span', { class: 'pill' }, j.state);
  }
  const actionLabel = { pickup: 'Start pickup', arrive: 'I have arrived', otp: 'Enter buyer OTP', return: 'Return to seller' };

  async function jobs() {
    const list = get(K.jobs) || [];
    const waiting = new Set((await store.list()).map(i => i.orderId));
    put(h('p', { class: 'muted' }, `Nexa Circuit · ${localStorage.getItem(K.name) || ''}`), h('h1', {}, 'My jobs'),
      list.length === 0 ? h('p', { class: 'muted' }, 'No jobs right now.') : null,
      list.map(j => h('div', { class: 'card' },
        h('div', { class: 'row' }, h('strong', {}, j.orderNumber), statusPill(j)),
        h('p', {}, j.itemDescription),
        h('p', { class: 'muted small' }, [j.pickupAddress, j.deliveryAddress].filter(Boolean).join(' → ')),
        h('div', { class: 'row' }, h('strong', {}, money(j.deliveryFee) + ' fee'),
          waiting.has(j.id) ? h('span', { class: 'pill warn' }, 'Waiting to sync') : j.nextAction !== 'none' ? h('button', { class: 'btn', style: 'width:auto;padding:.6rem 1.2rem;margin:0', onclick: () => go('job', j) }, actionLabel[j.nextAction]) : null))));
  }

  function progress(total, current) {
    return h('div', { class: 'progress' }, ...Array.from({ length: total }, (_, i) => h('i', { class: i <= current ? 'on' : '' })));
  }
  function stepMeta(n, total, label) { return h('p', { class: 'step-meta' }, `Step ${n} of ${total} · ${label}`); }

  // Listing photos the buyer approved. Fetched with the courier's token; skipped quietly when offline.
  function listingPhotos(j) {
    const row = h('div', { class: 'photos' });
    (j.listingPhotoIds || []).forEach(async id => {
      try {
        const res = await fetch(`${API}/courier/jobs/${j.id}/proofs/${id}/file`, { headers: { Authorization: 'Bearer ' + token() } });
        if (!res.ok) return;
        row.append(h('div', { class: 'photo' }, h('img', { src: URL.createObjectURL(await res.blob()), alt: 'listing photo' })));
      } catch { /* offline */ }
    });
    return row;
  }
  function job(j) {
    const msg = h('div');
    const fail = e => msg.replaceChildren(h('div', { class: 'err' }, e.message || String(e)));
    const done = text => { flash(text); go('jobs'); };
    const summary = h('div', { class: 'card' }, h('strong', {}, j.itemDescription),
      h('p', { class: 'muted small' }, `Seller ${j.sellerName} · ${j.sellerPhone}`),
      h('p', { class: 'muted small' }, `Buyer ${j.buyerName} · ${j.buyerPhone}`),
      h('p', { class: 'small' }, [j.pickupAddress, j.deliveryAddress].filter(Boolean).join(' → ')),
      j.nextAction === 'pickup' ? h('p', { class: 'muted small' }, 'Check the item matches these photos:') : null,
      j.nextAction === 'pickup' ? listingPhotos(j) : null);
    const head = (step, total, label) => [
      h('p', { class: 'muted' }, `${j.orderNumber} · ${j.state}`),
      h('h1', {}, actionTitle(j.nextAction)),
      progress(total, step),
      stepMeta(step + 1, total, label),
      summary
    ];
    const backJobs = h('button', { class: 'btn ghost', onclick: () => go('jobs') }, 'Back to jobs');
    let step = 0;

    if (j.nextAction === 'pickup') {
      const pics = photoPicker(3, 'Photos of the item');
      const weight = h('input', { type: 'number', step: '0.01', inputmode: 'decimal' });
      const seal = h('input', { value: j.sealId || '', placeholder: 'SL-558201' });
      const paint = () => {
        msg.replaceChildren();
        const body = step === 0 ? [pics.el, h('p', { class: 'muted small' }, 'Take at least 3 photos of the packed item.')]
          : step === 1 ? [h('label', {}, 'Weight (kg)'), weight, h('label', {}, 'Tamper seal ID'), seal]
          : [h('p', {}, 'GPS will be captured when you confirm. The buyer gets their code once this is sent.'),
              h('p', { class: 'muted small' }, `Weight ${weight.value || '—'} kg · seal ${seal.value || '—'}`)];
        const next = h('button', { class: 'btn', onclick: async () => {
          try {
            if (step === 0) { if (!pics.ok()) throw new Error('Take at least 3 photos.'); step = 1; paint(); return; }
            if (step === 1) { if (!(+weight.value > 0)) throw new Error('Enter the weight in kg.'); if (!seal.value.trim()) throw new Error('Enter the tamper seal ID.'); step = 2; paint(); return; }
            next.disabled = true;
            const p = await gps();
            await queueAction(j, 'pickup', { weightKg: +weight.value, sealId: seal.value.trim(), ...p, clientTime: new Date().toISOString() }, pics.files);
            done('Pickup saved. The buyer gets their code once it is sent.');
          } catch (e) { fail(e); next.disabled = false; }
        } }, step < 2 ? 'Continue' : 'Confirm pickup');
        const prev = step > 0 ? h('button', { class: 'btn ghost', onclick: () => { step--; paint(); } }, 'Back') : backJobs;
        put(...head(step, 3, ['Photos', 'Weight & seal', 'Confirm'][step]), ...body, msg,
          h('div', { class: 'actions-col' }, next, problemButton(j, fail, done), prev));
      };
      paint();
    } else if (j.nextAction === 'arrive') {
      const pics = photoPicker(1, 'Arrival photo');
      const paint = () => {
        msg.replaceChildren();
        const body = step === 0
          ? [pics.el, h('p', { class: 'muted small' }, 'A photo at the door is enough.')]
          : [h('p', {}, 'GPS will be captured when you confirm arrival.')];
        const next = h('button', { class: 'btn', onclick: async () => {
          try {
            if (step === 0) { if (!pics.ok()) throw new Error('Take an arrival photo.'); step = 1; paint(); return; }
            next.disabled = true; const p = await gps();
            await queueAction(j, 'arrived', { ...p, clientTime: new Date().toISOString() }, pics.files); done('Arrival saved.');
          } catch (e) { fail(e); next.disabled = false; }
        } }, step === 0 ? 'Continue' : 'I have arrived');
        const prev = step > 0 ? h('button', { class: 'btn ghost', onclick: () => { step--; paint(); } }, 'Back') : backJobs;
        put(...head(step, 2, ['Photo', 'Confirm'][step]), ...body, msg,
          h('div', { class: 'actions-col' }, next, unreachableButton(j, done), problemButton(j, fail, done), prev));
      };
      paint();
    } else if (j.nextAction === 'otp') {
      const otp = h('input', { class: 'otp', inputmode: 'numeric', maxlength: 6, placeholder: '••••••', autocomplete: 'one-time-code' });
      const paint = () => {
        msg.replaceChildren();
        const body = step === 0
          ? [h('div', { class: 'banner', style: 'margin:.5rem 0' }, 'Ask for the code only after the buyer has inspected the item and accepted it.')]
          : [h('label', {}, '6-digit code from the buyer\'s SMS'), otp, h('p', { class: 'muted small' }, 'After 5 wrong codes the order goes to ops.')];
        const next = h('button', { class: 'btn', onclick: async () => {
          if (step === 0) { step = 1; paint(); return; }
          if (!/^\d{6}$/.test(otp.value.trim())) return fail(new Error('The code is 6 digits.'));
          next.disabled = true; await queueAction(j, 'otp', { otp: otp.value.trim() }, []); go('sync');
        } }, step === 0 ? 'Buyer accepted — enter code' : 'Release payment');
        const prev = step > 0 ? h('button', { class: 'btn ghost', onclick: () => { step--; paint(); } }, 'Back') : backJobs;
        put(...head(step, 2, ['Inspect', 'Code'][step]), ...body, msg,
          h('div', { class: 'actions-col' }, next,
            h('button', { class: 'btn danger', onclick: () => go('reject', j) }, 'Reject at the door'),
            unreachableButton(j, done), prev));
      };
      paint();
    } else if (j.nextAction === 'return') {
      const pics = photoPicker(1, 'Return photo'); const sc = h('input', { type: 'checkbox', style: 'width:auto' });
      const paint = () => {
        msg.replaceChildren();
        const body = step === 0 ? [pics.el]
          : [h('label', {}, sc, ' The seller has the item back'), h('p', { class: 'muted small' }, 'Ops will decide the refund after this.')];
        const next = h('button', { class: 'btn', onclick: async () => {
          try {
            if (step === 0) { if (!pics.ok()) throw new Error('Take a photo of the returned item.'); step = 1; paint(); return; }
            if (!sc.checked) throw new Error('The seller must confirm the return.');
            next.disabled = true; const p = await gps();
            await queueAction(j, 'return', { sellerConfirmed: true, ...p, clientTime: new Date().toISOString() }, pics.files);
            done('Return saved. Ops will decide the refund.');
          } catch (e) { fail(e); next.disabled = false; }
        } }, step === 0 ? 'Continue' : 'Seller confirmed return');
        const prev = step > 0 ? h('button', { class: 'btn ghost', onclick: () => { step--; paint(); } }, 'Back') : backJobs;
        put(...head(step, 2, ['Photo', 'Confirm'][step]), ...body, msg, h('div', { class: 'actions-col' }, next, prev));
      };
      paint();
    } else put(h('p', { class: 'muted' }, `${j.orderNumber} · ${j.state}`), h('h1', {}, actionTitle(j.nextAction)), summary,
      h('p', { class: 'muted' }, 'Nothing to do on this job right now.'), backJobs);
  }

  const actionTitle = a => ({ pickup: 'Pickup proof', arrive: 'Arrival', otp: 'Buyer code', return: 'Return to seller' }[a] || 'Job');

  function unreachableButton(j, done) {
    return h('button', { class: 'btn warn', onclick: async () => { await queueAction(j, 'unreachable', {}, []); done('Flagged to ops. Funds stay held.'); } }, 'Buyer unreachable, flag to ops');
  }
  function problemButton(j, fail, done) {
    return h('button', { class: 'btn ghost', onclick: async () => {
      const reason = prompt('What went wrong? (ops will freeze the funds and decide)'); if (!reason) return;
      await queueAction(j, 'dispute', { reason }, []); done('Problem reported. Funds are frozen until ops decide.'); } }, 'Report a problem');
  }

  function reject(j) {
    const reasons = ['Item damaged', 'Wrong item', 'Wrong colour', 'Seal broken or tampered', 'Not as described', 'Other'];
    const sel = h('select', {}, reasons.map(r => h('option', {}, r))); const pics = photoPicker(2, 'Photos'); const msg = h('div');
    let step = 0;
    const paint = () => {
      msg.replaceChildren();
      const body = step === 0 ? [h('label', {}, 'Reason'), sel]
        : step === 1 ? [pics.el, h('p', { class: 'muted small' }, 'Take at least 2 photos of the problem.')]
        : [h('p', {}, `Reason: ${sel.value}`), h('p', { class: 'muted small' }, 'Funds stay frozen. Ops decide the refund. Buyer and seller are told.')];
      const next = h('button', { class: 'btn danger', onclick: async () => {
        try {
          if (step === 0) { step = 1; paint(); return; }
          if (step === 1) { if (!pics.ok()) throw new Error('Take at least 2 photos.'); step = 2; paint(); return; }
          next.disabled = true; const p = await gps();
          await queueAction(j, 'reject', { reasonCode: sel.value, ...p, clientTime: new Date().toISOString() }, pics.files); go('sync');
        } catch (e) { msg.replaceChildren(h('div', { class: 'err' }, e.message)); next.disabled = false; }
      } }, step < 2 ? 'Continue' : 'Reject and freeze funds');
      const prev = h('button', { class: 'btn ghost', onclick: () => { if (step === 0) go('job', j); else { step--; paint(); } } }, 'Back');
      put(h('p', { class: 'muted' }, `${j.orderNumber} · ${j.state}`), h('h1', {}, 'Reject at the door'),
        progress(3, step), h('p', { class: 'step-meta' }, `Step ${step + 1} of 3 · ${['Reason', 'Photos', 'Confirm'][step]}`),
        ...body, msg, h('div', { class: 'actions-col' }, next, prev));
    };
    paint();
  }

  async function syncView() {
    const items = await store.list(); const results = get(K.results) || [];
    put(h('h1', {}, 'Sync'),
      items.length ? h('div', { class: 'banner', style: 'margin:0 0 1rem' }, `${items.length} update(s) waiting. They send when you have signal.` + (lastStop === 'auth' ? ' Sign in again to send them.' : '')) : h('p', { class: 'ok' }, 'Everything is sent.'),
      items.map(i => h('div', { class: 'card' }, h('strong', {}, `${i.orderNumber || ''} · ${i.eventType}`), h('p', { class: 'muted small' }, `${i.photos.length} photo(s) · saved ${new Date(i.createdAt).toLocaleTimeString()}`))),
      h('button', { class: 'btn', onclick: () => sync() }, 'Send now'),
      results.length ? h('h3', {}, 'Recent results') : null,
      results.map(r => h('div', { class: r.ok ? 'ok' : 'err' }, `${r.order || ''} ${r.type}: ${r.message}`)));
  }

  function profile() {
    put(h('h1', {}, 'Profile'), h('div', { class: 'card' }, h('strong', {}, localStorage.getItem(K.name) || ''), h('p', { class: 'muted small' }, 'This phone is bound to your account.')),
      h('button', { class: 'btn danger', onclick: async () => { if ((await store.list()).length && !confirm('Updates are still waiting to send. Sign out anyway?')) return; localStorage.removeItem(K.token); chrome(); go('login'); } }, 'Sign out'));
  }

  // ---- chrome: banner, tabs, network state --
  let flashUntil = 0;
  function flash(text) { flashUntil = Date.now() + 5000; const b = $('banner'); b.hidden = false; b.textContent = text; setTimeout(chrome, 5100); }
  async function chrome() {
    const n = (await store.list()).length;
    $('qcount').textContent = n ? `(${n})` : '';
    $('tabs').hidden = !token();
    if (Date.now() >= flashUntil) {
      const b = $('banner'); b.hidden = !n;
      b.textContent = n ? `${n} update(s) waiting to sync. They will send when you have signal.` : '';
    }
    const net = $('net'); net.textContent = navigator.onLine ? '' : 'Offline'; net.className = 'net' + (navigator.onLine ? '' : ' off');
    document.querySelectorAll('nav button').forEach(x => x.classList.toggle('on', x.dataset.go === current.name));
  }
  document.querySelectorAll('nav button').forEach(b => b.addEventListener('click', () => go(b.dataset.go)));
  window.addEventListener('online', () => { chrome(); sync(); });
  window.addEventListener('offline', chrome);
  setInterval(sync, 30000);

  (async () => { await chrome(); if (token()) { go('jobs'); refreshJobs().then(() => current.name === 'jobs' && jobs()); sync(); } else go('login'); })();
})();
