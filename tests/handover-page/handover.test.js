// The development hand-over screen against a mocked API (jsdom): a wrong code is counted, the courier and then the buyer can
// type the code, the right one releases the money, and the payouts are sent and confirmed.
//   cd tests/handover-page && npm install && npm test
const { JSDOM } = require('jsdom');
const fs = require('fs');
const path = require('path');
const html = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'CourierEscrow.Api', 'wwwroot', 'dev', 'handover.html'), 'utf8');

const REAL = '482913';
function makeServer() {
  const s = { state: 'Delivered', attempts: 5, used: false, locked: false, payouts: [], calls: [], by: [] };
  const json = (status, body) => ({ ok: status < 400, status, text: async () => JSON.stringify(body) });
  const status = () => ({ escrowId: 'E1', state: s.state, currency: 'MWK', total: 30800000, codeIssued: true, codeUsed: s.used, codeLocked: s.locked, attemptsLeft: s.attempts, payouts: s.payouts });
  s.fetch = async (url, o = {}) => {
    const m = o.method || 'GET'; s.calls.push(m + ' ' + url);
    if (url === '/dev/ping') return json(200, { dev: true });
    if (url === '/dev/handover/TOKEN123') return json(200, status());
    if (url === '/dev/sms?escrowId=E1') return json(200, [{ at: new Date().toISOString(), template: 'picked_up_buyer', body: 'Give this code ONLY after you inspect: 482 913. Track: http://x/t/9999999 valid 24h' }]);
    if (url === '/dev/handover/TOKEN123/code') {
      const b = JSON.parse(o.body); s.by.push('courier');
      if (b.code === REAL) {
        s.state = 'Released'; s.used = true;
        s.payouts = [{ kind: 'Seller', payee: 'Chikondi Phones', status: 'Queued', amount: 29400000, fee: 0, currency: 'MWK' }, { kind: 'Courier', payee: 'Mphatso Banda', status: 'Queued', amount: 800000, fee: 0, currency: 'MWK' }];
        return json(200, { result: 'Accepted', attemptsLeft: s.attempts, state: s.state, replayed: false });
      }
      s.attempts--; return json(200, { result: 'Wrong', attemptsLeft: s.attempts, state: s.state, replayed: false });
    }
    if (url === '/dev/handover/TOKEN123/payouts') { s.state = 'Settled'; s.payouts = s.payouts.map(p => ({ ...p, status: 'Confirmed' })); return json(200, status()); }
    return json(404, { title: 'Not found' });
  };
  return s;
}

const sleep = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const ok = (c, name) => { console.log((c ? 'ok   ' : 'FAIL ') + name); if (!c) failed++; };

(async () => {
  const s = makeServer();
  const dom = new JSDOM(html, { runScripts: 'dangerously', url: 'http://localhost:5080/dev/handover?t=http%3A%2F%2Flocalhost%3A5080%2Ft%2FTOKEN123',
    beforeParse(w) { w.fetch = s.fetch; w.crypto.randomUUID = w.crypto.randomUUID || (() => Math.random().toString(16).slice(2)); } });
  const d = dom.window.document;
  await sleep(100);

  ok(!d.getElementById('entry').hidden, 'the code box appears once the order is at the door');
  ok(d.getElementById('tok').value.includes('TOKEN123'), 'the link in the address bar is picked up');

  const type = v => { const i = d.getElementById('code'); i.value = v; i.dispatchEvent(new dom.window.Event('input', { bubbles: true })); };
  const submit = async () => { d.getElementById('submit').click(); await sleep(100); };

  type('12a345b6');
  ok(d.getElementById('code').value === '123456', 'only digits are kept, at most six');
  type('123');
  ok(d.getElementById('submit').disabled, 'submit waits for six digits');

  type('111111'); await submit();
  ok(/not right\. 4 tries left/.test(d.getElementById('entryMsg').textContent), 'a wrong code says how many tries are left');
  ok(d.getElementById('money').hidden, 'no payouts are shown after a wrong code');

  await (async () => { d.getElementById('peek').click(); await sleep(100); })();
  ok(/482913/.test(d.getElementById('smsText').textContent), 'the test SMS code is read from the spaced "482 913" form');
  d.getElementById('fill').click();
  ok(d.getElementById('code').value === REAL, 'the code can be filled in from the test inbox');

  await submit();
  ok(/Code accepted/.test(d.getElementById('entryMsg').textContent) && !d.querySelector('[data-who]'), 'the courier’s right code is accepted and there is no buyer option');
  ok(d.getElementById('courierLink').value === 'http://localhost:5080/dev/handover?t=TOKEN123' && !d.getElementById('linkBox').hidden, 'a courier link is shown');
  ok(!d.getElementById('money').hidden && d.getElementById('payRows').children.length === 2, 'seller and courier payouts are listed');
  ok(/Seller · Chikondi Phones/.test(d.getElementById('payRows').textContent) && /Queued/.test(d.getElementById('payRows').textContent), 'payouts start queued');

  d.getElementById('pay').click(); await sleep(100);
  ok(/Confirmed/.test(d.getElementById('payRows').textContent) && d.getElementById('state').textContent === 'Settled', 'sending the payouts confirms them and settles the order');
  ok(d.getElementById('pay').hidden, 'the payout button goes away when nothing is left to pay');

  console.log(failed ? failed + ' FAILED' : 'ALL PASSED');
  process.exit(failed ? 1 : 0);
})();
