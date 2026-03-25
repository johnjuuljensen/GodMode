import { useState, useEffect, useMemo, useCallback } from 'react';
import { useAppStore } from '../../store';
import type { ProjectRootInfo } from '../../signalr/types';
import './CreateProject.css';

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

  // Find connected servers with roots
  const connectedServers = useMemo(
    () => servers
      .map((s, i) => ({ server: s, index: i }))
      .filter(({ server }) => server.connectionState === 'connected' && server.roots.length > 0),
    [servers],
  );

  const [selectedServerIndex, setSelectedServerIndex] = useState<number>(connectedServers[0]?.index ?? -1);
  const [selectedRootName, setSelectedRootName] = useState<string>('');
  const [selectedActionName, setSelectedActionName] = useState<string>('');
  const [selectedModel, setSelectedModel] = useState('opus');
  const [formValues, setFormValues] = useState<Record<string, string>>({});
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const server = servers[selectedServerIndex];
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

  const selectedRoot = roots.find(r => r.Name === selectedRootName);
  const actions = selectedRoot?.Actions ?? [];
  const selectedAction = actions.find(a => a.Name === selectedActionName) ?? null;
  const formFields = useMemo(() => selectedAction?.InputSchema ? parseFormFields(selectedAction.InputSchema) : [], [selectedAction]);

  // Auto-select first root when server changes
  useEffect(() => {
    if (roots.length > 0 && !roots.find(r => r.Name === selectedRootName)) {
      setSelectedRootName(roots[0].Name);
    }
  }, [roots, selectedRootName]);

  // Auto-select first action when root changes
  useEffect(() => {
    if (actions.length > 0 && !actions.find(a => a.Name === selectedActionName)) {
      setSelectedActionName(actions[0].Name);
    } else if (actions.length === 0) {
      setSelectedActionName('');
    }
  }, [actions, selectedActionName]);

  // Reset form values when action changes, applying defaults
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

  const setFieldValue = useCallback((key: string, value: string) => {
    setFormValues(prev => ({ ...prev, [key]: value }));
  }, []);

  const handleCreate = async () => {
    if (!server || !selectedRoot) return;

    // Validate required fields
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

  return (
    <div className="modal-overlay" onClick={() => !creating && setShowCreateProject(false)}>
      <div className="modal create-project-modal" onClick={e => e.stopPropagation()}>
        <h2>New Project</h2>

        {/* Server selector (only if multiple) */}
        {connectedServers.length > 1 && (
          <div className="form-group">
            <label>Server</label>
            <select
              value={selectedServerIndex}
              onChange={e => setSelectedServerIndex(Number(e.target.value))}
            >
              {connectedServers.map(({ server: s, index: i }) => (
                <option key={i} value={i}>
                  {s.serverInfo.Name || s.serverInfo.Url}
                </option>
              ))}
            </select>
          </div>
        )}

        {/* Root selector grouped by profile */}
        <div className="form-group">
          <label>Project Root</label>
          <select
            value={selectedRootName}
            onChange={e => setSelectedRootName(e.target.value)}
          >
            {rootsByProfile.size <= 1 ? (
              roots.map(r => (
                <option key={r.Name} value={r.Name}>
                  {r.Name}{r.Description ? ` — ${r.Description}` : ''}
                </option>
              ))
            ) : (
              Array.from(rootsByProfile.entries()).map(([profile, profileRoots]) => (
                <optgroup key={profile} label={profile}>
                  {profileRoots.map(r => (
                    <option key={r.Name} value={r.Name}>
                      {r.Name}{r.Description ? ` — ${r.Description}` : ''}
                    </option>
                  ))}
                </optgroup>
              ))
            )}
          </select>
        </div>

        {/* Action selector (if multiple actions) */}
        {actions.length > 1 && (
          <div className="form-group">
            <label>Action</label>
            <select
              value={selectedActionName}
              onChange={e => setSelectedActionName(e.target.value)}
            >
              {actions.map(a => (
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
                <span>{formValues[field.key] === 'true' ? 'Yes' : 'No'}</span>
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
            onClick={() => setShowCreateProject(false)}
            disabled={creating}
          >
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
