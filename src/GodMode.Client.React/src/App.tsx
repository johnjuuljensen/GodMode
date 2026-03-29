import { useEffect, useState, useCallback } from 'react';
import { useAppStore } from './store';
import { Shell } from './components/Shell';
import { LoginPage } from './components/Auth/LoginPage';
import { ConfigPage } from './components/Auth/ConfigPage';

interface AuthStatus {
  googleAuthEnabled: boolean;
  googleClientId: string | null;
  configured: boolean;
  allowedEmail: string | null;
  authenticated: boolean;
  email: string | null;
}

export default function App() {
  const loadServers = useAppStore(s => s.loadServers);
  const [authStatus, setAuthStatus] = useState<AuthStatus | null>(null);
  const [loading, setLoading] = useState(true);

  const checkAuth = useCallback(async () => {
    try {
      const res = await fetch('/api/auth/status');
      if (res.ok) {
        const data: AuthStatus = await res.json();
        setAuthStatus(data);
      } else {
        setAuthStatus({ googleAuthEnabled: false, googleClientId: null, configured: false, allowedEmail: null, authenticated: false, email: null });
      }
    } catch {
      setAuthStatus({ googleAuthEnabled: false, googleClientId: null, configured: false, allowedEmail: null, authenticated: false, email: null });
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    checkAuth();
  }, [checkAuth]);

  useEffect(() => {
    if (authStatus && (!authStatus.googleAuthEnabled || authStatus.authenticated)) {
      loadServers().catch(console.error);
    }
  }, [authStatus, loadServers]);

  if (loading) return null;

  if (authStatus?.googleAuthEnabled) {
    if (!authStatus.configured) {
      return <ConfigPage onConfigured={checkAuth} />;
    }

    if (!authStatus.authenticated) {
      const params = new URLSearchParams(window.location.search);
      const error = params.get('error') ?? undefined;
      return (
        <LoginPage
          clientId={authStatus.googleClientId!}
          allowedEmail={authStatus.allowedEmail!}
          error={error}
          onLogin={checkAuth}
        />
      );
    }
  }

  return <Shell />;
}
