import { useState, useEffect, useMemo, useCallback } from 'react';
import { useAppStore, type ActivePage } from '../../store';
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

/** The input the server starts claude with --dangerously-skip-permissions for, where the root allows it. */
const SKIP_PERMISSIONS = 'skipPermissions';

function parseFormFields(schema: unknown, allowSkipPermissions: boolean): FormField[] {
  if (!schema || typeof schema !== 'object') return [];
  const s = schema as Record<string, unknown>;
  const properties = s.properties as Record<string, Record<string, unknown>> | undefined;
  if (!properties) return [];

  const required = new Set<string>(Array.isArray(s.required) ? s.required as string[] : []);
  const fields: FormField[] = [];

  for (const [key, prop] of Object.entries(properties)) {
    // Offered only where the root allows it, which the server checks too, and never on by default (#233)
    if (key === SKIP_PERMISSIONS && !allowSkipPermissions) continue;
    const type = prop.type as string;
    const title = (prop.title as string) || key;
    const description = prop.description as string | undefined;
    const defaultValue = prop.default != null && key !== SKIP_PERMISSIONS ? String(prop.default) : undefined;

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

/** What the user has entered in one root's form, kept in sessionStorage so a remount or a reload of a discarded tab restores it. */
interface CreateDraft {
  actionName: string;
  model: string;
  values: Record<string, string>;
}

/** The drafts by server and root: each root's form keeps its own while the page is open (#240). */
const DRAFTS_STORAGE = 'godmode-create-drafts';
const draftKeyOf = (serverId: string, rootName: string) => `${serverId}
${rootName}`;

function readDrafts(): Record<string, CreateDraft> {
  try {
    return JSON.parse(sessionStorage.getItem(DRAFTS_STORAGE) ?? '{}') as Record<string, CreateDraft>;
  } catch {
    return {};
  }
}

function writeDraft(key: string, draft: CreateDraft) {
  try {
    sessionStorage.setItem(DRAFTS_STORAGE, JSON.stringify({ ...readDrafts(), [key]: draft }));
  } catch {
    // Storage unavailable (private window, blocked site data): the draft only lives in component state
  }
}

function discardDrafts() {
  try {
    sessionStorage.removeItem(DRAFTS_STORAGE);
  } catch {
    // Nothing was kept
  }
}

/** Identifies the form the values belong to, by name, so a refreshed roots list does not count as a new form. */
const formKeyOf = (serverId: string, rootName: string, actionName: string) => `${serverId}
${rootName}
${actionName}`;

type CreateContext = Extract<ActivePage, { type: 'createProject' }>['context'];

/**
 * The form for the root the route names (context), or the root picker when it names none. The Shell
 * mounts one per route, so each root's form starts from its own draft.
 */
export function CreateProject({ context }: { context?: CreateContext }) {
  const serverConnections = useAppStore(s => s.serverConnections);
  const openCreatedProject = useAppStore(s => s.openCreatedProject);
  const setActivePage = useAppStore(s => s.setActivePage);
  const profileFilter = useAppStore(s => s.profileFilter);

  const [draft] = useState(() => (context && readDrafts()[draftKeyOf(context.serverId, context.rootName)]) ?? null);

  // Servers keep their roots while reconnecting, so the form stays up (and filled) through a dropped socket
  const servers = useMemo(() => serverConnections.filter(c => c.roots.length > 0), [serverConnections]);

  // Pinned once known: a server that finishes connecting later must not take the form over (#240)
  const [selectedServerId, setSelectedServerId] = useState(context?.serverId ?? '');
  if (!selectedServerId && servers.length > 0) setSelectedServerId(servers[0].serverInfo.Id);
  // Step 1: root picker, Step 2: project form
  const [step, setStep] = useState<1 | 2>(context ? 2 : 1);
  const [selectedRootName, setSelectedRootName] = useState(context?.rootName ?? '');
  const [pickedActionName, setSelectedActionName] = useState(draft?.actionName ?? '');
  const [selectedModel, setSelectedModel] = useState(draft?.model ?? 'opus');
  const [formValues, setFormValues] = useState<Record<string, string>>(draft?.values ?? {});
  // The form formValues were filled for; defaults are applied only when this changes
  const [valuesFor, setValuesFor] = useState(context && draft ? formKeyOf(context.serverId, context.rootName, draft.actionName) : '');
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // The pinned server, also while it lists no roots (a disconnect), so the form says where it creates
  const server = serverConnections.find(c => c.serverInfo.Id === selectedServerId);
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
  const formFields = useMemo(
    () => selectedAction?.InputSchema ? parseFormFields(selectedAction.InputSchema, selectedAction.AllowSkipPermissions) : [],
    [selectedAction]);

  // A different root or action resets the form to its defaults. Keyed by name, not by object:
  // every roots refresh (a reconnect, say) hands out new objects for the same form
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
    writeDraft(draftKeyOf(selectedServerId, selectedRootName), { actionName: selectedActionName, model: selectedModel, values: formValues });
  }, [selectedServerId, selectedRootName, selectedActionName, selectedModel, formValues, valuesFor, formKey]);

  // Leaving the page (Back) discards the drafts; a remount with the page still open (another root's form) keeps them
  useEffect(() => () => {
    if (useAppStore.getState().activePage?.type !== 'createProject') discardDrafts();
  }, []);

  const setFieldValue = useCallback((key: string, value: string) => {
    setFormValues(prev => ({ ...prev, [key]: value }));
  }, []);

  // The route names the root picked, so a reload restores its form; a root other than the route's is another form
  const selectRoot = useCallback((rootName: string) => {
    setSelectedRootName(rootName);
    setStep(2);
    setActivePage({ type: 'createProject', context: { serverId: selectedServerId, rootName } });
  }, [selectedServerId, setActivePage]);

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
      discardDrafts();
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
          discardDrafts();
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
                <span className="selected-root-name">{selectedRoot?.Name ?? selectedRootName}</span>
                <span className="selected-root-server">on {server?.serverInfo.Name || server?.serverInfo.Url || selectedServerId}</span>
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
