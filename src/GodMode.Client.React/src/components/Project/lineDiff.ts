export interface DiffLine {
  op: ' ' | '-' | '+';
  text: string;
}

// Beyond this many line pairs the diff is shown as all removed then all added, rather than computed
const MAX_CELLS = 250_000;

/** A line diff of two texts (longest common subsequence), for an Edit's old and new strings. */
export function lineDiff(before: string, after: string): DiffLine[] {
  const a = before === '' ? [] : before.split('\n');
  const b = after === '' ? [] : after.split('\n');
  // Lines shared at the start and end need no table
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start++;
  let endA = a.length, endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) { endA--; endB--; }

  const head = a.slice(0, start).map(text => ({ op: ' ' as const, text }));
  const tail = a.slice(endA).map(text => ({ op: ' ' as const, text }));
  const midA = a.slice(start, endA), midB = b.slice(start, endB);
  const n = midA.length, m = midB.length;

  if (n * m > MAX_CELLS) {
    return [...head, ...midA.map(text => ({ op: '-' as const, text })), ...midB.map(text => ({ op: '+' as const, text })), ...tail];
  }

  // lcs[i][j]: the longest common subsequence of midA[i..] and midB[j..]
  const lcs = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      lcs[i][j] = midA[i] === midB[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
    }
  }
  const mid: DiffLine[] = [];
  let i = 0, j = 0;
  while (i < n && j < m) {
    if (midA[i] === midB[j]) { mid.push({ op: ' ', text: midA[i] }); i++; j++; }
    else if (lcs[i + 1][j] >= lcs[i][j + 1]) mid.push({ op: '-', text: midA[i++] });
    else mid.push({ op: '+', text: midB[j++] });
  }
  while (i < n) mid.push({ op: '-', text: midA[i++] });
  while (j < m) mid.push({ op: '+', text: midB[j++] });
  return [...head, ...mid, ...tail];
}
