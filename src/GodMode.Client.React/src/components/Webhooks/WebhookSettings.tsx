import { useState, useEffect, useCallback } from 'react';
import { useAppStore } from '../../store';
import type { WebhookInfo } from '../../signalr/types';
import { Toggle, RowDelete } from '../settings-shared';
import '../settings-common.css';

const BackArrow = () => (
  <svg width={16} height={16} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round"><path d="M10 4L6 8l4 4"/></svg>
);

type View = 'list' | 'create';

export function WebhookSettings() {
  const serverConnections = useAppStore(s => s.serverConnections);

  const conn = serverConnections.find(c => c.connectionState === 'connected');
  const hub = conn?.hub;
  const profiles = conn?.profiles ?? [];
  const roots = conn?.roots ?? [];

  const [webhooks, setWebhooks] = useState<WebhookInfo[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<View>('list');

  // Create form state
  const [newKeyword, setNewKeyword] = useState('');
  const [newProfile, setNewProfile] = useState(profiles[0]?.Name ?? '');
  const [newRoot, setNewRoot] = useState('');
  const [newAction, setNewAction] = useState('');
  const [newDescription, setNewDescription] = useState('');
  const [mappingPairs, setMappingPairs] = useState<{ input: string; path: string }[]>([]);

  // Token reveal
  const [revealedToken, setRevealedToken] = useState<{ keyword: string; token: string } | null>(null);

  // Filtered roots for selected profile
  const filteredRoots = roots.filter(r => r.ProfileName === newProfile);
  const selectedRoot = filteredRoots.find(r => r.Name === newRoot);
  const actions = selectedRoot?.Actions ?? [];

  const loadWebhooks = useCallback(async () => {
    if (!hub) return;
    setLoading(true);
    setError(null);
    try {
      const result = await hub.listWebhooks();
      setWebhooks(result);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load webhooks');
    } finally {
      setLoading(false);
    }
  }, [hub]);

  useEffect(() => { loadWebhooks(); }, [loadWebhooks]);

  useEffect(() => {
    const available = roots.filter(r => r.ProfileName === newProfile);
    if (available.length > 0 && !available.find(r => r.Name === newRoot)) {
      setNewRoot(available[0].Name);
      setNewAction('');
    } else if (available.length === 0) {
      setNewRoot('');
      setNewAction('');
    }
  }, [newProfile, roots, newRoot]);

  const goList = () => { setView('list'); setError(null); };

  const handleCreate = async () => {
    if (!hub || !newKeyword.trim() || !newProfile || !newRoot) return;
    setError(null);
    try {
      const inputMapping = mappingPairs.length > 0
        ? Object.fromEntries(mappingPairs.filter(p => p.input.trim() && p.path.trim()).map(p => [p.input, p.path]))
        : null;
      await hub.createWebhook(newKeyword.trim(), newProfile, newRoot, newAction || null, newDescription.trim() || null, inputMapping);
      const fullToken = await hub.regenerateWebhookToken(newKeyword.trim());
      setRevealedToken({ keyword: newKeyword.trim(), token: fullToken });
      setNewKeyword('');
      setNewDescription('');
      setMappingPairs([]);
      goList();
      await loadWebhooks();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create webhook');
    }
  };

  const handleDelete = async (keyword: string) => {
    if (!hub) return;
    setError(null);
    try {
      await hub.deleteWebhook(keyword);
      if (revealedToken?.keyword === keyword) setRevealedToken(null);
      await loadWebhooks();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to delete webhook');
    }
  };

  const handleToggleEnabled = async (w: WebhookInfo) => {
    if (!hub) return;
    setError(null);
    try {
      await hub.updateWebhook(w.Keyword, null, null, null, !w.Enabled);
      await loadWebhooks();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to update webhook');
    }
  };

  const handleRegenerateToken = async (keyword: string) => {
    if (!hub) return;
    setError(null);
    try {
      const newToken = await hub.regenerateWebhookToken(keyword);
      setRevealedToken({ keyword, token: newToken });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to regenerate token');
    }
  };

  const copyToClipboard = (text: string) => {
    navigator.clipboard.writeText(text).catch(() => {});
  };

  const addMappingPair = () => setMappingPairs([...mappingPairs, { input: '', path: '' }]);
  const updateMappingPair = (idx: number, field: 'input' | 'path', val: string) => {
    const updated = [...mappingPairs];
    updated[idx] = { ...updated[idx], [field]: val };
    setMappingPairs(updated);
  };
  const removeMappingPair = (idx: number) => setMappingPairs(mappingPairs.filter((_, i) => i !== idx));

  const baseUrl = window.location.origin;

  // ── Create view ──
  if (view === 'create') {
    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={goList}><BackArrow /> Webhooks</button>
        </div>
        <div className="settings-header"><h2>New Webhook</h2></div>
        {error && <div className="settings-error">{error}</div>}
        <div className="form-group">
          <label>Keyword (URL slug)</label>
          <input type="text" value={newKeyword} onChange={e => setNewKeyword(e.target.value.replace(/[^a-zA-Z0-9_-]/g, ''))} placeholder="e.g. deploy, review, triage" autoFocus />
        </div>
        <div className="settings-form-row">
          <div className="form-group">
            <label>Profile</label>
            <select value={newProfile} onChange={e => setNewProfile(e.target.value)}>
              {profiles.map(p => <option key={p.Name} value={p.Name}>{p.Name}</option>)}
            </select>
          </div>
          <div className="form-group">
            <label>Root</label>
            <select value={newRoot} onChange={e => setNewRoot(e.target.value)}>
              {filteredRoots.map(r => <option key={r.Name} value={r.Name}>{r.Name}</option>)}
            </select>
          </div>
          {actions.length > 0 && (
            <div className="form-group">
              <label>Action</label>
              <select value={newAction} onChange={e => setNewAction(e.target.value)}>
                <option value="">(default)</option>
                {actions.map(a => <option key={a.Name} value={a.Name}>{a.Name}</option>)}
              </select>
            </div>
          )}
        </div>
        <div className="form-group">
          <label>Description</label>
          <input type="text" value={newDescription} onChange={e => setNewDescription(e.target.value)} placeholder="e.g. Triggered by GitHub issue webhooks" />
        </div>
        <details className="settings-details">
          <summary>Input Mapping (optional — maps payload fields to root inputs)</summary>
          <div className="settings-details-body">
            {mappingPairs.map((pair, idx) => (
              <div key={idx} className="settings-kv-row">
                <input type="text" value={pair.input} onChange={e => updateMappingPair(idx, 'input', e.target.value)} placeholder="Input key (e.g. name)" />
                <span className="kv-arrow">&larr;</span>
                <input type="text" value={pair.path} onChange={e => updateMappingPair(idx, 'path', e.target.value)} placeholder="JSON path (e.g. $.title)" />
                <button className="btn btn-danger btn-sm" onClick={() => removeMappingPair(idx)}>x</button>
              </div>
            ))}
            <button className="settings-kv-add" onClick={addMappingPair}>+ Add mapping</button>
          </div>
        </details>
        <div className="settings-form-actions">
          <button className="btn btn-primary" onClick={handleCreate} disabled={!newKeyword.trim() || !newProfile || !newRoot}>Create Webhook</button>
        </div>
      </>
    );
  }

  // ── List view ──
  return (
    <>
      <div className="settings-header">
        <h2>Webhooks</h2>
        <button className="settings-add-btn" onClick={() => setView('create')}>
          <span className="plus">+</span> Add
        </button>
      </div>

      {error && <div className="settings-error">{error}</div>}

      {/* Token reveal banner */}
      {revealedToken && (
        <div className="settings-token-reveal">
          <p>Token for "{revealedToken.keyword}" (copy now — shown only once):</p>
          <div className="settings-token-value">
            <code>{revealedToken.token}</code>
            <button className="settings-copy-btn" onClick={() => copyToClipboard(revealedToken.token)}>Copy</button>
          </div>
          <div className="settings-token-dismiss">
            <button className="settings-copy-btn" onClick={() => setRevealedToken(null)}>Dismiss</button>
          </div>
        </div>
      )}

      {loading ? (
        <div className="settings-empty">Loading...</div>
      ) : webhooks.length === 0 ? (
        <div className="settings-empty">No webhooks configured. Tap Add to create one.</div>
      ) : (
        <>
          <div className="settings-list">
            {webhooks.map(w => (
              <div key={w.Keyword} className="settings-item" style={{ flexDirection: 'column', alignItems: 'stretch', gap: 0 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                  <div className="settings-item-info">
                    <div className="settings-item-name">
                      {w.Keyword}
                      {!w.Enabled && <span className="settings-badge disabled">disabled</span>}
                    </div>
                    <div className="settings-item-meta">
                      <span className="settings-badge">{w.ProfileName}</span>
                      <span>{w.RootName}{w.ActionName ? ` / ${w.ActionName}` : ''}</span>
                    </div>
                    {w.Description && <div className="settings-item-desc">{w.Description}</div>}
                  </div>
                  <div className="settings-item-actions">
                    <Toggle checked={w.Enabled} onChange={() => handleToggleEnabled(w)} />
                    <button className="btn btn-sm btn-secondary" onClick={() => handleRegenerateToken(w.Keyword)}>New Token</button>
                    <RowDelete onDelete={() => handleDelete(w.Keyword)} />
                  </div>
                </div>
                <div className="settings-code-block">
                  <code>POST {baseUrl}/webhook/{w.Keyword}</code>
                  {revealedToken?.keyword === w.Keyword ? (
                    <button className="settings-copy-btn" onClick={() => copyToClipboard(`curl -X POST ${baseUrl}/webhook/${w.Keyword} -H "Authorization: Bearer ${revealedToken.token}" -H "Content-Type: application/json" -d '{}'`)}>
                      Copy curl + token
                    </button>
                  ) : (
                    <button className="settings-copy-btn" onClick={() => copyToClipboard(`curl -X POST ${baseUrl}/webhook/${w.Keyword} -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{}'`)}>
                      Copy curl
                    </button>
                  )}
                </div>
              </div>
            ))}
          </div>
          <div className="settings-count">{webhooks.length} webhook{webhooks.length !== 1 ? 's' : ''}</div>
        </>
      )}
    </>
  );
}
