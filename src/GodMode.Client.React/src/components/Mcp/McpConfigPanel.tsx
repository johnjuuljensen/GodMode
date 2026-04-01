import { useState, useEffect, useCallback } from 'react';
import { useAppStore } from '../../store';
import type { McpServerConfig } from '../../signalr/types';
import { RowDelete } from '../settings-shared';
import '../settings-common.css';

const BackArrow = () => (
  <svg width={16} height={16} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round"><path d="M10 4L6 8l4 4"/></svg>
);

type View = 'list' | 'add';

export function McpConfigPanel() {
  const serverConnections = useAppStore(s => s.serverConnections);
  const profileFilter = useAppStore(s => s.profileFilter);

  const conn = serverConnections.find(c => c.connectionState === 'connected');
  const hub = conn?.hub;
  const profiles = conn?.profiles ?? [];
  const roots = conn?.roots ?? [];

  const defaultProfile = profileFilter !== 'All' ? profileFilter : (profiles[0]?.Name ?? '');
  const [selectedProfile, setSelectedProfile] = useState(defaultProfile);
  const visibleProfiles = profileFilter !== 'All'
    ? profiles.filter(p => p.Name.toLowerCase() === profileFilter.toLowerCase())
    : profiles;
  const [servers, setServers] = useState<Record<string, McpServerConfig>>({});
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<View>('list');

  const [newName, setNewName] = useState('');
  const [newCommand, setNewCommand] = useState('');
  const [newArgs, setNewArgs] = useState('');
  const [newEnvPairs, setNewEnvPairs] = useState<{ key: string; value: string }[]>([]);

  const anyRoot = roots.find(r => r.ProfileName === selectedProfile || r.ProfileName === 'Default')?.Name;

  const loadServers = useCallback(async () => {
    if (!hub || !selectedProfile || !anyRoot) return;
    setLoading(true);
    setError(null);
    try {
      const result = await hub.getEffectiveMcpServers(selectedProfile, anyRoot);
      setServers(result);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load MCP servers');
    } finally {
      setLoading(false);
    }
  }, [hub, selectedProfile, anyRoot]);

  useEffect(() => { loadServers(); }, [loadServers]);

  const goList = () => { setView('list'); setError(null); };

  const handleAdd = async () => {
    if (!hub || !newName.trim() || !newCommand.trim()) return;
    setError(null);
    try {
      const args = newArgs.trim() ? newArgs.split(/\s+/) : undefined;
      const env = newEnvPairs.length > 0
        ? Object.fromEntries(newEnvPairs.filter(p => p.key.trim()).map(p => [p.key, p.value]))
        : undefined;
      const config: McpServerConfig = { Command: newCommand.trim(), Args: args, Env: env };
      await hub.addMcpServer(newName.trim(), config, 'profile', selectedProfile);
      setNewName('');
      setNewCommand('');
      setNewArgs('');
      setNewEnvPairs([]);
      goList();
      await loadServers();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to add MCP server');
    }
  };

  const handleRemove = async (name: string) => {
    if (!hub) return;
    setError(null);
    try {
      await hub.removeMcpServer(name, 'profile', selectedProfile);
      await loadServers();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to remove MCP server');
    }
  };

  const addEnvPair = () => setNewEnvPairs([...newEnvPairs, { key: '', value: '' }]);
  const updateEnvPair = (idx: number, field: 'key' | 'value', val: string) => {
    const updated = [...newEnvPairs];
    updated[idx] = { ...updated[idx], [field]: val };
    setNewEnvPairs(updated);
  };
  const removeEnvPair = (idx: number) => setNewEnvPairs(newEnvPairs.filter((_, i) => i !== idx));

  const serverEntries = Object.entries(servers);

  // ── Add view ──
  if (view === 'add') {
    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={goList}><BackArrow /> Connectors</button>
        </div>
        <div className="settings-header"><h2>New MCP Server</h2></div>
        {error && <div className="settings-error">{error}</div>}
        <div className="form-group">
          <label>Name</label>
          <input type="text" value={newName} onChange={e => setNewName(e.target.value)} placeholder="e.g. github" autoFocus />
        </div>
        <div className="form-group">
          <label>Command</label>
          <input type="text" value={newCommand} onChange={e => setNewCommand(e.target.value)} placeholder="e.g. npx" />
        </div>
        <div className="form-group">
          <label>Args (space-separated)</label>
          <input type="text" value={newArgs} onChange={e => setNewArgs(e.target.value)} placeholder="e.g. -y @modelcontextprotocol/server-github" />
        </div>
        <div className="form-group">
          <label>Environment Variables</label>
          {newEnvPairs.map((pair, idx) => (
            <div key={idx} className="settings-kv-row">
              <input type="text" value={pair.key} onChange={e => updateEnvPair(idx, 'key', e.target.value)} placeholder="Key" />
              <input type="text" value={pair.value} onChange={e => updateEnvPair(idx, 'value', e.target.value)} placeholder="Value" />
              <button className="btn btn-danger btn-sm" onClick={() => removeEnvPair(idx)}>x</button>
            </div>
          ))}
          <button className="settings-kv-add" onClick={addEnvPair}>+ Add env var</button>
        </div>
        <div className="settings-form-actions">
          <button className="btn btn-primary" onClick={handleAdd} disabled={!newName.trim() || !newCommand.trim()}>Add Server</button>
        </div>
      </>
    );
  }

  // ── List view ──
  return (
    <>
      <div className="settings-header">
        <h2>Connectors (MCP)</h2>
        <button className="settings-add-btn" onClick={() => setView('add')}>
          <span className="plus">+</span> Add
        </button>
      </div>

      <div className="form-group">
        <label>Profile</label>
        <select value={selectedProfile} onChange={e => setSelectedProfile(e.target.value)}>
          {visibleProfiles.map(p => <option key={p.Name} value={p.Name}>{p.Name}</option>)}
        </select>
      </div>

      {error && <div className="settings-error">{error}</div>}

      {loading ? (
        <div className="settings-empty">Loading...</div>
      ) : serverEntries.length === 0 ? (
        <div className="settings-empty">No MCP servers configured for this profile</div>
      ) : (
        <>
          <div className="settings-list">
            {serverEntries.map(([name, config]) => (
              <div key={name} className="settings-item">
                <div className="settings-item-info">
                  <div className="settings-item-name">{name}</div>
                  <div className="settings-item-meta">
                    <span className="settings-badge">{config.Command}</span>
                    {config.Args && <span className="truncate">{config.Args.join(' ')}</span>}
                  </div>
                </div>
                <div className="settings-item-actions">
                  <RowDelete onDelete={() => handleRemove(name)} />
                </div>
              </div>
            ))}
          </div>
          <div className="settings-count">{serverEntries.length} server{serverEntries.length !== 1 ? 's' : ''}</div>
        </>
      )}
    </>
  );
}
