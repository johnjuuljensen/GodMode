import { useEffect, useState, useCallback } from 'react';
import { useAppStore } from './store';
import { Shell } from './components/Shell';
import { LoginPage } from './components/Auth/LoginPage';

interface AuthChallenge {
  method: string;
  authenticated: boolean;
}

export default function App() {
  const loadServers = useAppStore(s => s.loadServers);
  const [challenge, setChallenge] = useState<AuthChallenge | null>(null);
  const [loading, setLoading] = useState(true);

  const checkAuth = useCallback(async () => {
    try {
      const res = await fetch('/api/auth/challenge');
      if (res.ok) {
        setChallenge(await res.json());
      } else {
        setChallenge({ method: 'none', authenticated: true });
      }
    } catch {
      setChallenge({ method: 'none', authenticated: true });
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    checkAuth();
  }, [checkAuth]);

  useEffect(() => {
    if (challenge && (challenge.method === 'none' || challenge.authenticated)) {
      loadServers().catch(console.error);
    }
  }, [challenge, loadServers]);

  if (loading) return null;

  if (challenge?.method === 'google' && !challenge.authenticated) {
    const params = new URLSearchParams(window.location.search);
    const error = params.get('error') ?? undefined;
    return <LoginPage error={error} />;
  }

  return <Shell />;
}
