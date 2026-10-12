(async () => {
  const t0 = performance.now();
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  location.hash = '#/servers/add';
  let url;
  for (let i = 0; i < 200 && !(url = document.querySelector('input[placeholder="http://localhost:31337"]')); i++) await sleep(25);
  if (!url) return 'no form; hash=' + location.hash;
  const set = (el, v) => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, v); el.dispatchEvent(new Event('input', { bubbles: true })); };
  set(url, 'http://10.0.2.2:31374');
  set(document.querySelector('input[placeholder="My Server"]'), 'T274');
  set(document.querySelector('input[type=password]'), 'test274key-not-secret-0123456789');
  await sleep(50);
  const btn = [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Add Server');
  const tClick = performance.now(); btn.click();
  const log = [];
  let last = '';
  for (let i = 0; i < 1200; i++) {
    const b = [...document.querySelectorAll('button')].find(b => /Add(ing)?/.test(b.textContent));
    const s = location.hash + '|' + (b ? b.textContent.trim() : 'no-button') + '|' + (document.querySelector('.error, .form-error')?.textContent ?? '');
    if (s !== last) { log.push(Math.round(performance.now() - tClick) + 'ms ' + s); last = s; }
    if (!b) break;
    await sleep(25);
  }
  return 'page-ready+' + Math.round(tClick - t0) + 'ms; ' + log.join(' ; ');
})()
