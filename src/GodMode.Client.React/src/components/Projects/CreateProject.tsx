import { useState, useEffect, useMemo, useCallback } from 'react';
import { useAppStore } from '../../store';
import type { ProjectRootInfo } from '../../signalr/types';
import { askConfirm } from '../../confirmDialog';
import '../settings-common.css';
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

/** What the user has entered, kept in sessionStorage so a remount or a reload of a discarded tab restores it. */
interface CreateDraft {
  serverId: string;
  rootName: string;
  actionName: string;
  model: string;
  values: Record<string, string>;
}

const DRAFT_STORAGE = 'godmode-create-draft';

function readDraft(): CreateDraft | null {
  try {
    const raw = sessionStorage.getItem(DRAFT_STORAGE);
    return raw ? JSON.parse(raw) as CreateDraft : null;
  } catch {
    return null;
  }
}

function writeDraft(draft: CreateDraft | null) {
  try {
    if (draft) sessionStorage.setItem(DRAFT_STORAGE, JSON.stringify(draft));
    else sessionStorage.removeItem(DRAFT_STORAGE);
  } catch {
    // Storage unavailable (private window, blocked site data): the draft only lives in component state
  }
}

/** Identifies the form the values belong to, by name, so a refreshed roots list does not count as a new form. */
const formKeyOf = (serverId: string, rootName: string, actionName: string) => `${serverId}
${rootName}
${actionName}`;

