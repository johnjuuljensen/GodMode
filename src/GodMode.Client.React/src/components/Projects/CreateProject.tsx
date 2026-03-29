import { useState, useEffect, useMemo, useCallback } from 'react';
import { useAppStore } from '../../store';
import type { ProjectRootInfo, CreateActionInfo, McpServerConfig } from '../../signalr/types';
import './CreateProject.css';
import '../Mcp/McpBrowser.css';

interface FormField {
  key: string;
  title: string;
  fieldType: 'string' | 'multiline' | 'boolean' | 'enum';
  isRequired: boolean;
  description?: string | null;
  defaultValue?: string;
  enumOptions?: { value: string; label: string }[];
}

function parseFormFields(schema: unknown): FormField[] {
  if (!schema || typeof schema !== 'object') return [];
  const s = schema as Record<string, unknown>;
  const properties = s.properties as Record<string, Record<string, unknown>> | undefined;
  if (!properties) return [];

  const required = new Set<string>(Array.isArray(s.required) ? s.required as string[] : []);
  const fields: FormField[] = [];

  for (const [key, prop] of Object.entries(properties)) {
    const type = prop.type as string;
    const title = (prop.title as string) || key;
    const description = prop.description as string | undefined;
    const defaultValue = prop.default != null ? String(prop.default) : undefined;

    if (type === 'boolean') {
      fields.push({ key, title, fieldType: 'boolean', isRequired: false, description, defaultValue: defaultValue ?? 'false' });
    } else if (Array.isArray(prop.enum)) {
      const enumOptions = (prop.enum as string[]).map(v => ({ value: v, label: v }));
      fields.push({ key, title, fieldType: 'enum', isRequired: required.has(key), description, defaultValue, enumOptions });
    } else if (prop.format === 'multiline' || (prop.maxLength && (prop.maxLength as number) > 200)) {
      fields.push({ key, title, fieldType: 'multiline', isRequired: required.has(key), description, defaultValue });
    } else {
      fields.push({ key, title, fieldType: 'string', isRequired: required.has(key), description, defaultValue });
    }
  }

  return fields;
}

const MODEL_OPTIONS = ['opus', 'sonnet', 'haiku'];

