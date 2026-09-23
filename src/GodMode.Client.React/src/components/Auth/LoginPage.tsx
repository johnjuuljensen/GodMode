import { useState, type FormEvent } from 'react';
import './Auth.css';

interface LoginPageProps {
  /** 'rejected': the server refused the entered key; 'network': the server could not be reached. */
  error?: 'rejected' | 'network';
  onSubmit: (apiKey: string) => Promise<void>;
}

export function LoginPage({ error, onSubmit }: LoginPageProps) {
  const [key, setKey] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    const trimmed = key.trim();
    if (!trimmed) return;
    setSubmitting(true);
    try {
      await onSubmit(trimmed);
    } finally {
      setSubmitting(false);
    }
  };

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
        <p className="auth-subtitle">This server requires an API key</p>

        {error && (
          <div className="auth-error">
            {error === 'rejected'
              ? 'The server did not accept that key.'
              : 'Could not reach the server. Please try again.'}
          </div>
        )}

        <form className="auth-form" onSubmit={handleSubmit}>
          <label className="auth-label" htmlFor="auth-api-key">API key</label>
          <input
            id="auth-api-key"
            className="auth-input"
            type="password"
            autoComplete="current-password"
            autoFocus
            value={key}
            onChange={e => setKey(e.target.value)}
            placeholder="Authentication:ApiKey from the server's configuration"
          />
          <button className="auth-submit-btn" type="submit" disabled={submitting || !key.trim()}>
            {submitting ? 'Checking...' : 'Continue'}
          </button>
        </form>
        <p className="auth-hint">
          The key is stored in this browser only. On a GitHub Codespace server, use a GitHub token
          belonging to the codespace owner.
        </p>
      </div>
    </div>
  );
}