export function CreateProject() {
  const serverConnections = useAppStore(s => s.serverConnections);
  const openCreatedProject = useAppStore(s => s.openCreatedProject);
  const activePage = useAppStore(s => s.activePage);
  const createProjectContext = activePage?.type === 'createProject' ? activePage.context ?? null : null;
  const profileFilter = useAppStore(s => s.profileFilter);

  // A draft is restored unless the page was opened for a different root
  const [draft] = useState(() => {
    const d = readDraft();
    return d && (!createProjectContext || (d.serverId === createProjectContext.serverId && d.rootName === createProjectContext.rootName)) ? d : null;
  });

  // Servers keep their roots while reconnecting, so the form stays up (and filled) through a dropped socket
  const servers = useMemo(() => serverConnections.filter(c => c.roots.length > 0), [serverConnections]);

  const [pickedServerId, setSelectedServerId] = useState(createProjectContext?.serverId ?? draft?.serverId ?? '');
  const selectedServerId = pickedServerId || servers[0]?.serverInfo.Id || '';
  // Step 1: root picker, Step 2: project form
  const initialRootName = createProjectContext?.rootName ?? draft?.rootName ?? '';
  const [step, setStep] = useState<1 | 2>(initialRootName ? 2 : 1);
  const [selectedRootName, setSelectedRootName] = useState(initialRootName);
  const [pickedActionName, setSelectedActionName] = useState(draft?.actionName ?? '');
  const [selectedModel, setSelectedModel] = useState(draft?.model ?? 'opus');
  const [formValues, setFormValues] = useState<Record<string, string>>(draft?.values ?? {});
  // The form formValues were filled for; defaults are applied only when this changes
  const [valuesFor, setValuesFor] = useState(draft ? formKeyOf(draft.serverId, draft.rootName, draft.actionName) : '');
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const server = servers.find(c => c.serverInfo.Id === selectedServerId);
  const isConnected = server?.connectionState === 'connected';

  // Filter roots by active profile filter
  const roots = useMemo(() => {
    const allRoots = server?.roots ?? [];
    if (profileFilter === 'All') return allRoots;
    return allRoots.filter(r => (r.ProfileName ?? 'Default').toLowerCase() === profileFilter.toLowerCase());
  }, [server, profileFilter]);

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
  // Falls back to the root's first action; kept as picked while the root is unavailable
  const selectedActionName = actions.some(a => a.Name === pickedActionName) ? pickedActionName : actions[0]?.Name ?? pickedActionName;
  const selectedAction = actions.find(a => a.Name === selectedActionName) ?? null;
  const formFields = useMemo(() => selectedAction?.InputSchema ? parseFormFields(selectedAction.InputSchema) : [], [selectedAction]);

  // A different root or action resets the form to its defaults. Keyed by name, not by object:
  // every roots refresh (a ProfilesChanged broadcast, a reconnect) hands out new objects for the same form
  const formKey = formKeyOf(selectedServerId, selectedRootName, selectedActionName);
  if (selectedAction && valuesFor !== formKey) {
    const defaults: Record<string, string> = {};
    for (const field of formFields) defaults[field.key] = field.defaultValue ?? '';
    setValuesFor(formKey);
    setFormValues(defaults);
    if (selectedAction.Model) setSelectedModel(selectedAction.Model);
  }

  useEffect(() => {
    if (!selectedRootName || valuesFor !== formKey) return;
    writeDraft({ serverId: selectedServerId, rootName: selectedRootName, actionName: selectedActionName, model: selectedModel, values: formValues });
  }, [selectedServerId, selectedRootName, selectedActionName, selectedModel, formValues, valuesFor, formKey]);

  // Leaving the page (Back) discards the draft; a remount with the page still open keeps it
  useEffect(() => () => {
    if (useAppStore.getState().activePage?.type !== 'createProject') writeDraft(null);
  }, []);

  const setFieldValue = useCallback((key: string, value: string) => {
    setFormValues(prev => ({ ...prev, [key]: value }));
  }, []);

  const selectRoot = useCallback((rootName: string) => {
    setSelectedRootName(rootName);
    setStep(2);
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
    const profileName = selectedRoot.ProfileName ?? 'Default';
    const inputs: Record<string, unknown> = { model: selectedModel };
    for (const field of formFields) {
      const val = formValues[field.key];
      if (field.fieldType === 'boolean') inputs[field.key] = val === 'true';
      else if (val) inputs[field.key] = val;
    }
    try {
      // Only this client opens what it created; other clients just list it (#170)
      const created = await server.hub.createProject(profileName, selectedRoot.Name, selectedActionName || null, inputs);
      writeDraft(null);
      openCreatedProject(server.serverInfo.Id, created);
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Failed to create project';
      if (msg.includes('FOLDER_EXISTS:')) {
        // Project folder already exists — ask user what to do
        const choice = await askConfirm({
          title: 'A project with this name already exists',
          message: 'Reuse its folder, or create a new folder with a suffix (_2, _3, ...)?',
          choices: [
            { label: 'New folder', value: 'suffix', tone: 'secondary' },
            { label: 'Reuse folder', value: 'reuse' },
          ],
        });
        if (choice === null) return;
        try {
          if (choice === 'reuse') {
            inputs.__reuseExisting = true;
          } else {
            inputs.__autoSuffix = true;
          }
          const created = await server.hub.createProject(profileName, selectedRoot.Name, selectedActionName || null, inputs);
          writeDraft(null);
          openCreatedProject(server.serverInfo.Id, created);
        } catch (retryErr) {
          setError(retryErr instanceof Error ? retryErr.message : 'Failed to create project');
        }
      } else {
        setError(msg);
      }
    } finally {
      setCreating(false);
    }
  };

  // Auto-advance to step 2 if only one root
  useEffect(() => {
    if (step === 1 && roots.length === 1) selectRoot(roots[0].Name);
  }, [step, roots, selectRoot]);

  return (
    <>
      {step === 1 ? (
          <>
            <div className="settings-header"><h2>Choose Root</h2></div>

            {servers.length > 1 && (
              <div className="form-group">
                <label>Server</label>
                <select value={selectedServerId} onChange={e => setSelectedServerId(e.target.value)}>
                  {servers.map(c => (
                    <option key={c.serverInfo.Id} value={c.serverInfo.Id}>
                      {c.serverInfo.Name || c.serverInfo.Url}
                    </option>
                  ))}
                </select>
              </div>
            )}

            <div className="root-picker-grid">
              {Array.from(rootsByProfile.entries()).map(([profile, profileRoots]) => (
                <div key={profile}>
                  {rootsByProfile.size > 1 && <div className="root-picker-profile">{profile}</div>}
                  <div className="root-picker-cards">
                    {profileRoots.map(root => (
                      <button
                        key={root.Name}
                        className="root-picker-card"
                        onClick={() => selectRoot(root.Name)}
                      >
                        <div className="root-picker-card-name">{root.Name}</div>
                        {root.Description && <div className="root-picker-card-desc">{root.Description}</div>}
                        {root.Actions && root.Actions.length > 1 && (
                          <div className="root-picker-card-actions">
                            {root.Actions.map(a => <span key={a.Name} className="root-picker-action-tag">{a.Name}</span>)}
                          </div>
                        )}
                      </button>
                    ))}
                  </div>
                </div>
              ))}
            </div>

          </>
        ) : (
          <>
            <div className="settings-header"><h2>New Project</h2></div>

            <div className="form-group">
              <label>Root</label>
              <div className="selected-root-header">
                <span className="selected-root-name">{selectedRoot?.Name}</span>
                {roots.length > 1 && (
                  <button className="btn btn-secondary btn-sm" onClick={() => setStep(1)}>Change</button>
                )}
              </div>
            </div>

            {actions.length > 1 && (
              <div className="form-group">
                <label>Action</label>
                <div className="action-picker">
                  {actions.map(a => (
                    <button
                      key={a.Name}
                      className={`action-picker-btn ${selectedActionName === a.Name ? 'active' : ''}`}
                      onClick={() => setSelectedActionName(a.Name)}
                    >
                      <span className="action-picker-name">{a.Name}</span>
                      {a.Description && <span className="action-picker-desc">{a.Description}</span>}
                    </button>
                  ))}
                </div>
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

            {server && !isConnected && <div className="create-project-offline">Reconnecting to the server. Your input is kept.</div>}
            {error && <div className="form-error">{error}</div>}

            <div className="btn-group">
              <button className="btn btn-primary" onClick={handleCreate} disabled={creating || !selectedRoot || !isConnected}>
                {creating ? 'Creating...' : 'Create'}
              </button>
            </div>
          </>
        )}
    </>
  );
}
