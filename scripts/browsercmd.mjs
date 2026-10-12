const [port, method] = process.argv.slice(2);
const ws = new WebSocket(`ws://127.0.0.1:${port}/devtools/browser`);
await new Promise(r => ws.onopen = r);
ws.onmessage = m => { console.log(m.data.slice(0, 300)); };
ws.onclose = () => { console.log('closed'); process.exit(0); };
ws.send(JSON.stringify({ id: 1, method }));
setTimeout(() => { console.log('timeout'); process.exit(0); }, 8000);
