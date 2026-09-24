// Scrubs a real .godmode/output.jsonl into a fixture: node scrub-output.mjs <in> <out> <projectDir>
// Paths become /project, the home directory ~, session ids zeros; thinking signatures and
// the init line's machine details (memory paths, sockets, installed skills and servers) are dropped.
import { readFileSync, writeFileSync } from 'node:fs';

const [input, output, projectDir] = process.argv.slice(2);
const zeroId = '00000000-0000-0000-0000-000000000000';
const raw = readFileSync(input, 'utf8').split('\n').filter(Boolean).map(line => JSON.parse(line));

const variants = dir => [dir, dir.replaceAll('\\', '/'), dir.replace(/[:\\/]/g, '-')];
const home = process.env.USERPROFILE ?? process.env.HOME ?? '';
const replacements = [
  ...variants(projectDir).map(v => [v, '/project']),
  ...variants(home).map(v => [v, '~']),
  // A session id also appears inside paths, such as a subagent's output file
  ...[...new Set(raw.map(j => j.session_id).filter(Boolean))].map(id => [id, zeroId]),
];
const initKeep = ['type', 'subtype', 'cwd', 'session_id', 'model', 'tools', 'permissionMode', 'claude_code_version', 'uuid'];

function scrubValue(value) {
  if (typeof value === 'string') return replacements.reduce((s, [from, to]) => s.replaceAll(from, to), value).replaceAll('\\', '/');
  if (Array.isArray(value)) return value.map(scrubValue);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([k, v]) =>
      [k, k === 'session_id' ? zeroId : k === 'signature' ? 'REDACTED' : scrubValue(v)]));
  }
  return value;
}

const lines = raw.map(json => {
  const kept = json.type === 'system' && json.subtype === 'init'
    ? Object.fromEntries(initKeep.filter(k => k in json).map(k => [k, json[k]]))
    : json;
  return JSON.stringify(scrubValue(kept));
});
writeFileSync(output, lines.join('\n') + '\n');
