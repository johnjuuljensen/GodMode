/** The inbox card's icons (#548): drawn in the text's colour, named by the button that holds them. */
const ICON = { width: 16, height: 16, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round', strokeLinejoin: 'round', 'aria-hidden': true } as const;

/** Go to: an arrow into the session. */
export const GoToIcon = () => (
  <svg {...ICON}><line x1="5" y1="12" x2="19" y2="12" /><polyline points="13 6 19 12 13 18" /></svg>
);

/** Mark seen: a check. */
export const SeenIcon = () => (
  <svg {...ICON}><polyline points="5 12.5 10 17.5 19 7" /></svg>
);
