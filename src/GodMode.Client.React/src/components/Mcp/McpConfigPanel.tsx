import { useState, useEffect, useCallback } from 'react';
import { useAppStore } from '../../store';
import type { McpServerConfig } from '../../signalr/types';
import './McpConfigPanel.css';

export function McpConfigPanel() {
  const setShowMcpConfig = useAppStore(s => s.setShowMcpConfig);
  const serverConnections = useAppStore(s => s.serverConnections);
  const profileFilter = useAppStore(s => s.profileFilter);

  const conn = serverConnections.find(c => c.connectionState === 'connected');
  const hub = conn?.hub;
  const profiles = conn?.profiles ?? [];
  const roots = conn?.roots ?? [];

  // If a profile is selected globally, default to it; otherwise use first profile
  const defaultProfile = profileFilter !== 'All' ? profileFilter : (profiles[0]?.Name ?? '');
  const [selectedProfile, setSelectedProfile] = useState(defaultProfile);
  // Filter profiles shown in dropdown when global filter is active
  const visibleProfiles = profileFilter !== 'All'
    ? profiles.filter(p => p.Name.toLowerCase() === profileFilter.toLowerCase())
    : profiles;

  const [servers, setServers] = useState<Record<string, McpServerConfig>>({});
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Add form state
  const [newName, setNewName] = useState('');
  const [newCommand, setNewCommand] = useState('');
  const [newArgs, setNewArgs] = useState('');
  const [newEnvPairs, setNewEnvPairs] = useState<{ key: string; value: string }[]>([]);

  // Pick any root in this profile to call getEffectiveMcpServers (profile-level servers are the same for all roots)
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

  useEffect(() => {
    loadServers();
  }, [loadServers]);

  const handleAdd = async () => {
    if (!hub || !newName.trim() || !newCommand.trim()) return;
    setError(null);
    try {
      const args = newArgs.trim() ? newArgs.split(/\s+/) : undefined;
      const env = newEnvPairs.length > 0
        ? Object.fromEntries(newEnvPairs.filter(p => p.key.trim()).map(p => [p.key, p.value]))
        : undefined;

      const config: McpServerConfig = {
        Command: newCommand.trim(),
        Args: args,
        Env: env,
      };

      await hub.addMcpServer(newName.trim(), config, 'profile', selectedProfile);
      setNewName('');
      setNewCommand('');
      setNewArgs('');
      setNewEnvPairs([]);
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

  return (
    <div className="modal-overlay" onClick={() => setShowMcpConfig(false)}>
      <div className="modal mcp-config-modal" onClick={e => e.stopPropagation()}>
        <h2>MCP Servers</h2>

        <div className="form-group">
          <label>Profile</label>
          <select value={selectedProfile} onChange={e => setSelectedProfile(e.target.value)}>
            {visibleProfiles.map(p => <option key={p.Name} value={p.Name}>{p.Name}</option>)}
          </select>
        </div>

        {error && <div className="form-error">{error}</div>}

        {loading ? (
          <div className="mcp-empty">Loading...</div>
        ) : Object.keys(servers).length === 0 ? (
          <div className="mcp-empty">No MCP servers configured for this profile</div>
        ) : (
          <div className="mcp-server-list">
            {Object.entries(servers).map(([name, config]) => (
              <div key={name} className="mcp-server-item">
                <div className="mcp-server-info">
                  <span className="mcp-server-name">{name}</span>
                  <span className="mcp-server-command">{config.Command} {config.Args?.join(' ') ?? ''}</span>
                </div>
                <button className="btn btn-danger btn-sm" onClick={() => handleRemove(name)}>Remove</button>
              </div>
            ))}
          </div>
        )}

        <div className="mcp-add-section">
          <h3>Add MCP Server</h3>
          <div className="form-group">
            <label>Name</label>
            <input type="text" value={newName} onChange={e => setNewName(e.target.value)} placeholder="e.g. github" />
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
              <div key={idx} className="mcp-env-row">
                <input type="text" value={pair.key} onChange={e => updateEnvPair(idx, 'key', e.target.value)} placeholder="Key" />
                <input type="text" value={pair.value} onChange={e => updateEnvPair(idx, 'value', e.target.value)} placeholder="Value" />
                <button className="btn btn-danger btn-sm" onClick={() => removeEnvPair(idx)}>x</button>
              </div>
            ))}
            <button className="mcp-env-add" onClick={addEnvPair}>+ Add env var</button>
          </div>

          <div className="form-actions">
            <button className="btn btn-secondary" onClick={() => setShowMcpConfig(false)}>Close</button>
            <button className="btn btn-primary" onClick={handleAdd} disabled={!newName.trim() || !newCommand.trim()}>Add Server</button>
          </div>
        </div>
      </div>
    </div>
  );
}