export function CreateProject() {
  const servers = useAppStore(s => s.servers);
  const setShowCreateProject = useAppStore(s => s.setShowCreateProject);
  const setShowMcpBrowser = useAppStore(s => s.setShowMcpBrowser);

  const connectedServers = useMemo(
    () => servers.filter(s => s.connectionState === 'connected' && s.roots.length > 0),
    [servers],
  );

  const [selectedServerId, setSelectedServerId] = useState<string>(connectedServers[0]?.registration.url ?? '');
  const [selectedRoot, setSelectedRoot] = useState<ProjectRootInfo | null>(null);
  const [selectedActionName, setSelectedActionName] = useState<string>('');
  const [selectedModel, setSelectedModel] = useState('opus');
  const [formValues, setFormValues] = useState<Record<string, string>>({});
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [effectiveMcpServers, setEffectiveMcpServers] = useState<Record<string, McpServerConfig>>({});

  const server = servers.find(s => s.registration.url === selectedServerId);
  const roots = server?.roots ?? [];

  // Group roots by profile
  const rootsByProfile = useMemo(() => {
    const groups = new Map<string, ProjectRootInfo[]>();
    for (const root of roots) {
      const profile = root.ProfileName ?? 'Default';
      if (!groups.has(profile)) groups.set(profile, []);
      groups.get(profile)!.push(root);
    }
    return groups;
  }, [roots]);

  // If there's only one root, auto-select it
  useEffect(() => {
    if (roots.length === 1 && !selectedRoot) {
      setSelectedRoot(roots[0]);
    }
  }, [roots, selectedRoot]);

  const actions = selectedRoot?.Actions ?? [];
  const selectedAction = actions.find((a: CreateActionInfo) => a.Name === selectedActionName) ?? null;
  const formFields = useMemo(() => selectedAction?.InputSchema ? parseFormFields(selectedAction.InputSchema) : [], [selectedAction]);

  // Auto-select first action when root changes
  useEffect(() => {
    if (actions.length > 0 && !actions.find((a: CreateActionInfo) => a.Name === selectedActionName)) {
      setSelectedActionName(actions[0].Name);
    } else if (actions.length === 0) {
      setSelectedActionName('');
    }
  }, [actions, selectedActionName]);

  // Reset form values when action changes
  useEffect(() => {
    const defaults: Record<string, string> = {};
    for (const field of formFields) {
      defaults[field.key] = field.defaultValue ?? '';
    }
    setFormValues(defaults);
  }, [formFields]);

  // Set model from action config default
  useEffect(() => {
    if (selectedAction?.Model) {
      setSelectedModel(selectedAction.Model);
    }
  }, [selectedAction]);

  // Load effective MCP servers when root/action changes
  useEffect(() => {
    if (!server || !selectedRoot) {
      setEffectiveMcpServers({});
      return;
    }
    const profileName = selectedRoot.ProfileName ?? 'Default';
    server.hub.getEffectiveMcpServers(profileName, selectedRoot.Name, selectedActionName || undefined)
      .then(setEffectiveMcpServers)
      .catch(() => setEffectiveMcpServers({}));
  }, [server, selectedRoot, selectedActionName]);

  const setFieldValue = useCallback((key: string, value: string) => {
    setFormValues(prev => ({ ...prev, [key]: value }));
  }, []);

  const handleCreate = async () => {
    if (!server || !selectedRoot) return;

    for (const field of formFields) {
      if (field.isRequired && !formValues[field.key]?.trim()) {
        setError(`"${field.title}" is required`);
        return;
      }
    }

    setCreating(true);
    setError(null);

    try {
      const profileName = selectedRoot.ProfileName ?? 'Default';
      const inputs: Record<string, unknown> = { model: selectedModel };
      for (const field of formFields) {
        const val = formValues[field.key];
        if (field.fieldType === 'boolean') {
          inputs[field.key] = val === 'true';
        } else if (val) {
          inputs[field.key] = val;
        }
      }

      await server.hub.createProject(
        profileName,
        selectedRoot.Name,
        selectedActionName || null,
        inputs,
      );

      setShowCreateProject(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create project');
    } finally {
      setCreating(false);
    }
  };

  const close = () => !creating && setShowCreateProject(false);

  // ─── Step 1: Root Selection ─────────────────────────────────────────────────
  if (!selectedRoot) {
    return (
      <div className="modal-overlay" onClick={close}>
        <div className="modal create-project-modal" onClick={e => e.stopPropagation()}>
          <h2>New Project</h2>

          {connectedServers.length > 1 && (
            <div className="form-group">
              <label>Server</label>
              <select
                value={selectedServerId}
                onChange={e => { setSelectedServerId(e.target.value); setSelectedRoot(null); }}
              >
                {connectedServers.map(s => (
                  <option key={s.registration.url} value={s.registration.url}>
                    {s.registration.displayName || s.registration.url}
                  </option>
                ))}
              </select>
            </div>
          )}

          <p className="root-picker-hint">Choose a project root to get started:</p>

          <div className="root-picker-grid">
            {[...rootsByProfile.entries()].map(([profileName, profileRoots]) => (
              profileRoots.map(root => (
                <button
                  key={`${profileName}/${root.Name}`}
                  className="root-picker-card"
                  onClick={() => setSelectedRoot(root)}
                >
                  <div className="root-picker-card-name">{root.Name}</div>
                  {root.Description && (
                    <div className="root-picker-card-desc">{root.Description}</div>
                  )}
                  {root.Actions && root.Actions.length > 0 && (
                    <div className="root-picker-card-actions">
                      {root.Actions.map((a: CreateActionInfo) => (
                        <span key={a.Name} className="root-picker-action-badge">{a.Name}</span>
                      ))}
                    </div>
                  )}
                  {rootsByProfile.size > 1 && (
                    <div className="root-picker-card-profile">{profileName}</div>
                  )}
                </button>
              ))
            ))}
          </div>

          <div className="btn-group">
            <button className="btn btn-secondary" onClick={close}>Cancel</button>
          </div>
        </div>
      </div>
    );
  }

  // ─── Step 2: Project Form ───────────────────────────────────────────────────
  return (
    <div className="modal-overlay" onClick={close}>
      <div className="modal create-project-modal" onClick={e => e.stopPropagation()}>
        <div className="create-project-header">
          {roots.length > 1 && (
            <button className="btn btn-secondary btn-sm" onClick={() => setSelectedRoot(null)}>
              ← Back
            </button>
          )}
          <h2>New Project — {selectedRoot.Name}</h2>
        </div>

        {/* Action selector (if multiple actions) */}
        {actions.length > 1 && (
          <div className="form-group">
            <label>Action</label>
            <select
              value={selectedActionName}
              onChange={e => setSelectedActionName(e.target.value)}
            >
              {actions.map((a: CreateActionInfo) => (
                <option key={a.Name} value={a.Name}>
                  {a.Name}{a.Description ? ` — ${a.Description}` : ''}
                </option>
              ))}
            </select>
          </div>
        )}

        {/* Model selector */}
        <div className="form-group">
          <label>Model</label>
          <select value={selectedModel} onChange={e => setSelectedModel(e.target.value)}>
            {MODEL_OPTIONS.map(m => (
              <option key={m} value={m}>{m}</option>
            ))}
          </select>
        </div>

        {/* MCP Servers panel */}
        <div className="mcp-servers-panel">
          <div className="mcp-servers-header">
            <h4>
              MCP Servers
              {Object.keys(effectiveMcpServers).length > 0 && (
                <span className="mcp-count-badge">{Object.keys(effectiveMcpServers).length}</span>
              )}
            </h4>
            <button
              className="btn btn-secondary btn-sm"
              onClick={() => setShowMcpBrowser(true, {
                serverId: selectedServerId,
                profileName: selectedRoot.ProfileName ?? 'Default',
                rootName: selectedRoot.Name,
                actionName: selectedActionName || undefined,
              })}
            >
              Browse &amp; Add
            </button>
          </div>
          {Object.keys(effectiveMcpServers).length > 0 && (
            <div className="mcp-server-list">
              {Object.entries(effectiveMcpServers).map(([name, config]) => (
                <div key={name} className="mcp-server-entry">
                  <span>
                    <span className="mcp-server-entry-name">{name}</span>
                    <span className="mcp-type-badge">{config.url ? 'remote' : 'stdio'}</span>
                  </span>
                  <button
                    className="mcp-remove-btn"
                    title="Remove"
                    onClick={async () => {
                      try {
                        await server!.hub.removeMcpServer(name, 'profile', selectedRoot!.ProfileName ?? 'Default', selectedRoot!.Name);
                        const updated = await server!.hub.getEffectiveMcpServers(
                          selectedRoot!.ProfileName ?? 'Default', selectedRoot!.Name, selectedActionName || undefined,
                        );
                        setEffectiveMcpServers(updated);
                      } catch { /* ignore */ }
                    }}
                  >
                    &#x2715;
                  </button>
                </div>
              ))}
            </div>
          )}
        </div>

        {/* Dynamic form fields */}
        {formFields.map(field => (
          <div className="form-group" key={field.key}>
            <label>
              {field.title}
              {field.isRequired && <span className="form-required">*</span>}
            </label>
            {field.description && (
              <div className="form-description">{field.description}</div>
            )}
            {field.fieldType === 'boolean' ? (
              <label className="form-toggle">
                <input
                  type="checkbox"
                  checked={formValues[field.key] === 'true'}
                  onChange={e => setFieldValue(field.key, e.target.checked ? 'true' : 'false')}
                />
                <span className="toggle-track" />
                <span className="toggle-label">{formValues[field.key] === 'true' ? 'On' : 'Off'}</span>
              </label>
            ) : field.fieldType === 'enum' ? (
              <select
                value={formValues[field.key] ?? ''}
                onChange={e => setFieldValue(field.key, e.target.value)}
              >
                {field.enumOptions?.map(opt => (
                  <option key={opt.value} value={opt.value}>{opt.label}</option>
                ))}
              </select>
            ) : field.fieldType === 'multiline' ? (
              <textarea
                className="form-textarea"
                value={formValues[field.key] ?? ''}
                onChange={e => setFieldValue(field.key, e.target.value)}
                rows={5}
              />
            ) : (
              <input
                type="text"
                value={formValues[field.key] ?? ''}
                onChange={e => setFieldValue(field.key, e.target.value)}
              />
            )}
          </div>
        ))}

        {error && <div className="form-error">{error}</div>}

        <div className="btn-group">
          <button
            className="btn btn-primary"
            onClick={handleCreate}
            disabled={creating || !selectedRoot}
          >
            {creating ? 'Creating...' : 'Create'}
          </button>
          <button
            className="btn btn-secondary"
            onClick={close}
            disabled={creating}
          >
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
