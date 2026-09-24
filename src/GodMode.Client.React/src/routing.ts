import { useEffect } from 'react';
import { useAppStore, type ActivePage } from './store';
import { dismissConfirm, getOpenConfirm, subscribeConfirm } from './confirmDialog';

// Hash routing: the store stays the source of truth for what is on screen, and this module
// mirrors it into the URL so each screen is a history entry. Browser back and Android back
// (MAUI maps it to history.back()) then walk back through the screens.

/** A screen the URL can name */
export type Route =
  | { screen: 'home' }
  /** The phone's project list; home on a phone is the inbox */
  | { screen: 'projects' }
  | { screen: 'project'; serverId: string; projectId: string }
  | { screen: 'page'; page: ActivePage };

type Params = Record<string, string>;

interface RouteDef {
  /** Segments after `#/`; a `:name` segment captures a parameter */
  pattern: string;
  toRoute: (params: Params) => Route;
}

/** Every hash the client understands. A new screen (the inbox, say) adds a row here and a case in `formatRoute`. */
const routeTable: readonly RouteDef[] = [
  { pattern: '', toRoute: () => ({ screen: 'home' }) },
  { pattern: 'projects', toRoute: () => ({ screen: 'projects' }) },
  { pattern: 'project/:serverId/:projectId', toRoute: p => ({ screen: 'project', serverId: p.serverId, projectId: p.projectId }) },
  { pattern: 'settings/profiles', toRoute: () => ({ screen: 'page', page: { type: 'profileSettings' } }) },
  { pattern: 'settings/app', toRoute: () => ({ screen: 'page', page: { type: 'appSettings' } }) },
  { pattern: 'servers/add', toRoute: () => ({ screen: 'page', page: { type: 'addServer' } }) },
  { pattern: 'servers/:serverId', toRoute: p => ({ screen: 'page', page: { type: 'editServer', serverId: p.serverId } }) },
  { pattern: 'create', toRoute: () => ({ screen: 'page', page: { type: 'createProject' } }) },
  { pattern: 'create/:serverId/:rootName', toRoute: p => ({ screen: 'page', page: { type: 'createProject', context: { serverId: p.serverId, rootName: p.rootName } } }) },
];

const hashOf = (...segments: string[]) => '#/' + segments.map(encodeURIComponent).join('/');

export function formatRoute(route: Route): string {
  switch (route.screen) {
    case 'home': return hashOf();
    case 'projects': return hashOf('projects');
    case 'project': return hashOf('project', route.serverId, route.projectId);
    case 'page': {
      const page = route.page;
      switch (page.type) {
        case 'profileSettings': return hashOf('settings', 'profiles');
        case 'appSettings': return hashOf('settings', 'app');
        case 'addServer': return hashOf('servers', 'add');
        case 'editServer': return hashOf('servers', page.serverId);
        case 'createProject': return page.context ? hashOf('create', page.context.serverId, page.context.rootName) : hashOf('create');
      }
    }
  }
}

