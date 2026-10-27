/** A transcript line's time as its tooltip says it: "14:05" today, "27 Oct 14:05" another day, with the year when not this one. */
export function messageTime(timestamp: string, now: Date = new Date()): string | undefined {
  const at = new Date(timestamp);
  if (Number.isNaN(at.getTime())) return undefined;
  const time = `${String(at.getHours()).padStart(2, '0')}:${String(at.getMinutes()).padStart(2, '0')}`;
  if (at.toDateString() === now.toDateString()) return time;
  const date = at.toLocaleDateString([], {
    day: 'numeric', month: 'short', ...(at.getFullYear() !== now.getFullYear() ? { year: 'numeric' } : {}),
  });
  return `${date} ${time}`;
}
