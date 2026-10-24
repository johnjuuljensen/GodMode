/** The reset as the row says it: "15:50" today, "Mon 15:50" on another day. */
export function resetTime(until: string, now: Date = new Date()): string {
  const at = new Date(until);
  const time = `${String(at.getHours()).padStart(2, '0')}:${String(at.getMinutes()).padStart(2, '0')}`;
  return at.toDateString() === now.toDateString() ? time : `${at.toLocaleDateString([], { weekday: 'short' })} ${time}`;
}