/** The route a hash names, or null when no row of the table matches */
export function parseRoute(hash: string): Route | null {
  const path = hash.replace(/^#\/?/, '');
  const segments = path === '' ? [] : path.split('/');
  for (const def of routeTable) {
    const pattern = def.pattern === '' ? [] : def.pattern.split('/');
    if (pattern.length !== segments.length) continue;
    const params: Params = {};
    const matches = pattern.every((part, i) => {
      if (part.startsWith(':')) {
        try { params[part.slice(1)] = decodeURIComponent(segments[i]); } catch { return false; }
        return true;
      }
      return part === segments[i];
    });
    if (matches) return def.toRoute(params);
  }
  return null;
}

type StoreState = ReturnType<typeof useAppStore.getState>;

function routeOf(state: StoreState): Route {
  if (state.activePage) return { screen: 'page', page: state.activePage };
  if (state.selectedProject) return { screen: 'project', ...state.selectedProject };
  return state.homeView === 'projects' ? { screen: 'projects' } : { screen: 'home' };
}

/** Set while the URL drives the store, so the store's intermediate states are not pushed back into history */
let applying = false;

function applyRoute(route: Route) {
  const store = useAppStore.getState();
  if (formatRoute(routeOf(store)) === formatRoute(route)) return;
  applying = true;
  try {
    applyToStore(store, route);
  } finally {
    applying = false;
  }
}

function applyToStore(store: StoreState, route: Route) {
  switch (route.screen) {
    case 'home':
    case 'projects':
      store.closePage();
      store.clearSelection();
      store.setHomeView(route.screen === 'projects' ? 'projects' : 'inbox');
      break;
    case 'project':
      store.selectProject(route.serverId, route.projectId);
      break;
    case 'page':
      store.setActivePage(route.page);
      break;
  }
}

/** What this module keeps in `history.state`: how many of its entries lie behind this one, and the hash of the one before */
interface Entry {
  godmodeDepth: number;
  previous: string | null;
  /** The entry an open confirm dialog pushes over its screen, so back closes the dialog first */
  godmodeDialog?: boolean;
}

const HOME = formatRoute({ screen: 'home' });

function currentEntry(): Entry | null {
  const state = history.state as Partial<Entry> | null;
  return typeof state?.godmodeDepth === 'number'
    ? { godmodeDepth: state.godmodeDepth, previous: state.previous ?? null, godmodeDialog: state.godmodeDialog === true }
    : null;
}

/** Leaving these for another screen replaces them, so back from a created project goes home rather than to the form */
const isForm = (route: Route | null) => route?.screen === 'page' && (route.page.type === 'createProject' || route.page.type === 'addServer');

let replaceNext = false;
/** Set between a history.back() this module started and its popstate, while location still shows the screen being left */
let awaitingPop = false;

/** Set while the open confirm dialog has its own history entry on top */
let dialogEntry = false;

function syncToUrl(state: StoreState) {
  if (applying || awaitingPop) return;
  const route = routeOf(state);
  const hash = formatRoute(route);
  if (hash === location.hash) return;

  // A question belongs to the screen it was asked on; leaving that screen cancels it
  if (getOpenConfirm()) {
    dismissConfirm();
    // Its entry is being popped; the pop brings this change into the URL
    if (awaitingPop) return;
  }

  const entry = currentEntry() ?? { godmodeDepth: 0, previous: null };
  if (entry.godmodeDepth > 0 && hash === entry.previous) {
    // Returning to the screen before this one: go back rather than stacking a copy of it
    awaitingPop = true;
    history.back();
  } else if (replaceNext || isForm(parseRoute(location.hash))) {
    history.replaceState(entry, '', hash);
  } else {
    history.pushState({ godmodeDepth: entry.godmodeDepth + 1, previous: location.hash } satisfies Entry, '', hash);
  }
  replaceNext = false;
}

function onPopState() {
  if (awaitingPop) {
    // The store already shows this screen, or has moved on since; either way it leads
    awaitingPop = false;
    syncToUrl(useAppStore.getState());
    return;
  }
  const poppedDialog = dialogEntry;
  dialogEntry = false;
  if (getOpenConfirm()) {
    // Back with a question open answers it with Cancel. When the dialog had its own entry, that is all back does
    dismissConfirm();
    if (poppedDialog) return;
  } else if (currentEntry()?.godmodeDialog) {
    // Forward onto a closed dialog's entry: step off it again
    awaitingPop = true;
    history.back();
    return;
  }
  syncFromUrl();
}

/** Gives an opening dialog its own entry, and removes that entry when the dialog closes */
function onConfirmChange() {
  const isOpen = getOpenConfirm() !== null;
  if (isOpen && !dialogEntry && !awaitingPop) {
    const entry = currentEntry() ?? { godmodeDepth: 0, previous: null };
    history.pushState({ godmodeDepth: entry.godmodeDepth + 1, previous: location.hash, godmodeDialog: true } satisfies Entry, '', location.hash);
    dialogEntry = true;
  } else if (!isOpen && dialogEntry) {
    dialogEntry = false;
    awaitingPop = true;
    history.back();
  }
}

function syncFromUrl() {
  const route = parseRoute(location.hash);
  if (!currentEntry()) history.replaceState({ godmodeDepth: 0, previous: null } satisfies Entry, '', location.hash || HOME);
  if (route) applyRoute(route);
  else history.replaceState(currentEntry(), '', formatRoute(routeOf(useAppStore.getState())));
}

/** Back from the current screen: a history step when there is one of ours, otherwise `fallback` (a deep link opened cold) */
export function goBack(fallback: () => void) {
  if ((currentEntry()?.godmodeDepth ?? 0) > 0) {
    history.back();
  } else {
    // The store notifies synchronously, so the flag covers exactly this change
    replaceNext = true;
    fallback();
    replaceNext = false;
  }
}

/** Keeps the URL hash and the store's screen in step. Mount once, in the Shell. */
export function useHashRoute() {
  useEffect(() => {
    if (!currentEntry()) {
      // First load: put home underneath a deep link, so back from it lands in the app, not outside
      const route = parseRoute(location.hash);
      const hash = route ? formatRoute(route) : HOME;
      history.replaceState({ godmodeDepth: 0, previous: null } satisfies Entry, '', HOME);
      if (hash !== HOME) history.pushState({ godmodeDepth: 1, previous: HOME } satisfies Entry, '', hash);
    }
    syncFromUrl();
    const unsubscribe = useAppStore.subscribe(syncToUrl);
    const unsubscribeConfirm = subscribeConfirm(onConfirmChange);
    window.addEventListener('popstate', onPopState);
    return () => {
      unsubscribe();
      unsubscribeConfirm();
      window.removeEventListener('popstate', onPopState);
    };
  }, []);
}
