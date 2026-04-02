import { useState, useEffect, useCallback, useMemo } from 'react';
import { useAppStore } from '../../store';
import type { McpServerConfig } from '../../signalr/types';
import { RowDelete } from '../settings-shared';
import { CONNECTOR_CATALOG, findCatalogEntry, CATEGORY_LABELS, type CatalogConnector } from '../../connectors-catalog';
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

  // Setup form state
  const [setupConnector, setSetupConnector] = useState<CatalogConnector | null>(null);
  const [setupProfile, setSetupProfile] = useState(defaultProfile);
  const [fieldValues, setFieldValues] = useState<Record<string, string>>({});
  const [saving, setSaving] = useState(false);

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

  const selectConnector = (connector: CatalogConnector) => {
    setSetupConnector(connector);
    // Initialize field values with defaults
    const defaults: Record<string, string> = {};
    if (connector.config.env) {
      for (const key of Object.keys(connector.config.env)) {
        defaults[key] = '';
      }
    }
    if (connector.config.argTemplates) {
      for (const key of Object.keys(connector.config.argTemplates)) {
        defaults[`__arg__${key}`] = '';
      }
    }
    if (connector.config.headerTemplates) {
      for (const key of Object.keys(connector.config.headerTemplates)) {
        defaults[`__hdr__${key}`] = '';
      }
    }
    setFieldValues(defaults);
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

      // Resolve arg templates
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

      // Resolve header templates (for SSE connectors)
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
      // Reload if we added to the currently viewed profile
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

  const serverEntries = Object.entries(servers);

  // Group catalog by category
  const catalogByCategory = useMemo(() => {
    const groups = new Map<string, CatalogConnector[]>();
    for (const c of CONNECTOR_CATALOG) {
      if (!groups.has(c.category)) groups.set(c.category, []);
      groups.get(c.category)!.push(c);
    }
    return groups;
  }, []);

  // ── Setup view (credential form) ──
  if (view === 'setup' && setupConnector) {
    const envFields = Object.entries(setupConnector.config.env ?? {});
    const argFields = Object.entries(setupConnector.config.argTemplates ?? {});
    const headerFields = Object.entries(setupConnector.config.headerTemplates ?? {});

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

        {error && <div className="settings-error">{error}</div>}

        <div className="form-group">
          <label>Profile</label>
          <select value={setupProfile} onChange={e => setSetupProfile(e.target.value)}>
            {profiles.map(p => <option key={p.Name} value={p.Name}>{p.Name}</option>)}
          </select>
        </div>

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
          <button className="btn btn-primary" onClick={handleAdd} disabled={saving}>
            {saving ? 'Adding...' : `Add ${setupConnector.name}`}
          </button>
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

        {Array.from(catalogByCategory.entries()).map(([category, connectors]) => (
          <div key={category}>
            <div className="catalog-category">{CATEGORY_LABELS[category] ?? category}</div>
            <div className="catalog-grid">
              {connectors.map(c => (
                <button key={c.id} className="catalog-card" onClick={() => selectConnector(c)}>
                  <div className="catalog-card-header">
                    <div className="catalog-card-logo">
                      <img src={c.logoUrl} alt="" />
                    </div>
                    <div className="catalog-card-title">
                      <div className="catalog-card-name">{c.name}</div>
                      <div className="catalog-card-badges">
                        <span className="settings-badge">{c.transport}</span>
                        {c.stability === 'beta' && <span className="settings-badge">beta</span>}
                      </div>
                    </div>
                  </div>
                  <div className="catalog-card-desc">{c.description}</div>
                </button>
              ))}
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
                      <span className="settings-badge">{config.Url ? 'sse' : config.Command || 'unknown'}</span>
                      {config.Url ? <span className="truncate">{config.Url}</span> : config.Args && <span className="truncate">{config.Args.join(' ')}</span>}
                    </div>
                  </div>
                  <div className="settings-item-actions">
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
