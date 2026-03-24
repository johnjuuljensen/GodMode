import { useState, useEffect, useCallback, useMemo } from 'react';
import { useAppStore } from '../../store';
import type { McpRegistryServer, McpServerDetail, McpServerConfig } from '../../signalr/types';
import './McpBrowser.css';

interface ConfigField {
  key: string;
  title: string;
  description?: string;
  required: boolean;
}

function parseConfigSchema(schema: unknown): ConfigField[] {
  if (!schema || typeof schema !== 'object') return [];
  const s = schema as Record<string, unknown>;
  const properties = s.properties as Record<string, Record<string, unknown>> | undefined;
  if (!properties) return [];

  const required = new Set<string>(Array.isArray(s.required) ? s.required as string[] : []);
  const fields: ConfigField[] = [];

  for (const [key, prop] of Object.entries(properties)) {
    fields.push({
      key,
      title: (prop.title as string) || key,
      description: prop.description as string | undefined,
      required: required.has(key),
    });
  }
  return fields;
}

export function McpBrowser() {
  const servers = useAppStore(s => s.servers);
  const context = useAppStore(s => s.mcpBrowserContext);
  const setShowMcpBrowser = useAppStore(s => s.setShowMcpBrowser);

  const [searchQuery, setSearchQuery] = useState('');
  const [searchResults, setSearchResults] = useState<McpRegistryServer[]>([]);
  const [isSearching, setIsSearching] = useState(false);
  const [selectedServer, setSelectedServer] = useState<McpRegistryServer | null>(null);
  const [serverDetail, setServerDetail] = useState<McpServerDetail | null>(null);
  const [isLoadingDetail, setIsLoadingDetail] = useState(false);
  const [configValues, setConfigValues] = useState<Record<string, string>>({});
  const [targetLevel, setTargetLevel] = useState('profile');
  const [error, setError] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);

  const hub = context ? servers[context.serverIndex]?.hub : null;

  // Config fields from the selected server's connection schema
  const configFields = useMemo(() => {
    const connection = serverDetail?.Connections?.[0];
    return connection?.configSchema ? parseConfigSchema(connection.configSchema) : [];
  }, [serverDetail]);

  // Debounced search
  useEffect(() => {
    if (!searchQuery.trim() || !hub) {
      setSearchResults([]);
      return;
    }

    const timer = setTimeout(async () => {
      setIsSearching(true);
      setError(null);
      try {
        const result = await hub.searchMcpServers(searchQuery.trim());
        setSearchResults(result.Servers);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Search failed');
        setSearchResults([]);
      } finally {
        setIsSearching(false);
      }
    }, 300);

    return () => clearTimeout(timer);
  }, [searchQuery, hub]);

  // Load detail when a server is selected
  const handleSelectServer = useCallback(async (server: McpRegistryServer) => {
    if (!hub) return;
    setSelectedServer(server);
    setIsLoadingDetail(true);
    setServerDetail(null);
    setConfigValues({});
    setError(null);

    try {
      const detail = await hub.getMcpServerDetail(server.QualifiedName);
      setServerDetail(detail);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load server detail');
    } finally {
      setIsLoadingDetail(false);
    }
  }, [hub]);

  const handleAdd = async () => {
    if (!hub || !context || !serverDetail) return;

    setAdding(true);
    setError(null);

    try {
      const connection = serverDetail.Connections?.[0];
      const env = Object.fromEntries(
        configFields
          .filter(f => configValues[f.key]?.trim())
          .map(f => [f.key, configValues[f.key].trim()])
      );

      const config: McpServerConfig = connection?.Type === 'http'
        ? { Url: connection.DeploymentUrl, Env: Object.keys(env).length > 0 ? env : undefined }
        : {
            Command: 'npx',
            Args: ['-y', '@smithery/cli@latest', 'run', serverDetail.QualifiedName],
            Env: Object.keys(env).length > 0 ? env : undefined,
          };

      await hub.addMcpServer(
        serverDetail.QualifiedName,
        config,
        targetLevel,
        context.profileName,
        context.rootName,
        context.actionName,
      );

      setShowMcpBrowser(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to add server');
    } finally {
      setAdding(false);
    }
  };

  return (
    <div className="modal-overlay" onClick={() => !adding && setShowMcpBrowser(false)}>
      <div className="modal mcp-browser-modal" onClick={e => e.stopPropagation()}>
        <h2>Browse MCP Servers</h2>

        {/* Search */}
        <div className="mcp-search-box">
          <input
            type="text"
            placeholder="Search MCP servers..."
            value={searchQuery}
            onChange={e => setSearchQuery(e.target.value)}
            autoFocus
          />
        </div>

        {/* Results */}
        {isSearching && <div className="mcp-loading">Searching...</div>}

        {!isSearching && searchResults.length > 0 && (
          <div className="mcp-results">
            {searchResults.map(server => (
              <div
                key={server.QualifiedName}
                className={`mcp-server-item ${selectedServer?.QualifiedName === server.QualifiedName ? 'selected' : ''}`}
                onClick={() => handleSelectServer(server)}
              >
                {server.IconUrl ? (
                  <img className="mcp-server-icon" src={server.IconUrl} alt="" />
                ) : (
                  <div className="mcp-server-icon-placeholder">MCP</div>
                )}
                <div className="mcp-server-info">
                  <div className="mcp-server-name">
                    {server.DisplayName}
                    {server.Verified && <span className="mcp-verified-badge" title="Verified">&#10003;</span>}
                  </div>
                  {server.Description && (
                    <div className="mcp-server-desc">{server.Description}</div>
                  )}
                </div>
                <div className="mcp-server-meta">
                  {server.UseCount > 0 && `${server.UseCount.toLocaleString()} uses`}
                </div>
              </div>
            ))}
          </div>
        )}

        {!isSearching && searchQuery && searchResults.length === 0 && (
          <div className="mcp-empty">No servers found</div>
        )}

        {/* Detail panel */}
        {isLoadingDetail && <div className="mcp-loading">Loading details...</div>}

        {serverDetail && (
          <div className="mcp-detail-panel">
            <h3>{serverDetail.DisplayName}</h3>
            {serverDetail.Description && (
              <div className="mcp-detail-desc">{serverDetail.Description}</div>
            )}

            {/* Tools */}
            {serverDetail.Tools && serverDetail.Tools.length > 0 && (
              <div className="mcp-tools-list">
                {serverDetail.Tools.map(tool => (
                  <span key={tool.Name} className="mcp-tool-badge" title={tool.Description ?? ''}>
                    {tool.Name}
                  </span>
                ))}
              </div>
            )}

            {/* Config fields from schema */}
            {configFields.length > 0 && (
              <div className="mcp-config-section">
                <h4>Configuration</h4>
                {configFields.map(field => (
                  <div className="form-group" key={field.key}>
                    <label>
                      {field.title}
                      {field.required && <span className="form-required">*</span>}
                    </label>
                    {field.description && (
                      <div className="form-description">{field.description}</div>
                    )}
                    <input
                      type={field.key.toLowerCase().includes('key') || field.key.toLowerCase().includes('token') || field.key.toLowerCase().includes('secret') ? 'password' : 'text'}
                      value={configValues[field.key] ?? ''}
                      onChange={e => setConfigValues(prev => ({ ...prev, [field.key]: e.target.value }))}
                      placeholder={field.required ? 'Required' : 'Optional'}
                    />
                  </div>
                ))}
              </div>
            )}

            {/* Target level */}
            <div className="mcp-target-level">
              <label>Add to</label>
              <select value={targetLevel} onChange={e => setTargetLevel(e.target.value)}>
                <option value="profile">Profile ({context?.profileName})</option>
                <option value="root">Root ({context?.rootName})</option>
                {context?.actionName && (
                  <option value="action">Action ({context.actionName})</option>
                )}
              </select>
            </div>
          </div>
        )}

        {error && <div className="form-error">{error}</div>}

        <div className="btn-group">
          {serverDetail && (
            <button
              className="btn btn-primary"
              onClick={handleAdd}
              disabled={adding}
            >
              {adding ? 'Adding...' : 'Add Server'}
            </button>
          )}
          <button
            className="btn btn-secondary"
            onClick={() => setShowMcpBrowser(false)}
            disabled={adding}
          >
            {serverDetail ? 'Cancel' : 'Close'}
          </button>
        </div>
      </div>
    </div>
  );
}
