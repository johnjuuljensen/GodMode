import { useState, useEffect, useCallback, useMemo } from 'react';
import { useAppStore } from '../../store';
import type { McpServerConfig, OAuthProviderStatus } from '../../signalr/types';
import { RowDelete } from '../settings-shared';
import { CONNECTOR_CATALOG, findCatalogEntry, CATEGORY_LABELS, CONNECTOR_TO_OAUTH_PROVIDER, type CatalogConnector, type CatalogSetupStep } from '../../connectors-catalog';
import '../settings-common.css';
import './McpConfigPanel.css';

const BackArrow = () => (
  <svg width={16} height={16} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round"><path d="M10 4L6 8l4 4"/></svg>
);

type View = 'list' | 'catalog' | 'setup';

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

  // Setup form state (non-OAuth connectors only)
  const [setupConnector, setSetupConnector] = useState<CatalogConnector | null>(null);
  const [setupProfile, setSetupProfile] = useState(defaultProfile);
  const [fieldValues, setFieldValues] = useState<Record<string, string>>({});
  const [saving, setSaving] = useState(false);

  // Setup steps check state: key → 'checking' | 'ok' | 'missing'
  const [stepStatus, setStepStatus] = useState<Record<string, 'checking' | 'ok' | 'missing'>>({});

  // OAuth status per provider (proxy flow)
  const [oauthStatus, setOauthStatus] = useState<Record<string, OAuthProviderStatus>>({});
  // MCP OAuth status per connector (remote MCP server flow)
  const [mcpOAuthStatus, setMcpOAuthStatus] = useState<Record<string, { Connected: boolean }>>({});

  const loadOAuthStatus = useCallback(async () => {
    if (!hub || !selectedProfile) return;
    try {
      const status = await hub.getOAuthStatus(selectedProfile);
      setOauthStatus(status);
    } catch { /* ignore */ }
    try {
      const resp = await fetch(`/api/mcp-oauth/status?profileName=${encodeURIComponent(selectedProfile)}`);
      if (resp.ok) setMcpOAuthStatus(await resp.json());
    } catch { /* ignore */ }
  }, [hub, selectedProfile]);

  useEffect(() => { loadOAuthStatus(); }, [loadOAuthStatus]);

  const anyRoot = roots.find(r => r.ProfileName === selectedProfile || r.ProfileName === 'Default')?.Name;

  const loadServers = useCallback(async () => {
    if (!hub || !selectedProfile || !anyRoot) return;
    setLoading(true);
    setError(null);
    try {
      const result = await hub.getEffectiveMcpServers(selectedProfile, anyRoot);
      setServers(result);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load connectors');
    } finally {
      setLoading(false);
    }
  }, [hub, selectedProfile, anyRoot]);

  useEffect(() => { loadServers(); }, [loadServers]);

  const goList = () => { setView('list'); setError(null); setSetupConnector(null); };

  const openCatalog = () => {
    setSetupProfile(selectedProfile);
    setView('catalog');
    setError(null);
  };

  // Add an OAuth connector directly — token injected server-side at project start
  const addOAuthConnector = async (connector: CatalogConnector, profile: string) => {
    if (!hub) return;
    setSaving(true);
    try {
      // Stdio connectors (e.g. gws) use command+args; SSE connectors use URL
      const config: McpServerConfig = connector.config.command
        ? { Command: connector.config.command, Args: connector.config.args }
        : { Url: connector.config.url! };
      await hub.addMcpServer(connector.id, config, 'profile', profile);
      goList();
      if (profile === selectedProfile) await loadServers();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to add connector');
    } finally {
      setSaving(false);
    }
  };

  // Handle clicking an OAuth connector in the catalog
  const handleOAuthCatalogClick = (connector: CatalogConnector) => {
    const provider = CONNECTOR_TO_OAUTH_PROVIDER[connector.id];
    if (!provider) return;

    const profile = setupProfile || selectedProfile;
    const providerConnected = oauthStatus[provider]?.Connected;

    if (providerConnected) {
      // Already connected — add directly
      addOAuthConnector(connector, profile);
    } else {
      // Not connected — save intent and redirect to OAuth
      sessionStorage.setItem('oauth-pending-connector', JSON.stringify({
        connectorId: connector.id,
        profileName: profile,
      }));
      window.location.href = `/api/oauth/initiate?provider=${encodeURIComponent(provider)}&profileId=${encodeURIComponent(profile)}&purpose=connector`;
    }
  };

  const checkSetupSteps = useCallback(async (steps: CatalogSetupStep[]) => {
    if (!hub) return;
    const status: Record<string, 'checking' | 'ok' | 'missing'> = {};
    for (const step of steps) status[step.key] = step.checkCommand ? 'checking' : 'ok';
    setStepStatus({ ...status });

    for (const step of steps) {
      if (step.checkCommand) {
        try {
          const result = await hub.checkCommand(step.checkCommand);
          status[step.key] = result ? 'ok' : 'missing';
        } catch {
          status[step.key] = 'missing';
        }
        setStepStatus({ ...status });
      }
    }
  }, [hub]);

  // Add an HTTP connector: save config, then redirect to MCP OAuth for authentication
  const addHttpConnector = async (connector: CatalogConnector) => {
    if (!hub) return;
    setSaving(true);
    try {
      const profile = setupProfile || selectedProfile;
      const config: McpServerConfig = { Url: connector.config.url! };
      await hub.addMcpServer(connector.id, config, 'profile', profile);

      // Check if already authenticated
      if (mcpOAuthStatus[connector.id]?.Connected) {
        goList();
        if (profile === selectedProfile) await loadServers();
      } else {
        // Redirect to MCP OAuth flow — server handles registration + PKCE + redirect
        window.location.href = `/api/mcp-oauth/initiate?connectorId=${encodeURIComponent(connector.id)}&profileName=${encodeURIComponent(profile)}&mcpServerUrl=${encodeURIComponent(connector.config.url!)}`;
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to add connector');
      setSaving(false);
    }
  };

  // Initiate MCP OAuth for an already-added connector
  const connectMcpOAuth = (connectorId: string, url: string) => {
    const profile = selectedProfile;
    window.location.href = `/api/mcp-oauth/initiate?connectorId=${encodeURIComponent(connectorId)}&profileName=${encodeURIComponent(profile)}&mcpServerUrl=${encodeURIComponent(url)}`;
  };

  // Disconnect MCP OAuth for a connector
  const disconnectMcpOAuth = async (connectorId: string) => {
    try {
      await fetch(`/api/mcp-oauth/disconnect?profileName=${encodeURIComponent(selectedProfile)}&connectorId=${encodeURIComponent(connectorId)}`, { method: 'POST' });
      await loadOAuthStatus();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to disconnect');
    }
  };

  const selectConnector = (connector: CatalogConnector) => {
    // HTTP connectors (remote MCP servers) — add directly, auth handled by the remote server
    if (connector.transport === 'http') {
      addHttpConnector(connector);
      return;
    }

    // OAuth connectors skip the setup form
    if (connector.config.auth === 'oauth') {
      handleOAuthCatalogClick(connector);
      return;
    }

    setSetupConnector(connector);
    const defaults: Record<string, string> = {};
    if (connector.config.env) {
      for (const key of Object.keys(connector.config.env)) defaults[key] = '';
    }
    if (connector.config.argTemplates) {
      for (const key of Object.keys(connector.config.argTemplates)) defaults[`__arg__${key}`] = '';
    }
    if (connector.config.headerTemplates) {
      for (const key of Object.keys(connector.config.headerTemplates)) defaults[`__hdr__${key}`] = '';
    }
    setFieldValues(defaults);

    // Check setup prerequisites
    if (connector.setupSteps?.length) {
      checkSetupSteps(connector.setupSteps);
    }

    setView('setup');
  };

  const handleAdd = async () => {
    if (!hub || !setupConnector) return;
    setError(null);
    setSaving(true);
    try {
      const envDef = setupConnector.config.env ?? {};
      const env: Record<string, string> = {};
      for (const [key, def] of Object.entries(envDef)) {
        const val = fieldValues[key]?.trim() ?? '';
        if (def.required && !val) {
          setError(`"${def.label}" is required`);
          setSaving(false);
          return;
        }
        if (val) env[key] = val;
      }

      let args = setupConnector.config.args ? [...setupConnector.config.args] : undefined;
      if (setupConnector.config.argTemplates && args) {
        for (const [key, def] of Object.entries(setupConnector.config.argTemplates)) {
          const val = fieldValues[`__arg__${key}`]?.trim() ?? '';
          if (def.required && !val) {
            setError(`"${def.label}" is required`);
            setSaving(false);
            return;
          }
          args = args.map(a => a.replace(`{${key}}`, val));
        }
      }

      const headerDef = setupConnector.config.headerTemplates ?? {};
      const headers: Record<string, string> = {};
      for (const [headerName, def] of Object.entries(headerDef)) {
        const val = fieldValues[`__hdr__${headerName}`]?.trim() ?? '';
        if (def.required && !val) {
          setError(`"${def.label}" is required`);
          setSaving(false);
          return;
        }
        if (val) headers[headerName] = def.valueTemplate.replace('{value}', val);
      }

      const config: McpServerConfig = setupConnector.config.url
        ? { Url: setupConnector.config.url, Headers: Object.keys(headers).length > 0 ? headers : undefined }
        : { Command: setupConnector.config.command ?? '', Args: args, Env: Object.keys(env).length > 0 ? env : undefined };

      await hub.addMcpServer(setupConnector.id, config, 'profile', setupProfile);
      goList();
      if (setupProfile === selectedProfile) await loadServers();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to add connector');
    } finally {
      setSaving(false);
    }
  };

  const handleRemove = async (name: string) => {
    if (!hub) return;
    setError(null);
    try {
      await hub.removeMcpServer(name, 'profile', selectedProfile);
      await loadServers();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to remove connector');
    }
  };

  const handleOAuthDisconnect = async (provider: string) => {
    if (!hub) return;
    try {
      await hub.disconnectOAuthProvider(selectedProfile, provider);
      await loadOAuthStatus();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to disconnect');
    }
  };

  const serverEntries = Object.entries(servers);

  const catalogByCategory = useMemo(() => {
    const groups = new Map<string, CatalogConnector[]>();
    for (const c of CONNECTOR_CATALOG) {
      if (!groups.has(c.category)) groups.set(c.category, []);
      groups.get(c.category)!.push(c);
    }
    return groups;
  }, []);

  // ── Setup view ──
  if (view === 'setup' && setupConnector) {
    const envFields = Object.entries(setupConnector.config.env ?? {});
    const argFields = Object.entries(setupConnector.config.argTemplates ?? {});
    const headerFields = Object.entries(setupConnector.config.headerTemplates ?? {});
    const steps = setupConnector.setupSteps ?? [];
    const hasSteps = steps.length > 0;
    const gwsInstalled = !steps.some(s => s.checkCommand) || steps.filter(s => s.checkCommand).every(s => stepStatus[s.key] === 'ok');
    const hasAnyMissing = steps.some(s => stepStatus[s.key] === 'missing');

    // OAuth support: connector has a mapped OAuth provider
    const oauthProvider = CONNECTOR_TO_OAUTH_PROVIDER[setupConnector.id];
    const oauthConnected = oauthProvider ? oauthStatus[oauthProvider]?.Connected : false;

    // Can add if: gws installed AND (OAuth connected OR gws has local auth — we assume local auth if gws is installed)
    const canAdd = gwsInstalled;

    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={() => setView('catalog')}><BackArrow /> Catalog</button>
        </div>

        <div className="connector-setup-header">
          <div className="connector-setup-logo">
            <img src={setupConnector.logoUrl} alt="" />
          </div>
          <div className="connector-setup-info">
            <h3>{setupConnector.name}</h3>
            <p>{setupConnector.description}</p>
          </div>
        </div>

        {setupConnector.config.note && (
          <div className="connector-note">{setupConnector.config.note}</div>
        )}

        {setupConnector.manifest && (
          <div className="connector-manifest">
            <div className="connector-manifest-header">
              <span className="connector-manifest-label">App Manifest</span>
              <button className="btn btn-secondary btn-sm" onClick={() => {
                navigator.clipboard.writeText(setupConnector.manifest!);
                const btn = document.querySelector('.connector-manifest-header .btn') as HTMLButtonElement;
                if (btn) { btn.textContent = 'Copied!'; setTimeout(() => { btn.textContent = 'Copy'; }, 1500); }
              }}>Copy</button>
            </div>
            <pre className="connector-manifest-code">{setupConnector.manifest}</pre>
            <div className="form-description">
              Go to <a href="https://api.slack.com/apps" target="_blank" rel="noopener noreferrer">api.slack.com/apps</a> → Create New App → From an app manifest → paste this YAML → Create → Install to Workspace → copy the Bot Token below.
            </div>
          </div>
        )}

        {error && <div className="settings-error">{error}</div>}

        <div className="form-group">
          <label>Profile</label>
          <select value={setupProfile} onChange={e => setSetupProfile(e.target.value)}>
            {profiles.map(p => <option key={p.Name} value={p.Name}>{p.Name}</option>)}
          </select>
        </div>

        {hasSteps && (
          <div className="setup-steps">
            <div className="setup-steps-title">Setup</div>
            {steps.map((step, i) => {
              const status = stepStatus[step.key] ?? (step.checkCommand ? 'checking' : 'ok');
              return (
                <div key={step.key} className={`setup-step setup-step-${status}`}>
                  <div className="setup-step-header">
                    <span className="setup-step-num">{i + 1}</span>
                    <span className="setup-step-icon">
                      {status === 'checking' && '...'}
                      {status === 'ok' && '\u2713'}
                      {status === 'missing' && '!'}
                    </span>
                    <span className="setup-step-label">{step.label}</span>
                  </div>
                  <div className="setup-step-desc">{step.description}</div>

                  {status === 'missing' && step.installUrl && (
                    <div className="setup-step-action">
                      <a href={step.installUrl} target="_blank" rel="noopener noreferrer" className="setup-step-link">Installation docs</a>
                    </div>
                  )}

                  {/* Auth step: show OAuth button (primary) + local command (fallback) */}
                  {step.key === 'auth-gws' && oauthProvider && status === 'ok' && (
                    <div className="setup-step-action">
                      {oauthConnected ? (
                        <div className="setup-step-oauth-status">
                          <span className="setup-step-icon" style={{ color: '#22c55e' }}>{'\u2713'}</span>
                          Google account connected{oauthStatus[oauthProvider]?.Email ? ` (${oauthStatus[oauthProvider].Email})` : ''}
                        </div>
                      ) : (
                        <button className="btn btn-primary" style={{ marginTop: 4 }} onClick={() => {
                          sessionStorage.setItem('oauth-pending-connector', JSON.stringify({
                            connectorId: setupConnector.id,
                            profileName: setupProfile,
                          }));
                          window.location.href = `/api/oauth/initiate?provider=${encodeURIComponent(oauthProvider)}&profileId=${encodeURIComponent(setupProfile)}&purpose=connector`;
                        }}>
                          Connect with Google
                        </button>
                      )}
                      {step.setupCommand && (
                        <details className="setup-step-details">
                          <summary>Or authenticate locally on the server</summary>
                          <code className="setup-step-command">{step.setupCommand}</code>
                        </details>
                      )}
                    </div>
                  )}

                  {/* Non-OAuth auth steps */}
                  {step.setupCommand && !oauthProvider && status === 'ok' && (
                    <div className="setup-step-action">
                      <div className="setup-step-action-label">Run on the server:</div>
                      <code className="setup-step-command">{step.setupCommand}</code>
                      {step.setupUrl && (
                        <a href={step.setupUrl} target="_blank" rel="noopener noreferrer" className="setup-step-link">Setup docs</a>
                      )}
                    </div>
                  )}
                </div>
              );
            })}
            {hasAnyMissing && (
              <button className="btn btn-secondary" style={{ marginTop: 8 }} onClick={() => checkSetupSteps(steps)}>
                Re-check
              </button>
            )}
          </div>
        )}

        {argFields.map(([key, def]) => (
          <div className="form-group" key={key}>
            <label>
              {def.label}
              {def.required && <span className="form-required">*</span>}
            </label>
            <input
              type="text"
              value={fieldValues[`__arg__${key}`] ?? ''}
              onChange={e => setFieldValues(prev => ({ ...prev, [`__arg__${key}`]: e.target.value }))}
              placeholder={def.description}
            />
            <div className="form-description">{def.description}</div>
          </div>
        ))}

        {envFields.map(([key, def]) => (
          <div className="form-group" key={key}>
            <label>
              {def.label}
              {def.required && <span className="form-required">*</span>}
              {def.docsUrl && <a href={def.docsUrl} target="_blank" rel="noopener noreferrer" className="field-docs-link">Docs</a>}
            </label>
            <input
              type={def.secret ? 'password' : 'text'}
              value={fieldValues[key] ?? ''}
              onChange={e => setFieldValues(prev => ({ ...prev, [key]: e.target.value }))}
              placeholder={def.description}
            />
          </div>
        ))}

        {headerFields.map(([headerName, def]) => (
          <div className="form-group" key={headerName}>
            <label>
              {def.label}
              {def.required && <span className="form-required">*</span>}
              {def.docsUrl && <a href={def.docsUrl} target="_blank" rel="noopener noreferrer" className="field-docs-link">Docs</a>}
            </label>
            <input
              type={def.secret ? 'password' : 'text'}
              value={fieldValues[`__hdr__${headerName}`] ?? ''}
              onChange={e => setFieldValues(prev => ({ ...prev, [`__hdr__${headerName}`]: e.target.value }))}
              placeholder={def.description}
            />
          </div>
        ))}

        <div className="settings-form-actions">
          <button className="btn btn-primary" onClick={handleAdd} disabled={saving || !canAdd}>
            {saving ? 'Adding...' : `Add ${setupConnector.name}`}
          </button>
          {!canAdd && (
            <div className="form-description" style={{ marginTop: 6 }}>Install the required CLI tool first.</div>
          )}
        </div>
      </>
    );
  }

  // ── Catalog view (pick connector) ──
  if (view === 'catalog') {
    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={goList}><BackArrow /> Connectors</button>
        </div>
        <div className="settings-header"><h2>Add Connector</h2></div>

        {error && <div className="settings-error">{error}</div>}

        {Array.from(catalogByCategory.entries()).map(([category, connectors]) => (
          <div key={category}>
            <div className="catalog-category">{CATEGORY_LABELS[category] ?? category}</div>
            <div className="catalog-grid">
              {connectors.map(c => {
                const oauthProv = CONNECTOR_TO_OAUTH_PROVIDER[c.id];
                const isConnected = oauthProv ? oauthStatus[oauthProv]?.Connected : false;
                return (
                  <button key={c.id} className="catalog-card" onClick={() => selectConnector(c)} disabled={saving}>
                    <div className="catalog-card-header">
                      <div className="catalog-card-logo">
                        <img src={c.logoUrl} alt="" />
                      </div>
                      <div className="catalog-card-title">
                        <div className="catalog-card-name">{c.name}</div>
                        <div className="catalog-card-badges">
                          <span className="settings-badge">{c.transport}</span>
                          {c.stability === 'beta' && <span className="settings-badge">beta</span>}
                          {c.config.auth === 'oauth' && isConnected && <span className="settings-badge oauth-badge-connected">OAuth</span>}
                        </div>
                      </div>
                    </div>
                    <div className="catalog-card-desc">{c.description}</div>
                  </button>
                );
              })}
            </div>
          </div>
        ))}
      </>
    );
  }

  // ── List view (installed connectors) ──
  return (
    <>
      <div className="settings-header">
        <h2>Connectors</h2>
        <button className="settings-add-btn" onClick={openCatalog}>
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
        <div className="settings-empty">No connectors configured for this profile. Tap Add to browse the catalog.</div>
      ) : (
        <>
          <div className="settings-list">
            {serverEntries.map(([name, config]) => {
              const catalog = findCatalogEntry(name);
              const oauthProv = CONNECTOR_TO_OAUTH_PROVIDER[name];
              const provStatus = oauthProv ? oauthStatus[oauthProv] : undefined;
              const isHttp = config.Url && !config.Url.endsWith('/sse');
              const mcpConnected = mcpOAuthStatus[name]?.Connected;
              return (
                <div key={name} className="settings-item">
                  {catalog ? (
                    <div className="connector-logo">
                      <img src={catalog.logoUrl} alt="" />
                    </div>
                  ) : (
                    <div className="connector-logo-placeholder">
                      {name.charAt(0).toUpperCase()}
                    </div>
                  )}
                  <div className="settings-item-info">
                    <div className="settings-item-name">{catalog?.name ?? name}</div>
                    <div className="settings-item-meta">
                      <span className="settings-badge">{config.Url ? (config.Url.endsWith('/sse') ? 'sse' : 'http') : config.Command || 'unknown'}</span>
                      {/* Proxy OAuth status */}
                      {oauthProv && provStatus?.Connected && (
                        <span className="settings-badge oauth-badge-connected">OAuth</span>
                      )}
                      {oauthProv && !provStatus?.Connected && (
                        <span className="settings-badge oauth-badge-disconnected">Not connected</span>
                      )}
                      {/* MCP OAuth status (remote MCP servers) */}
                      {isHttp && !oauthProv && mcpConnected && (
                        <span className="settings-badge oauth-badge-connected">Connected</span>
                      )}
                      {isHttp && !oauthProv && !mcpConnected && (
                        <span className="settings-badge oauth-badge-disconnected">Not connected</span>
                      )}
                      {!oauthProv && !isHttp && config.Args && <span className="truncate">{config.Args.join(' ')}</span>}
                    </div>
                    {provStatus?.Email && <div className="settings-item-email">{provStatus.Email}</div>}
                  </div>
                  <div className="settings-item-actions">
                    {/* Proxy OAuth actions */}
                    {oauthProv && provStatus?.Connected && (
                      <button className="btn btn-secondary btn-sm" onClick={() => handleOAuthDisconnect(oauthProv)}>
                        Disconnect
                      </button>
                    )}
                    {oauthProv && !provStatus?.Connected && (
                      <button className="btn btn-primary btn-sm" onClick={() => {
                        sessionStorage.setItem('oauth-pending-connector', JSON.stringify({
                          connectorId: name,
                          profileName: selectedProfile,
                        }));
                        window.location.href = `/api/oauth/initiate?provider=${encodeURIComponent(oauthProv)}&profileId=${encodeURIComponent(selectedProfile)}&purpose=connector`;
                      }}>
                        Connect
                      </button>
                    )}
                    {/* MCP OAuth actions (remote MCP servers) */}
                    {isHttp && !oauthProv && mcpConnected && (
                      <button className="btn btn-secondary btn-sm" onClick={() => disconnectMcpOAuth(name)}>
                        Disconnect
                      </button>
                    )}
                    {isHttp && !oauthProv && !mcpConnected && (
                      <button className="btn btn-primary btn-sm" onClick={() => connectMcpOAuth(name, config.Url!)}>
                        Connect
                      </button>
                    )}
                    <RowDelete onDelete={() => handleRemove(name)} />
                  </div>
                </div>
              );
            })}
          </div>
          <div className="settings-count">{serverEntries.length} connector{serverEntries.length !== 1 ? 's' : ''}</div>
        </>
      )}
    </>
  );
}
