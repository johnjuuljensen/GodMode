import { useState, useEffect, useMemo, useCallback } from 'react';
import { useAppStore, inProfile, profileNameOf, sameProfile, rootShownOf, type ActivePage } from '../../store';
import type { CreateProjectResult, ProjectRootInfo } from '../../signalr/types';
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
    } else if (prop['x-multiline'] === true || prop.format === 'multiline' || (prop.maxLength && (prop.maxLength as number) > 200)) {
      fields.push({ key, title, fieldType: 'multiline', isRequired: required.has(key), description, defaultValue });
    } else {
      fields.push({ key, title, fieldType: 'string', isRequired: required.has(key), description, defaultValue });
    }
  }

  return fields;
}

/** Claude Code's model aliases; a full model name (claude-fable-5, say) is typed in as Other. */
const MODEL_PRESETS = ['fable', 'opus', 'sonnet', 'haiku'];
/** The select's choice that turns it into a text field for a model the presets do not name. */
const OTHER_MODEL = '__other__';
/** Claude Code's --effort levels. The empty choice passes none: claude's own default. */
const EFFORT_LEVELS = ['low', 'medium', 'high', 'xhigh', 'max'];

/** The presets, with the action's model and the one chosen where they are not presets: the select always shows what will run. */
const modelOptions = (...shown: (string | null | undefined)[]) =>
  [...new Set([...MODEL_PRESETS, ...shown.filter((m): m is string => !!m)])];

/** What the user has entered in one root's form, kept in sessionStorage so a remount or a reload of a discarded tab restores it. */
interface CreateDraft {
  actionName: string;
  model: string;
  effort?: string;
  values: Record<string, string>;
}

