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
  const serverConnections = useAppStore(s => s.serverConnections);
  const setShowCreateProject = useAppStore(s => s.setShowCreateProject);
  const createProjectContext = useAppStore(s => s.createProjectContext);

  const connectedServers = useMemo(
    () => serverConnections.filter(c => c.connectionState === 'connected' && c.roots.length > 0),
    [serverConnections],
  );

  const defaultServerId = createProjectContext?.serverId ?? connectedServers[0]?.serverInfo.Id ?? '';
  const [selectedServerId, setSelectedServerId] = useState<string>(defaultServerId);
  const [selectedRootName, setSelectedRootName] = useState(createProjectContext?.rootName ?? '');
  const [selectedActionName, setSelectedActionName] = useState('');
  const [selectedModel, setSelectedModel] = useState('opus');
  const [formValues, setFormValues] = useState<Record<string, string>>({});
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const server = connectedServers.find(c => c.serverInfo.Id === selectedServerId);
  const roots = server?.roots ?? [];

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

  useEffect(() => {
    if (roots.length > 0 && !roots.find(r => r.Name === selectedRootName))
      setSelectedRootName(roots[0].Name);
  }, [roots, selectedRootName]);

  useEffect(() => {
    if (actions.length > 0 && !actions.find(a => a.Name === selectedActionName))
      setSelectedActionName(actions[0].Name);
    else if (actions.length === 0) setSelectedActionName('');
  }, [actions, selectedActionName]);

  useEffect(() => {
    const defaults: Record<string, string> = {};
    for (const field of formFields) defaults[field.key] = field.defaultValue ?? '';
    setFormValues(defaults);
  }, [formFields]);

  useEffect(() => {
    if (selectedAction?.Model) setSelectedModel(selectedAction.Model);
  }, [selectedAction]);

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
        if (field.fieldType === 'boolean') inputs[field.key] = val === 'true';
        else if (val) inputs[field.key] = val;
      }
      await server.hub.createProject(profileName, selectedRoot.Name, selectedActionName || null, inputs);
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

        {connectedServers.length > 1 && (
          <div className="form-group">
            <label>Server</label>
            <select value={selectedServerId} onChange={e => setSelectedServerId(e.target.value)}>
              {connectedServers.map(c => (
                <option key={c.serverInfo.Id} value={c.serverInfo.Id}>
                  {c.serverInfo.Name || c.serverInfo.Url}
                </option>
              ))}
            </select>
          </div>
        )}

        <div className="form-group">
          <label>Project Root</label>
          <select value={selectedRootName} onChange={e => setSelectedRootName(e.target.value)}>
            {rootsByProfile.size <= 1 ? (
              roots.map(r => (
                <option key={r.Name} value={r.Name}>{r.Name}{r.Description ? ` — ${r.Description}` : ''}</option>
              ))
            ) : (
              Array.from(rootsByProfile.entries()).map(([profile, profileRoots]) => (
                <optgroup key={profile} label={profile}>
                  {profileRoots.map(r => (
                    <option key={r.Name} value={r.Name}>{r.Name}{r.Description ? ` — ${r.Description}` : ''}</option>
                  ))}
                </optgroup>
              ))
            )}
          </select>
        </div>

        {actions.length > 1 && (
          <div className="form-group">
            <label>Action</label>
            <select value={selectedActionName} onChange={e => setSelectedActionName(e.target.value)}>
              {actions.map(a => (
                <option key={a.Name} value={a.Name}>{a.Name}{a.Description ? ` — ${a.Description}` : ''}</option>
              ))}
            </select>
          </div>
        )}

        <div className="form-group">
          <label>Model</label>
          <select value={selectedModel} onChange={e => setSelectedModel(e.target.value)}>
            {MODEL_OPTIONS.map(m => <option key={m} value={m}>{m}</option>)}
          </select>
        </div>

        {formFields.map(field => (
          <div className="form-group" key={field.key}>
            <label>{field.title}{field.isRequired && <span className="form-required">*</span>}</label>
            {field.description && <div className="form-description">{field.description}</div>}
            {field.fieldType === 'boolean' ? (
              <label className="form-toggle">
                <input type="checkbox" checked={formValues[field.key] === 'true'} onChange={e => setFieldValue(field.key, e.target.checked ? 'true' : 'false')} />
                <span>{formValues[field.key] === 'true' ? 'Yes' : 'No'}</span>
              </label>
            ) : field.fieldType === 'enum' ? (
              <select value={formValues[field.key] ?? ''} onChange={e => setFieldValue(field.key, e.target.value)}>
                {field.enumOptions?.map(opt => <option key={opt.value} value={opt.value}>{opt.label}</option>)}
              </select>
            ) : field.fieldType === 'multiline' ? (
              <textarea className="form-textarea" value={formValues[field.key] ?? ''} onChange={e => setFieldValue(field.key, e.target.value)} rows={5} />
            ) : (
              <input type="text" value={formValues[field.key] ?? ''} onChange={e => setFieldValue(field.key, e.target.value)} />
            )}
          </div>
        ))}

        {error && <div className="form-error">{error}</div>}

        <div className="btn-group">
          <button className="btn btn-primary" onClick={handleCreate} disabled={creating || !selectedRoot}>
            {creating ? 'Creating...' : 'Create'}
          </button>
          <button className="btn btn-secondary" onClick={() => setShowCreateProject(false)} disabled={creating}>Cancel</button>
        </div>
      </div>
    </div>
  );
}
