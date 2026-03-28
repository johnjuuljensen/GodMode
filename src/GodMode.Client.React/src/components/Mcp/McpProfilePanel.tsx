import { useState, useEffect, useCallback } from 'react';
import { useAppStore } from '../../store';
import type { McpServerConfig } from '../../signalr/types';
import './McpBrowser.css';
import './McpProfilePanel.css';

interface McpProfilePanelProps {
  serverIndex: number;
  profileName: string;
}

export function McpProfilePanel({ serverIndex, profileName }: McpProfilePanelProps) {
  const servers = useAppStore(s => s.servers);
  const setShowMcpBrowser = useAppStore(s => s.setShowMcpBrowser);
  const setShowMcpProfile = useAppStore(s => s.setShowMcpProfile);

  const hub = servers[serverIndex]?.hub;
  const roots = servers[serverIndex]?.roots ?? [];
  // Pick the first root from this profile for the GetEffectiveMcpServers call
  const profileRoot = roots.find(r => (r.ProfileName ?? 'Default') === profileName);

  const [mcpServers, setMcpServers] = useState<Record<string, McpServerConfig>>({});
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Manual add state
  const [showManualAdd, setShowManualAdd] = useState(false);
  const [manualName, setManualName] = useState('');
  const [manualCommand, setManualCommand] = useState('');
  const [manualArgs, setManualArgs] = useState('');
  const [manualUrl, setManualUrl] = useState('');
  const [manualEnv, setManualEnv] = useState('');
  const [addMode, setAddMode] = useState<'stdio' | 'http'>('stdio');

  const loadServers = useCallback(async () => {
    if (!hub || !profileRoot) {
      setMcpServers({});
      setLoading(false);
      return;
    }
    setLoading(true);
    try {
      const result = await hub.getEffectiveMcpServers(profileName, profileRoot.Name);
      setMcpServers(result);
    } catch {
      setMcpServers({});
    } finally {
      setLoading(false);
    }
  }, [hub, profileName, profileRoot]);

  useEffect(() => { loadServers(); }, [loadServers]);

  const handleRemove = async (name: string) => {
    if (!hub) return;
    try {
      await hub.removeMcpServer(name, 'profile', profileName);
      await loadServers();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to remove');
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

      // Parse env as KEY=VALUE lines
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
      await loadServers();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to add');
    }
  };

  const close = () => setShowMcpProfile(false);

  return (
    <div className="modal-overlay" onClick={close}>
      <div className="modal mcp-profile-modal" onClick={e => e.stopPropagation()}>
        <h2>MCP Servers — {profileName}</h2>
        <p className="mcp-profile-subtitle">
          These servers are available to all projects in this profile.
        </p>

        {/* Current servers */}
        {loading ? (
          <div className="mcp-loading">Loading...</div>
        ) : Object.keys(mcpServers).length === 0 ? (
          <div className="mcp-empty">No MCP servers configured for this profile.</div>
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
                <button className="mcp-remove-btn" title="Remove" onClick={() => handleRemove(name)}>
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

        {/* Action buttons */}
        {!showManualAdd && (
          <div className="btn-group mcp-profile-actions">
            <button
              className="btn btn-primary"
              onClick={() => {
                if (!profileRoot) return;
                setShowMcpBrowser(true, {
                  serverIndex,
                  profileName,
                  rootName: profileRoot.Name,
                });
                close();
              }}
              disabled={!profileRoot}
            >
              Browse Registry
            </button>
            <button className="btn btn-secondary" onClick={() => setShowManualAdd(true)}>
              Add Manually
            </button>
            <button className="btn btn-secondary" onClick={close}>
              Close
            </button>
          </div>
        )}
      </div>
    </div>
  );
}
