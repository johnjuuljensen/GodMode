/** The phone's home routes (#172): `#/` is the inbox, `#/projects` the project list. */
import { describe, expect, it, vi } from 'vitest';
import { formatRoute, parseRoute } from './routing';

vi.mock('./signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('./services/hostApi', () => ({}));

describe('home routes', () => {
  it.each(['#/', '#/projects'])('%s round-trips', hash => {
    expect(formatRoute(parseRoute(hash)!)).toBe(hash);
  });

  it('names the project list apart from home', () => {
    expect(parseRoute('#/projects')).toEqual({ screen: 'projects' });
    expect(parseRoute('#/')).toEqual({ screen: 'home' });
  });
});
