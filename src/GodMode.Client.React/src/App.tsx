import { useEffect, useState, useCallback } from 'react';
import { useAppStore } from './store';
import { isAuthorized, getApiKey, setApiKey } from './services/hostApi';
import { Shell } from './components/Shell';
import { LoginPage } from './components/Auth/LoginPage';

type AuthState = 'checking' | 'authorized' | 'needs-key' | 'rejected' | 'unreachable';

/** Asks the backend whether this client may proceed with the key it holds (if any). */
async function probeAuth(): Promise<AuthState> {
  try {
    if (await isAuthorized()) return 'authorized';
    // A key was held but the server refused it, as opposed to none entered yet
    return getApiKey() !== null ? 'rejected' : 'needs-key';
  } catch {
    return 'unreachable';
  }
}

export default function App() {
  const loadServers = useAppStore(s => s.loadServers);
  const [auth, setAuth] = useState<AuthState>('checking');

  useEffect(() => {
    probeAuth().then(setAuth);
  }, []);

  useEffect(() => {
    if (auth === 'authorized') {
      loadServers().catch(console.error);
    }
  }, [auth, loadServers]);

  const submitKey = useCallback(async (key: string) => {
    setApiKey(key);
    setAuth(await probeAuth());
  }, []);

  if (auth === 'checking') return null;

  if (auth !== 'authorized') {
    return <LoginPage error={auth === 'unreachable' ? 'network' : auth === 'rejected' ? 'rejected' : undefined} onSubmit={submitKey} />;
  }

  return <Shell />;
}
