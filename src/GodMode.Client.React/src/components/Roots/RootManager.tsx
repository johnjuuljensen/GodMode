import { useState, useCallback } from 'react';
import { useAppStore } from '../../store';
import type { SharedRootPreview } from '../../signalr/types';
import './RootManager.css';

const refresh = () => useAppStore.getState().refreshFirstConnected();

type Tab = 'list' | 'create' | 'import';

export function RootManager() {
  const setShowRootManager = useAppStore(s => s.setShowRootManager);
  const serverConnections = useAppStore(s => s.serverConnections);
  const profileFilter = useAppStore(s => s.profileFilter);

  const conn = serverConnections.find(c => c.connectionState === 'connected');
  const hub = conn?.hub;
  const allRoots = conn?.roots ?? [];
  const profiles = conn?.profiles ?? [];

  // Filter roots by selected profile
  const roots = profileFilter !== 'All'
    ? allRoots.filter(r => (r.ProfileName ?? 'Default').toLowerCase() === profileFilter.toLowerCase())
    : allRoots;

  const [tab, setTab] = useState<Tab>('list');
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  // Create form
  const [newRootName, setNewRootName] = useState('');
  const [newRootProfile, setNewRootProfile] = useState('');
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
      // Inject profileName into config.json if a specific profile is selected
      let configJson = newConfigJson;
      if (newRootProfile) {
        try {
          const parsed = JSON.parse(configJson);
          parsed.profileName = newRootProfile;
          configJson = JSON.stringify(parsed, null, 2);
        } catch { /* leave as-is if JSON is invalid — server will validate */ }
      }
      const files: Record<string, string> = { 'config.json': configJson };
      await hub.createRoot(newRootName.trim(), { Files: files });
      await refresh();
      setNewRootName('');
      setNewRootProfile('');
      setTab('list');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create root');
    } finally {
      setLoading(false);
    }
  };

  const handleChangeProfile = async (rootProfileName: string, rootName: string, newProfile: string) => {
    if (!hub) return;
    setError(null);
    try {
      const preview = await hub.getRootPreview(rootProfileName, rootName);
      if (!preview) return;
      // Update profileName in config.json
      const configStr = preview.Files['config.json'];
      if (configStr) {
        try {
          const parsed = JSON.parse(configStr);
          if (newProfile) {
            parsed.profileName = newProfile;
          } else {
            delete parsed.profileName;
          }
          preview.Files['config.json'] = JSON.stringify(parsed, null, 2);
        } catch { /* skip if unparseable */ }
      }
      await hub.updateRoot(rootProfileName, rootName, preview);
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to change profile');
    }
  };

  const handleDelete = async (profileName: string, rootName: string) => {
    if (!hub || !confirm(`Delete root "${rootName}"?`)) return;
    setError(null);
    try {
      await hub.deleteRoot(profileName, rootName, true);
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to delete root');
    }
  };

  const handleCopy = async (profileName: string, rootName: string) => {
    if (!hub) return;
    setError(null);
    try {
      const preview = await hub.getRootPreview(profileName, rootName);
      if (!preview) { setError('Could not read root'); return; }
      const copyName = `${rootName}-copy`;
      await hub.createRoot(copyName, preview, profileName);
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to copy root');
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
      await refresh();
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
                    </div>
                    <div className="root-item-actions">
                      <select
                        className="root-profile-select"
                        value={r.ProfileName === 'Default' ? '' : (r.ProfileName ?? '')}
                        onChange={e => handleChangeProfile(r.ProfileName ?? 'Default', r.Name, e.target.value)}
                      >
                        <option value="">All profiles</option>
                        {profiles.map(p => <option key={p.Name} value={p.Name}>{p.Name}</option>)}
                      </select>
                      <button className="btn btn-secondary btn-sm" onClick={() => handleCopy(r.ProfileName ?? 'Default', r.Name)}>Copy</button>
                      <button className="btn btn-danger btn-sm" onClick={() => handleDelete(r.ProfileName ?? 'Default', r.Name)}>Delete</button>
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
              <label>Profile</label>
              <select value={newRootProfile} onChange={e => setNewRootProfile(e.target.value)}>
                <option value="">All profiles</option>
                {profiles.map(p => <option key={p.Name} value={p.Name}>{p.Name}</option>)}
              </select>
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
