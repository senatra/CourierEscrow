// The buyer tracking page walked through with a mocked API (jsdom): approve photos -> pay -> pickup -> delivery -> accept,
// plus the "provider is down, tap to retry" path and the not-found page.
//   cd tests/buyer-page && npm install jsdom && node flow.test.js
const { JSDOM } = require('jsdom');
const fs = require('fs');
const html = fs.readFileSync(require('path').join(__dirname, '..', '..', 'src', 'CourierEscrow.Api', 'wwwroot', 'track', 'index.html'), 'utf8');

function makeServer(opts = {}) {
  const s = { state: 'Created', confirmed: false, declined: false, account: null, providerDown: !!opts.providerDown, calls: [] };
  const steps = () => [
    { label: 'Confirm the item photos', done: s.confirmed, current: s.state === 'Created' && !s.confirmed },
    { label: 'Paid and held', done: ['Funded','PickedUp','Delivered','Released','Settled'].includes(s.state), current: s.state === 'Funded' },
    { label: 'Picked up', done: ['PickedUp','Delivered','Released','Settled'].includes(s.state), current: s.state === 'PickedUp' },
    { label: 'Courier has arrived', done: ['Delivered','Released','Settled'].includes(s.state), current: s.state === 'Delivered' },
    { label: 'Inspect and share code', done: ['Released','Settled'].includes(s.state), current: s.state === 'Delivered' },
    { label: 'Seller paid', done: s.state === 'Settled', current: s.state === 'Released' },
  ];
  const view = () => ({ orderNumber: 'NX-1001', itemDescription: 'Samsung A15', total: 30800000, currency: 'MWK', state: s.state, sealId: s.state === 'Created' || s.state === 'PaymentPending' || s.state === 'Funded' ? null : 'SEAL-AB12',
    courierName: 'Mphatso Banda', pickedUpAt: null, steps: steps(), productConfirmed: s.confirmed, canConfirmProduct: s.state === 'Created' && !s.confirmed, canDeclineProduct: s.state === 'Created' && !s.confirmed,
    photos: [1,2,3].map(i => ({ id: '0000000' + i + '-0000-0000-0000-000000000000', kind: 'Listing' })), payToAccount: s.account, paymentExpiresAt: s.account ? new Date(Date.now() + 14*60000).toISOString() : null,
    trackingToken: 'TOKEN123', itemAmount: 30000000, deliveryFee: 800000, sellerName: 'Chikondi Phones', productDeclined: s.declined });
  const json = (status, body) => ({ ok: status < 400, status, headers: { get: () => 'application/json' }, json: async () => body, text: async () => JSON.stringify(body) });
  s.fetch = async (url, o = {}) => {
    const m = (o.method || 'GET'); s.calls.push(m + ' ' + url);
    if (url === '/dev/ping') return json(200, { dev: true });
    if (url === '/track/TOKEN123' && m === 'GET') return json(200, view());
    if (url.endsWith('/confirm-product')) {
      if (s.state !== 'Created' && s.state !== 'PaymentPending') return json(409, { title: 'Not allowed in the current state' });
      s.confirmed = true; s.declined = false; s.state = 'PaymentPending';
      if (s.providerDown) return json(200, { alreadyConfirmed: false, state: 'PaymentPending', paymentReady: false, message: 'The payment provider is not answering right now. Your approval is saved. Tap the button to try again.' });
      s.account = 'MOCK-ES123'; return json(200, { alreadyConfirmed: false, state: 'PaymentPending', paymentReady: true, accountNumber: s.account });
    }
    if (url.endsWith('/decline-product')) { s.declined = true; return json(200, { declined: true }); }
    if (url.startsWith('/dev/pay-by-token/')) { s.state = 'Funded'; return json(200, {}); }
    if (url.startsWith('/dev/pickup/')) { s.state = 'PickedUp'; return json(200, { state: 'PickedUp', seal: 'SEAL-AB12' }); }
    if (url.startsWith('/dev/deliver/')) { s.state = 'Delivered'; return json(200, {}); }
    if (url.startsWith('/dev/accept/')) { s.state = 'Released'; return json(200, {}); }
    return json(404, {});
  };
  return s;
}

const sleep = ms => new Promise(r => setTimeout(r, ms));
let fails = 0; const ok = (c, m) => { if (!c) { fails++; console.log('FAIL', m); } else console.log('ok  ', m); };