/** The drafts by server, profile and root: each root's form keeps its own while the page is open (#240). */
const DRAFTS_STORAGE = 'godmode-create-drafts';
const draftKeyOf = (serverId: string, profileName: string, rootName: string) => `${serverId}
${profileName}
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
const formKeyOf = (serverId: string, profileName: string, rootName: string, actionName: string) => `${serverId}
${profileName}
${rootName}
${actionName}`;

/** A root's profile as the server lists it: a root that names none is in Default. */
const profileOf = (root: ProjectRootInfo) => profileNameOf(root.ProfileName);

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

  const [draft] = useState(() => (context && readDrafts()[draftKeyOf(context.serverId, context.profileName, context.rootName)]) ?? null);

  // Servers keep their roots while reconnecting, so the form stays up (and filled) through a dropped socket.
  // Only those with a root in the profile filtered to (the window's, in a locked one: #340)
  const servers = useMemo(
    () => serverConnections.filter(c => c.roots.some(r => inProfile(r.ProfileName, profileFilter))),
    [serverConnections, profileFilter]);

  // Pinned once known: a server that finishes connecting later must not take the form over (#240)
  const [selectedServerId, setSelectedServerId] = useState(context?.serverId ?? '');
  if (!selectedServerId && servers.length > 0) setSelectedServerId(servers[0].serverInfo.Id);
  // Step 1: root picker, Step 2: project form
  const [step, setStep] = useState<1 | 2>(context ? 2 : 1);
  const [selectedProfileName, setSelectedProfileName] = useState(context?.profileName ?? '');
  const [selectedRootName, setSelectedRootName] = useState(context?.rootName ?? '');
  const [pickedActionName, setSelectedActionName] = useState(draft?.actionName ?? '');
  const [selectedModel, setSelectedModel] = useState(draft?.model ?? 'opus');
  // Typing a model the presets do not name, in place of the select
  const [typingModel, setTypingModel] = useState(false);
  const [selectedEffort, setSelectedEffort] = useState(draft?.effort ?? '');
  const [formValues, setFormValues] = useState<Record<string, string>>(draft?.values ?? {});
  // The form formValues were filled for; defaults are applied only when this changes
  const [valuesFor, setValuesFor] = useState(context && draft ? formKeyOf(context.serverId, context.profileName, context.rootName, draft.actionName) : '');
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // What the last run of an action that starts no session said it made: the page stays, and shows it
  const [finished, setFinished] = useState<string | null>(null);

  // The pinned server, also while it lists no roots (a disconnect), so the form says where it creates
  const server = serverConnections.find(c => c.serverInfo.Id === selectedServerId);
  const isConnected = server?.connectionState === 'connected';

  // Filter roots by active profile filter
  const roots = useMemo(() => {
    return (server?.roots ?? []).filter(r => inProfile(r.ProfileName, profileFilter));
  }, [server, profileFilter]);

  const rootsByProfile = useMemo(() => {
    const groups = new Map<string, ProjectRootInfo[]>();
    for (const root of roots) {
      const profile = profileOf(root);
      if (!groups.has(profile)) groups.set(profile, []);
      groups.get(profile)!.push(root);
    }
    return groups;
  }, [roots]);

  // The root of the profile picked: two profiles may each have a root of one name
  const selectedRoot = roots.find(r => r.Name === selectedRootName && sameProfile(profileOf(r), selectedProfileName));
  const actions = selectedRoot?.Actions ?? [];
  // Falls back to the root's first action; kept as picked while the root is unavailable
  const selectedActionName = actions.some(a => a.Name === pickedActionName) ? pickedActionName : actions[0]?.Name ?? pickedActionName;
  const selectedAction = actions.find(a => a.Name === selectedActionName) ?? null;
  // An action that starts no session only runs its scripts: no model, and nothing to open when it is done
  const startsSession = selectedAction?.Session ?? true;
  const formFields = useMemo(
    () => selectedAction?.InputSchema ? parseFormFields(selectedAction.InputSchema, selectedAction.AllowSkipPermissions) : [],
    [selectedAction]);

  // A different root or action resets the form to its defaults. Keyed by name, not by object:
  // every roots refresh (a reconnect, say) hands out new objects for the same form
  const formKey = formKeyOf(selectedServerId, selectedProfileName, selectedRootName, selectedActionName);
  if (selectedAction && valuesFor !== formKey) {
    const defaults: Record<string, string> = {};
    for (const field of formFields) defaults[field.key] = field.defaultValue ?? '';
    setValuesFor(formKey);
    setFormValues(defaults);
    if (selectedAction.Model) setSelectedModel(selectedAction.Model);
    setTypingModel(false);
    setSelectedEffort(selectedAction.Effort ?? '');
  }

  useEffect(() => {
    if (!selectedRootName || valuesFor !== formKey) return;
    writeDraft(draftKeyOf(selectedServerId, selectedProfileName, selectedRootName), { actionName: selectedActionName, model: selectedModel, effort: selectedEffort, values: formValues });
  }, [selectedServerId, selectedProfileName, selectedRootName, selectedActionName, selectedModel, selectedEffort, formValues, valuesFor, formKey]);

  // Leaving the page (Back) discards the drafts; a remount with the page still open (another root's form) keeps them
  useEffect(() => () => {
    if (useAppStore.getState().activePage?.type !== 'createProject') discardDrafts();
  }, []);

  const setFieldValue = useCallback((key: string, value: string) => {
    setFormValues(prev => ({ ...prev, [key]: value }));
  }, []);

  // The route names the root picked, so a reload restores its form; a root other than the route's is another form
  const selectRoot = useCallback((root: ProjectRootInfo) => {
    const profileName = profileOf(root);
    setSelectedProfileName(profileName);
    setSelectedRootName(root.Name);
    setStep(2);
    setActivePage({ type: 'createProject', context: { serverId: selectedServerId, profileName, rootName: root.Name } });
  }, [selectedServerId, setActivePage]);

  /**
   * Opens the project a create made. An action that starts no session made none: the page stays where
   * it is and says what the script made, with the form back to its defaults so it is not run twice by accident.
   */
  const finish = (result: CreateProjectResult) => {
    discardDrafts();
    if (result.Project) {
      openCreatedProject(selectedServerId, result.Project);
      return;
    }
    setFinished(result.Message || `${selectedActionName} finished.`);
    setValuesFor('');
  };

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
    setFinished(null);
    const profileName = profileOf(selectedRoot);
    const inputs: Record<string, unknown> = startsSession ? { model: selectedModel.trim() } : {};
    // The empty choice is sent where the action has a level, so claude's default overrides it
    if (startsSession && (selectedEffort || selectedAction?.Effort)) inputs.effort = selectedEffort;
    for (const field of formFields) {
      const val = formValues[field.key];
      if (field.fieldType === 'boolean') inputs[field.key] = val === 'true';
      else if (val) inputs[field.key] = val;
    }
    try {
      // Only this client opens what it created; other clients just list it (#170)
      finish(await server.hub.createProject(profileName, selectedRoot.Name, selectedActionName || null, inputs));
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
          finish(await server.hub.createProject(profileName, selectedRoot.Name, selectedActionName || null, inputs));
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
    if (step === 1 && roots.length === 1) selectRoot(roots[0]);
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
                        onClick={() => selectRoot(root)}
                      >
                        <div className="root-picker-card-name" title={root.Title != null ? root.Name : undefined}>{rootShownOf(root)}</div>
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
                <span className="selected-root-name" title={selectedRoot?.Title != null ? selectedRoot.Name : undefined}>{selectedRoot ? rootShownOf(selectedRoot) : selectedRootName}</span>
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
                      onClick={() => { setSelectedActionName(a.Name); setFinished(null); }}
                    >
                      <span className="action-picker-name">{a.Name}</span>
                      {a.Description && <span className="action-picker-desc">{a.Description}</span>}
                    </button>
                  ))}
                </div>
              </div>
            )}

            {startsSession && (
              <div className="form-group">
                <label>Model</label>
                {typingModel ? (
                  <input type="text" aria-label="Model name" placeholder="claude-fable-5" value={selectedModel}
                    onChange={e => setSelectedModel(e.target.value)} autoFocus
                    // Back to the select, which then lists the name typed; an empty one is the action's model again
                    onBlur={() => { setSelectedModel(m => m.trim() || selectedAction?.Model || 'opus'); setTypingModel(false); }} />
                ) : (
                  <select value={selectedModel} onChange={e => e.target.value === OTHER_MODEL ? setTypingModel(true) : setSelectedModel(e.target.value)}>
                    {modelOptions(selectedAction?.Model, selectedModel).map(m => <option key={m} value={m}>{m}</option>)}
                    <option value={OTHER_MODEL}>Other…</option>
                  </select>
                )}
              </div>
            )}

            {startsSession && (
              <div className="form-group">
                <label>Effort</label>
                <select value={selectedEffort} onChange={e => setSelectedEffort(e.target.value)}>
                  <option value="">default</option>
                  {EFFORT_LEVELS.map(level => <option key={level} value={level}>{level}</option>)}
                </select>
              </div>
            )}

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
            {finished && <div className="form-success" role="status">{finished}</div>}

            <div className="btn-group">
              <button className="btn btn-primary" onClick={handleCreate} disabled={creating || !selectedRoot || !isConnected}>
                {startsSession ? (creating ? 'Creating...' : 'Create') : (creating ? 'Running...' : 'Run')}
              </button>
            </div>
          </>
        )}
    </>
  );
}
