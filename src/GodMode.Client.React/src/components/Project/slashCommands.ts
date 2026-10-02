// The composer's completion of the slash commands a session takes (#31): its status's SlashCommands, which the
// server passes to claude; any other of claude's commands it refuses.

/** At most this many commands are offered at once. */
const MAX_OFFERED = 8;

/** The commands a `/word` typed alone starts, unless it is one already (then Enter sends it). */
export function offeredCommands(value: string, commands: readonly string[] | null | undefined): string[] {
  const typed = /^\/(\S*)$/.exec(value)?.[1].toLowerCase();
  if (typed === undefined || !commands) return [];
  const offered = commands.filter(c => c.toLowerCase().startsWith(typed)).slice(0, MAX_OFFERED);
  return offered.length === 1 && offered[0].toLowerCase() === typed ? [] : offered;
}