async function boot(server) {
  const dom = new JSDOM(html, { runScripts: 'dangerously', url: 'http://localhost/t/TOKEN123', pretendToBeVisual: true,
    beforeParse(w) { w.fetch = server.fetch; w.HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', ''); }; w.HTMLDialogElement.prototype.close = function () { this.removeAttribute('open'); }; } });
  await sleep(150); return dom;
}
const text = d => d.window.document.getElementById('root').textContent;
const btn = (d, re) => [...d.window.document.querySelectorAll('button')].find(b => re.test(b.textContent));

(async () => {
  // 1. happy path
  let srv = makeServer(); let d = await boot(srv);
  ok(/Is this the item you are buying/.test(text(d)), 'created: asks buyer to confirm photos');
  ok(d.window.document.querySelectorAll('.photos .thumb').length === 3, 'created: shows 3 listing photos');
  ok(!/Pay to account/.test(text(d)), 'created: NO payment details before approval');
  ok(/K308,000/.test(text(d)) && /Sold by Chikondi Phones/.test(text(d)), 'hero shows total and seller');
  btn(d, /No, this is not/).click(); await sleep(50);
  ok(!srv.calls.some(c => c.includes('decline-product')), 'decline needs a reason (no request sent)');
  d.window.document.querySelector('textarea').value = 'wrong colour'; d.window.document.querySelector('textarea').dispatchEvent(new d.window.Event('input'));
  btn(d, /No, this is not/).click(); await sleep(100);
  ok(srv.declined && /looking into it/.test(text(d)), 'decline sent, page shows review note');
  btn(d, /Yes, this is the item/).click(); await sleep(150);
  ok(srv.confirmed && srv.state === 'PaymentPending', 'approve sent');
  ok(/Pay to account/.test(text(d)) && /MOCK-ES123/.test(text(d)), 'after approval: payment card with account number');
  ok(/Pay within 1[34]:/.test(text(d)), 'countdown shown');
  ok(!!btn(d, /Simulate payment/), 'dev: simulate payment button visible');
  btn(d, /Simulate payment/).click(); await sleep(150);
  ok(/Payment received/.test(text(d)), 'funded: payment received card');
  btn(d, /Simulate courier pickup/).click(); await sleep(150);
  ok(/On the way to you/.test(text(d)) && /SMS/.test(text(d)), 'picked up: code note');
  btn(d, /Simulate courier arrival/).click(); await sleep(150);
  ok(/courier is at your door/i.test(text(d)), 'delivered card');
  btn(d, /Simulate buyer accepting/).click(); await sleep(150);
  ok(/Item accepted/.test(text(d)), 'released card');
  ok(d.window.document.querySelectorAll('.timeline li.done').length >= 5, 'timeline marks steps done');
  
  // theme toggle: explicit choice wins and is remembered
  const root0 = d.window.document.documentElement;
  const tb = d.window.document.getElementById('theme');
  tb.click(); const first = root0.getAttribute('data-theme');
  tb.click(); const second = root0.getAttribute('data-theme');
  ok(first && second && first !== second, 'theme toggle flips between light and dark');
  ok(d.window.localStorage.getItem('nexa-theme') === second, 'theme choice is remembered');
  d.window.close();

  // 2. provider outage then retry
  srv = makeServer({ providerDown: true }); d = await boot(srv);
  btn(d, /Yes, this is the item/).click(); await sleep(150);
  ok(/approval is saved/i.test(text(d)) && !!btn(d, /Get my payment details/), 'outage: approval saved, retry button shown');
  ok(!/Pay to account/.test(text(d)), 'outage: no bogus account number');
  srv.providerDown = false; btn(d, /Get my payment details/).click(); await sleep(150);
  ok(/MOCK-ES123/.test(text(d)), 'retry: payment details appear');
  d.window.close();

  // 3. unknown link
  srv = makeServer(); srv.fetch = async (u) => u === '/dev/ping' ? { ok: false, headers: { get: () => '' } } : { ok: false, status: 404, headers: { get: () => '' }, json: async () => ({}) };
  d = await boot(srv); ok(/Order not found/.test(text(d)), '404: friendly not-found card'); d.window.close();
  console.log(fails ? `${fails} FAILED` : 'ALL PASSED'); process.exit(fails ? 1 : 0);
})();
