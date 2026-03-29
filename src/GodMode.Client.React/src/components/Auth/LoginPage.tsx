import { useEffect, useRef, useCallback } from 'react';
import './Auth.css';

interface LoginPageProps {
  clientId: string;
  error?: string;
  onLogin: () => void;
}

export function LoginPage({ clientId, error, onLogin }: LoginPageProps) {
  const btnRef = useRef<HTMLDivElement>(null);
  const initialized = useRef(false);

  const handleCredentialResponse = useCallback(async (response: { credential: string }) => {
    try {
      const res = await fetch('/api/auth/google-login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ credential: response.credential }),
      });

      if (res.ok) {
        onLogin();
      } else {
        window.location.href = '/?error=access_denied';
      }
    } catch {
      window.location.href = '/?error=network';
    }
  }, [onLogin]);

  useEffect(() => {
    if (initialized.current) return;
    initialized.current = true;

    const script = document.createElement('script');
    script.src = 'https://accounts.google.com/gsi/client';
    script.async = true;
    script.onload = () => {
      const google = (window as any).google;
      if (!google?.accounts?.id) return;

      google.accounts.id.initialize({
        client_id: clientId,
        callback: handleCredentialResponse,
      });

      if (btnRef.current) {
        google.accounts.id.renderButton(btnRef.current, {
          type: 'standard',
          theme: 'outline',
          size: 'large',
          text: 'signin_with',
          width: 300,
        });
      }
    };
    document.head.appendChild(script);

    return () => {
      script.remove();
    };
  }, [clientId, handleCredentialResponse]);

  return (
    <div className="auth-page">
      <div className="auth-card">
        <div className="auth-logo">
          <svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 2L2 7l10 5 10-5-10-5z" />
            <path d="M2 17l10 5 10-5" />
            <path d="M2 12l10 5 10-5" />
          </svg>
        </div>
        <h1 className="auth-title">GodMode</h1>
        <p className="auth-subtitle">Sign in to continue</p>

        {error && (
          <div className="auth-error">
            {error === 'access_denied'
              ? 'Access denied. You are not authorized to sign in.'
              : error === 'network'
              ? 'Network error. Please try again.'
              : 'An error occurred during sign in.'}
          </div>
        )}

        <div ref={btnRef} className="auth-google-container" />
      </div>
    </div>
  );
}
