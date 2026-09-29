import './KindLabel.css';

/** A session's kind (`bug`, `feat`, `experiment`, `chat`…) as a small label: its create script's, else its action's name. None without one. */
export function KindLabel({ kind }: { kind?: string | null }) {
  if (!kind) return null;
  return <span className="kind-label" title={`Kind: ${kind}`}>{kind}</span>;
}
