import { useState, useEffect, useCallback } from 'react';
import { useAppStore } from '../../store';
import type { McpServerConfig } from '../../signalr/types';
import '../Mcp/McpBrowser.css';
import '../Mcp/McpProfilePanel.css';
import './ProfileSettings.css';

interface ProfileSettingsProps {
  serverId: string;
  profileName: string;
}

export function ProfileSettings({ serverId, profileName }: ProfileSettingsProps) {
  const servers = useAppStore(s => s.servers);
  const setShowProfileSettings = useAppStore(s => s.setShowProfileSettings);
  const setShowMcpBrowser = useAppStore(s => s.setShowMcpBrowser);

  const server = servers.find(s => s.registration.url === serverId);
  const hub = server?.hub;
  const roots = server?.roots ?? [];
  const profileRoot = roots.find(r => (r.ProfileName ?? 'Default') === profileName);
  const rootName = profileRoot?.Name ?? '';

  // MCP servers state
  const [mcpServers, setMcpServers] = useState<Record<string, McpServerConfig>>({});
  const [mcpLoading, setMcpLoading] = useState(true);

  const [error, setError] = useState<string | null>(null);

  // Manual MCP add state
  const [showManualAdd, setShowManualAdd] = useState(false);
  const [manualName, setManualName] = useState('');
  const [manualCommand, setManualCommand] = useState('');
  const [manualArgs, setManualArgs] = useState('');
  const [manualUrl, setManualUrl] = useState('');
  const [manualEnv, setManualEnv] = useState('');
  const [addMode, setAddMode] = useState<'stdio' | 'http'>('stdio');

  const loadMcpServers = useCallback(async () => {
    if (!hub) {
      setMcpServers({});
      setMcpLoading(false);
      return;
    }
    setMcpLoading(true);
    try {
      const result = await hub.getEffectiveMcpServers(profileName, rootName);
      setMcpServers(result);
    } catch {
      setMcpServers({});
    } finally {
      setMcpLoading(false);
    }
  }, [hub, profileName, rootName]);

  useEffect(() => { loadMcpServers(); }, [loadMcpServers]);

  const handleRemoveMcp = async (name: string) => {
    if (!hub) return;
    setError(null);
    try {
      await hub.removeMcpServer(name, 'profile', profileName);
      await loadMcpServers();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to remove: ' + (err instanceof Error ? err.message : String(err)));
    }
  };

  const handleManualAdd = async () => {
    if (!hub || !manualName.trim()) return;
    setError(null);
    try {
      let config: McpServerConfig;
      if (addMode === 'http') {
        config = { url: manualUrl.trim() || undefined };
      } else {
        const args = manualArgs.trim() ? manualArgs.split(/\s+/) : undefined;
        config = { command: manualCommand.trim() || undefined, args };
      }
      if (manualEnv.trim()) {
        const env: Record<string, string> = {};
        for (const line of manualEnv.split('\n')) {
          const eq = line.indexOf('=');
          if (eq > 0) {
            env[line.slice(0, eq).trim()] = line.slice(eq + 1).trim();
          }
        }
        if (Object.keys(env).length > 0) config.env = env;
      }
      await hub.addMcpServer(manualName.trim(), config, 'profile', profileName);
      setManualName('');
      setManualCommand('');
      setManualArgs('');
      setManualUrl('');
      setManualEnv('');
      setShowManualAdd(false);
      await loadMcpServers();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to add');
    }
  };

  const close = () => setShowProfileSettings(false);

  return (
    <div className="modal-overlay" onClick={close}>
      <div className="modal profile-settings-modal" onClick={e => e.stopPropagation()}>
        <h2>Profile Settings — {profileName}</h2>

        {/* MCP Servers */}
        <div className="profile-settings-section">
          <h3>MCP Servers</h3>
          <p className="mcp-profile-subtitle">
            These servers are available to all projects in this profile.
          </p>

          {mcpLoading ? (
            <div className="mcp-loading">Loading...</div>
          ) : Object.keys(mcpServers).length === 0 ? (
            <div className="mcp-empty">No MCP servers configured.</div>
          ) : (
            <div className="mcp-server-list mcp-profile-list">
              {Object.entries(mcpServers).map(([name, config]) => (
                <div key={name} className="mcp-server-entry">
                  <div className="mcp-server-entry-info">
                    <span className="mcp-server-entry-name">{name}</span>
                    <span className="mcp-type-badge">{config.url ? 'remote' : 'stdio'}</span>
                    <span className="mcp-server-entry-detail">
                      {config.url ?? config.command ?? ''}
                      {config.args ? ' ' + config.args.join(' ') : ''}
                    </span>
                  </div>
                  <button className="mcp-remove-btn" title="Remove" onClick={() => handleRemoveMcp(name)}>
                    &#x2715;
                  </button>
                </div>
              ))}
            </div>
          )}

          {error && <div className="form-error">{error}</div>}

          {/* Manual add form */}
          {showManualAdd && (
            <div className="mcp-manual-add">
              <h4>Add MCP Server</h4>
              <div className="form-group">
                <label>Server Name</label>
                <input
                  type="text"
                  value={manualName}
                  onChange={e => setManualName(e.target.value)}
                  placeholder="e.g. github, filesystem"
                />
              </div>
              <div className="form-group">
                <label>Connection Type</label>
                <select value={addMode} onChange={e => setAddMode(e.target.value as 'stdio' | 'http')}>
                  <option value="stdio">stdio (command + args)</option>
                  <option value="http">HTTP / Streamable</option>
                </select>
              </div>
              {addMode === 'stdio' ? (
                <>
                  <div className="form-group">
                    <label>Command</label>
                    <input
                      type="text"
                      value={manualCommand}
                      onChange={e => setManualCommand(e.target.value)}
                      placeholder="e.g. npx, node, python"
                    />
                  </div>
                  <div className="form-group">
                    <label>Arguments <span className="form-description">(space-separated)</span></label>
                    <input
                      type="text"
                      value={manualArgs}
                      onChange={e => setManualArgs(e.target.value)}
                      placeholder="e.g. -y @modelcontextprotocol/server-filesystem /data"
                    />
                  </div>
                </>
              ) : (
                <div className="form-group">
                  <label>URL</label>
                  <input
                    type="text"
                    value={manualUrl}
                    onChange={e => setManualUrl(e.target.value)}
                    placeholder="https://..."
                  />
                </div>
              )}
              <div className="form-group">
                <label>Environment Variables <span className="form-description">(KEY=VALUE, one per line)</span></label>
                <textarea
                  className="form-textarea"
                  value={manualEnv}
                  onChange={e => setManualEnv(e.target.value)}
                  rows={3}
                  placeholder={"GITHUB_TOKEN=ghp_...\nAPI_KEY=sk-..."}
                />
              </div>
              <div className="btn-group">
                <button className="btn btn-primary" onClick={handleManualAdd} disabled={!manualName.trim()}>
                  Add
                </button>
                <button className="btn btn-secondary" onClick={() => setShowManualAdd(false)}>
                  Cancel
                </button>
              </div>
            </div>
          )}

          {!showManualAdd && (
            <div className="btn-group mcp-profile-actions">
              <button
                className="btn btn-primary"
                onClick={() => {
                  setShowMcpBrowser(true, {
                    serverId,
                    profileName,
                    rootName,
                  });
                  close();
                }}
              >
                Browse Registry
              </button>
              <button className="btn btn-secondary" onClick={() => setShowManualAdd(true)}>
                Add Manually
              </button>
            </div>
          )}
        </div>

        {/* Close button */}
        <div className="btn-group" style={{ justifyContent: 'flex-end' }}>
          <button className="btn btn-secondary" onClick={close}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
}
