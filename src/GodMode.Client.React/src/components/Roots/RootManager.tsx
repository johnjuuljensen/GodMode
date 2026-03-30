import { useState, useCallback } from 'react';
import { useAppStore } from '../../store';
import type { SharedRootPreview } from '../../signalr/types';
import './RootManager.css';

type Tab = 'list' | 'create' | 'import';

export function RootManager() {
  const setShowRootManager = useAppStore(s => s.setShowRootManager);
  const serverConnections = useAppStore(s => s.serverConnections);

  const conn = serverConnections.find(c => c.connectionState === 'connected');
  const hub = conn?.hub;
  const roots = conn?.roots ?? [];
  const profiles = conn?.profiles ?? [];

  const [tab, setTab] = useState<Tab>('list');
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  // Create form
  const [newRootName, setNewRootName] = useState('');
  const [newConfigJson, setNewConfigJson] = useState('{\n  "description": "",\n  "nameTemplate": "{name}",\n  "promptTemplate": "{prompt}"\n}');

  // Import form
  const [gitUrl, setGitUrl] = useState('');
  const [gitPath, setGitPath] = useState('');
  const [gitRef, setGitRef] = useState('');
  const [importPreview, setImportPreview] = useState<SharedRootPreview | null>(null);
  const [importRootName, setImportRootName] = useState('');

  const handleCreate = async () => {
    if (!hub || !newRootName.trim()) return;
    setError(null);
    setLoading(true);
    try {
      const files: Record<string, string> = { 'config.json': newConfigJson };
      await hub.createRoot(newRootName.trim(), { Files: files });
      setNewRootName('');
      setTab('list');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create root');
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = async (profileName: string, rootName: string) => {
    if (!hub || !confirm(`Delete root "${rootName}"?`)) return;
    setError(null);
    try {
      await hub.deleteRoot(profileName, rootName);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to delete root');
    }
  };

  const handlePreviewGit = useCallback(async () => {
    if (!hub || !gitUrl.trim()) return;
    setError(null);
    setLoading(true);
    try {
      const preview = await hub.previewImportFromGit(
        gitUrl.trim(),
        gitPath.trim() || null,
        gitRef.trim() || null,
      );
      setImportPreview(preview);
      setImportRootName(preview.Manifest.Name);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to preview import');
    } finally {
      setLoading(false);
    }
  }, [hub, gitUrl, gitPath, gitRef]);

  const handleInstall = async () => {
    if (!hub || !importPreview || !importRootName.trim()) return;
    setError(null);
    setLoading(true);
    try {
      await hub.installSharedRoot(importRootName.trim(), importPreview);
      setImportPreview(null);
      setGitUrl('');
      setGitPath('');
      setGitRef('');
      setTab('list');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to install root');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="modal-overlay" onClick={() => setShowRootManager(false)}>
      <div className="modal root-manager-modal" onClick={e => e.stopPropagation()}>
        <h2>Root Manager</h2>

        <div className="root-tabs">
          <button className={`root-tab ${tab === 'list' ? 'active' : ''}`} onClick={() => setTab('list')}>Roots</button>
          <button className={`root-tab ${tab === 'create' ? 'active' : ''}`} onClick={() => setTab('create')}>Create</button>
          <button className={`root-tab ${tab === 'import' ? 'active' : ''}`} onClick={() => setTab('import')}>Import</button>
        </div>

        {error && <div className="form-error">{error}</div>}

        {tab === 'list' && (
          <>
            {roots.length === 0 ? (
              <div className="root-empty">No roots configured</div>
            ) : (
              <div className="root-list">
                {roots.map(r => (
                  <div key={`${r.ProfileName}/${r.Name}`} className="root-item">
                    <div className="root-item-info">
                      <span className="root-item-name">{r.Name}</span>
                      {r.Description && <span className="root-item-desc">{r.Description}</span>}
                      {r.ProfileName && <span className="root-item-desc">Profile: {r.ProfileName}</span>}
                    </div>
                    <div className="root-item-actions">
                      <button className="btn btn-danger btn-sm" onClick={() => handleDelete(r.ProfileName ?? profiles[0]?.Name ?? '', r.Name)}>Delete</button>
                    </div>
                  </div>
                ))}
              </div>
            )}
            <div className="form-actions">
              <button className="btn btn-secondary" onClick={() => setShowRootManager(false)}>Close</button>
            </div>
          </>
        )}

        {tab === 'create' && (
          <div className="root-create-section">
            <div className="form-group">
              <label>Root Name</label>
              <input type="text" value={newRootName} onChange={e => setNewRootName(e.target.value)} placeholder="my-root" />
            </div>
            <div className="form-group">
              <label>config.json</label>
              <textarea
                className="form-textarea"
                rows={8}
                value={newConfigJson}
                onChange={e => setNewConfigJson(e.target.value)}
              />
            </div>
            <div className="form-actions">
              <button className="btn btn-secondary" onClick={() => setTab('list')}>Back</button>
              <button className="btn btn-primary" onClick={handleCreate} disabled={loading || !newRootName.trim()}>
                {loading ? 'Creating...' : 'Create Root'}
              </button>
            </div>
          </div>
        )}

        {tab === 'import' && (
          <div className="root-import-section">
            <h3>Import from Git</h3>
            <div className="form-group">
              <label>Git URL</label>
              <input type="text" value={gitUrl} onChange={e => setGitUrl(e.target.value)} placeholder="https://github.com/org/repo.git" />
            </div>
            <div className="form-group">
              <label>Path (optional)</label>
              <input type="text" value={gitPath} onChange={e => setGitPath(e.target.value)} placeholder="subdirectory/path" />
            </div>
            <div className="form-group">
              <label>Ref (optional)</label>
              <input type="text" value={gitRef} onChange={e => setGitRef(e.target.value)} placeholder="main, v1.0, etc." />
            </div>

            {importPreview && (
              <div className="form-group">
                <label>Install as</label>
                <input type="text" value={importRootName} onChange={e => setImportRootName(e.target.value)} />
                <div className="form-description">
                  {Object.keys(importPreview.Preview.Files).length} files: {Object.keys(importPreview.Preview.Files).join(', ')}
                </div>
              </div>
            )}

            <div className="form-actions">
              <button className="btn btn-secondary" onClick={() => setTab('list')}>Back</button>
              {!importPreview ? (
                <button className="btn btn-primary" onClick={handlePreviewGit} disabled={loading || !gitUrl.trim()}>
                  {loading ? 'Loading...' : 'Preview'}
                </button>
              ) : (
                <button className="btn btn-primary" onClick={handleInstall} disabled={loading || !importRootName.trim()}>
                  {loading ? 'Installing...' : 'Install'}
                </button>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
