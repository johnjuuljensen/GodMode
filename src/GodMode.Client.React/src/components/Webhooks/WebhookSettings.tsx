import { useState, useEffect, useCallback } from 'react';
import { useAppStore } from '../../store';
import type { WebhookInfo } from '../../signalr/types';
import './WebhookSettings.css';

export function WebhookSettings() {
  const setShowWebhookSettings = useAppStore(s => s.setShowWebhookSettings);
  const serverConnections = useAppStore(s => s.serverConnections);

  const conn = serverConnections.find(c => c.connectionState === 'connected');
  const hub = conn?.hub;
  const profiles = conn?.profiles ?? [];
  const roots = conn?.roots ?? [];

  const [webhooks, setWebhooks] = useState<WebhookInfo[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Create form state
  const [newKeyword, setNewKeyword] = useState('');
  const [newProfile, setNewProfile] = useState(profiles[0]?.Name ?? '');
  const [newRoot, setNewRoot] = useState('');
  const [newAction, setNewAction] = useState('');
  const [newDescription, setNewDescription] = useState('');
  const [mappingPairs, setMappingPairs] = useState<{ input: string; path: string }[]>([]);

  // Token reveal (shown once after create or regenerate)
  const [revealedToken, setRevealedToken] = useState<{ keyword: string; token: string } | null>(null);

  // Delete confirmation
  const [deleteConfirm, setDeleteConfirm] = useState<string | null>(null);

  // Filtered roots for selected profile (only roots that belong to this profile)
  const filteredRoots = roots.filter(r => r.ProfileName === newProfile);

  // Actions for selected root (must match both name and profile)
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

  useEffect(() => {
    loadWebhooks();
  }, [loadWebhooks]);

  // Update root selection when profile changes, reset action
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

  const handleCreate = async () => {
    if (!hub || !newKeyword.trim() || !newProfile || !newRoot) return;
    setError(null);
    try {
      const inputMapping = mappingPairs.length > 0
        ? Object.fromEntries(mappingPairs.filter(p => p.input.trim() && p.path.trim()).map(p => [p.input, p.path]))
        : null;

      await hub.createWebhook(
        newKeyword.trim(),
        newProfile,
        newRoot,
        newAction || null,
        newDescription.trim() || null,
        inputMapping,
      );

      // Read the token by listing (it's redacted in list, so we need the full one from create)
      // Actually the hub returns WebhookInfo with redacted token.
      // We need to get the full token from the file manager. For now, show the prefix.
      // The GodMode Chat create_webhook tool shows the full token.
      // For the UI, we regenerate to get the full token displayed.
      const fullToken = await hub.regenerateWebhookToken(newKeyword.trim());
      setRevealedToken({ keyword: newKeyword.trim(), token: fullToken });

      setNewKeyword('');
      setNewDescription('');
      setMappingPairs([]);
      await loadWebhooks();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create webhook');
    }
  };

  const handleDelete = async (keyword: string) => {
    if (!hub) return;
    setError(null);
    setDeleteConfirm(null);
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

  // Build the webhook URL for display
  const baseUrl = window.location.origin;

  return (
    <div className="modal-overlay" onClick={() => setShowWebhookSettings(false)}>
      <div className="modal webhook-settings-modal" onClick={e => e.stopPropagation()}>
        <h2>Webhooks</h2>

        {error && <div className="form-error">{error}</div>}

        {/* Token reveal banner */}
        {revealedToken && (
          <div className="webhook-token-reveal">
            <p>Token for "{revealedToken.keyword}" (copy now — shown only once):</p>
            <div className="webhook-token-value">
              <code>{revealedToken.token}</code>
              <button className="webhook-copy-btn" onClick={() => copyToClipboard(revealedToken.token)}>Copy</button>
            </div>
            <div className="webhook-token-dismiss">
              <button className="webhook-copy-btn" onClick={() => setRevealedToken(null)}>Dismiss</button>
            </div>
          </div>
        )}

        {/* Webhook list */}
        {loading ? (
          <div className="webhook-empty">Loading...</div>
        ) : webhooks.length === 0 ? (
          <div className="webhook-empty">No webhooks configured</div>
        ) : (
          <div className="webhook-list">
            {webhooks.map(w => (
              <div key={w.Keyword} className="webhook-item">
                <div className="webhook-item-header">
                  <div className="webhook-item-info">
                    <div className="webhook-item-keyword">
                      {w.Keyword}
                      {!w.Enabled && <span className="webhook-item-disabled">disabled</span>}
                    </div>
                    <div className="webhook-item-target">
                      {w.ProfileName} / {w.RootName}{w.ActionName ? ` / ${w.ActionName}` : ''}
                    </div>
                    {w.Description && <div className="webhook-item-desc">{w.Description}</div>}
                  </div>
                  <div className="webhook-item-actions">
                    <button
                      className="btn btn-sm btn-secondary"
                      onClick={() => handleToggleEnabled(w)}
                      title={w.Enabled ? 'Disable' : 'Enable'}
                    >
                      {w.Enabled ? 'Disable' : 'Enable'}
                    </button>
                    <button
                      className="btn btn-sm btn-secondary"
                      onClick={() => handleRegenerateToken(w.Keyword)}
                      title="Regenerate token"
                    >
                      New Token
                    </button>
                    {deleteConfirm === w.Keyword ? (
                      <>
                        <button className="btn btn-danger btn-sm" onClick={() => handleDelete(w.Keyword)}>Confirm</button>
                        <button className="btn btn-sm" onClick={() => setDeleteConfirm(null)}>Cancel</button>
                      </>
                    ) : (
                      <button className="btn btn-danger btn-sm" onClick={() => setDeleteConfirm(w.Keyword)}>Delete</button>
                    )}
                  </div>
                </div>
                <div className="webhook-item-endpoint">
                  <code>POST {baseUrl}/webhook/{w.Keyword}</code>
                  {revealedToken?.keyword === w.Keyword ? (
                    <button
                      className="webhook-copy-btn"
                      onClick={() => copyToClipboard(`curl -X POST ${baseUrl}/webhook/${w.Keyword} -H "Authorization: Bearer ${revealedToken.token}" -H "Content-Type: application/json" -d '{}'`)}
                    >
                      Copy curl + token
                    </button>
                  ) : (
                    <button
                      className="webhook-copy-btn"
                      onClick={() => copyToClipboard(`curl -X POST ${baseUrl}/webhook/${w.Keyword} -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{}'`)}
                    >
                      Copy curl
                    </button>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}

        {/* Create form */}
        <div className="webhook-add-section">
          <h3>Create Webhook</h3>
          <div className="form-group">
            <label>Keyword (URL slug)</label>
            <input
              type="text"
              value={newKeyword}
              onChange={e => setNewKeyword(e.target.value.replace(/[^a-zA-Z0-9_-]/g, ''))}
              placeholder="e.g. deploy, review, triage"
            />
          </div>

          <div className="webhook-form-row">
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
            <input
              type="text"
              value={newDescription}
              onChange={e => setNewDescription(e.target.value)}
              placeholder="e.g. Triggered by GitHub issue webhooks"
            />
          </div>

          <details className="webhook-mapping-section">
            <summary>Input Mapping (optional — maps payload fields to root inputs)</summary>
            <div className="webhook-mapping-rows">
              {mappingPairs.map((pair, idx) => (
                <div key={idx} className="webhook-mapping-row">
                  <input
                    type="text"
                    value={pair.input}
                    onChange={e => updateMappingPair(idx, 'input', e.target.value)}
                    placeholder="Input key (e.g. name)"
                  />
                  <span className="mapping-arrow">&larr;</span>
                  <input
                    type="text"
                    value={pair.path}
                    onChange={e => updateMappingPair(idx, 'path', e.target.value)}
                    placeholder="JSON path (e.g. $.title)"
                  />
                  <button className="btn btn-danger btn-sm" onClick={() => removeMappingPair(idx)}>x</button>
                </div>
              ))}
              <button className="webhook-mapping-add" onClick={addMappingPair}>+ Add mapping</button>
            </div>
          </details>

          <div className="form-actions">
            <button className="btn btn-secondary" onClick={() => setShowWebhookSettings(false)}>Close</button>
            <button
              className="btn btn-primary"
              onClick={handleCreate}
              disabled={!newKeyword.trim() || !newProfile || !newRoot}
            >
              Create Webhook
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
