import { useState, useCallback } from 'react';
import { useAppStore } from '../../store';
import type { SharedRootPreview } from '../../signalr/types';
import { IconBtn, ICON_EDIT, ICON_COPY, RowDelete } from '../settings-shared';
import '../settings-common.css';
import './RootManager.css';

const refresh = () => useAppStore.getState().refreshFirstConnected();

const BackArrow = () => (
  <svg width={16} height={16} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round"><path d="M10 4L6 8l4 4"/></svg>
);

type View = 'list' | 'create' | 'import-git' | 'import-zip' | 'edit';

export function RootManager() {
  const serverConnections = useAppStore(s => s.serverConnections);
  const profileFilter = useAppStore(s => s.profileFilter);

  const conn = serverConnections.find(c => c.connectionState === 'connected');
  const hub = conn?.hub;
  const allRoots = conn?.roots ?? [];
  const profiles = conn?.profiles ?? [];

  const roots = profileFilter !== 'All'
    ? allRoots.filter(r => (r.ProfileName ?? 'Default').toLowerCase() === profileFilter.toLowerCase())
    : allRoots;

  const [view, setView] = useState<View>('list');
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [statusMsg, setStatusMsg] = useState<string | null>(null);

  // ── Create form (structured) ──
  const [newRootName, setNewRootName] = useState('');
  const [newRootProfile, setNewRootProfile] = useState('');
  const [newDescription, setNewDescription] = useState('');
  const [newNameTemplate, setNewNameTemplate] = useState('{name}');
  const [newPromptTemplate, setNewPromptTemplate] = useState('{prompt}');
  const [newSchemaFields, setNewSchemaFields] = useState<{ key: string; title: string; type: string; multiline: boolean; required: boolean }[]>([
    { key: 'name', title: 'Project Name', type: 'string', multiline: false, required: true },
    { key: 'prompt', title: 'Task Description', type: 'string', multiline: true, required: true },
  ]);
  const [newPrepareScript, setNewPrepareScript] = useState('');
  const [newCreateScript, setNewCreateScript] = useState('');
  const [newDeleteScript, setNewDeleteScript] = useState('');
  const [newEnvVars, setNewEnvVars] = useState('');
  const [newClaudeArgs, setNewClaudeArgs] = useState('');

  // Actions — each action overrides the base config
  interface ActionDef {
    name: string;
    nameTemplate: string;
    promptTemplate: string;
    schemaFields: typeof newSchemaFields;
    createScript: string;
  }
  const [newActions, setNewActions] = useState<ActionDef[]>([]);

  // ── Edit form ──
  const [editRootName, setEditRootName] = useState('');
  const [editRootProfile, setEditRootProfile] = useState('');
  const [editConfigJson, setEditConfigJson] = useState('');

  // ── Import Git form ──
  const [gitUrl, setGitUrl] = useState('');
  const [gitPath, setGitPath] = useState('');
  const [gitRef, setGitRef] = useState('');
  const [importPreview, setImportPreview] = useState<SharedRootPreview | null>(null);
  const [importRootName, setImportRootName] = useState('');

  // ── Import ZIP ──
  const [zipPreview, setZipPreview] = useState<SharedRootPreview | null>(null);
  const [zipRootName, setZipRootName] = useState('');

  const goList = () => { setView('list'); setError(null); setStatusMsg(null); };

  // ── Create handler ──
  const handleCreate = async () => {
    if (!hub || !newRootName.trim()) return;
    setError(null);
    setLoading(true);
    try {
      const config: Record<string, unknown> = {
        description: newDescription || undefined,
        nameTemplate: newNameTemplate || '{name}',
        promptTemplate: newPromptTemplate || '{prompt}',
      };
      if (newRootProfile) config.profileName = newRootProfile;
      if (newEnvVars.trim()) {
        try { config.environment = JSON.parse(newEnvVars); } catch { /* skip */ }
      }
      if (newClaudeArgs.trim()) {
        config.claudeArgs = newClaudeArgs.split(',').map(s => s.trim()).filter(Boolean);
      }
      if (newPrepareScript.trim()) config.prepare = 'scripts/prepare.sh';
      if (newCreateScript.trim()) config.create = 'scripts/create.sh';
      if (newDeleteScript.trim()) config.delete = 'scripts/delete.sh';

      // Build schema.json
      const schemaProps: Record<string, unknown> = {};
      const required: string[] = [];
      for (const f of newSchemaFields) {
        if (!f.key.trim()) continue;
        const prop: Record<string, unknown> = { type: f.type || 'string', title: f.title || f.key };
        if (f.multiline) prop['x-multiline'] = true;
        if (f.type === 'boolean') prop.default = 'false';
        schemaProps[f.key] = prop;
        if (f.required) required.push(f.key);
      }
      const schema = { type: 'object', properties: schemaProps, required };

      // Build files
      const files: Record<string, string> = {
        'config.json': JSON.stringify(config, null, 2),
        'schema.json': JSON.stringify(schema, null, 2),
      };
      if (newPrepareScript.trim()) files['scripts/prepare.sh'] = newPrepareScript;
      if (newCreateScript.trim()) files['scripts/create.sh'] = newCreateScript;
      if (newDeleteScript.trim()) files['scripts/delete.sh'] = newDeleteScript;

      // Generate action overlay files
      for (const action of newActions) {
        if (!action.name.trim()) continue;
        const actionConfig: Record<string, unknown> = {};
        if (action.nameTemplate && action.nameTemplate !== newNameTemplate) actionConfig.nameTemplate = action.nameTemplate;
        if (action.promptTemplate && action.promptTemplate !== newPromptTemplate) actionConfig.promptTemplate = action.promptTemplate;
        if (action.createScript.trim()) {
          actionConfig.create = `${action.name}/create.sh`;
          files[`${action.name}/create.sh`] = action.createScript;
        }
        files[`config.${action.name}.json`] = JSON.stringify(actionConfig, null, 2);

        // Per-action schema
        if (action.schemaFields.length > 0) {
          const aSchemaProps: Record<string, unknown> = {};
          const aRequired: string[] = [];
          for (const f of action.schemaFields) {
            if (!f.key.trim()) continue;
            const prop: Record<string, unknown> = { type: f.type || 'string', title: f.title || f.key };
            if (f.multiline) prop['x-multiline'] = true;
            if (f.type === 'boolean') prop.default = 'false';
            aSchemaProps[f.key] = prop;
            if (f.required) aRequired.push(f.key);
          }
          files[`${action.name}/schema.json`] = JSON.stringify({ type: 'object', properties: aSchemaProps, required: aRequired }, null, 2);
        }
      }

      await hub.createRoot(newRootName.trim(), { Files: files });
      await refresh();
      goList();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create root');
    } finally {
      setLoading(false);
    }
  };

  // ── Edit handlers ──
  const handleEdit = async (profileName: string, rootName: string) => {
    if (!hub) return;
    setError(null);
    setLoading(true);
    try {
      const preview = await hub.getRootPreview(profileName, rootName);
      if (!preview) { setError('Could not read root'); setLoading(false); return; }
      setEditRootName(rootName);
      setEditRootProfile(profileName);
      setEditConfigJson(preview.Files['config.json'] ?? '{}');
      setView('edit');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load root');
    } finally {
      setLoading(false);
    }
  };

  const handleSaveEdit = async () => {
    if (!hub || !editRootName) return;
    setError(null);
    setLoading(true);
    try {
      const preview = await hub.getRootPreview(editRootProfile, editRootName);
      if (!preview) { setError('Could not read root'); setLoading(false); return; }
      preview.Files['config.json'] = editConfigJson;
      await hub.updateRoot(editRootProfile, editRootName, preview);
      await refresh();
      goList();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to save root');
    } finally {
      setLoading(false);
    }
  };

  const handleChangeProfile = async (rootProfileName: string, rootName: string, newProfile: string) => {
    if (!hub) return;
    setError(null);
    try {
      const preview = await hub.getRootPreview(rootProfileName, rootName);
      if (!preview) return;
      const configStr = preview.Files['config.json'];
      if (configStr) {
        try {
          const parsed = JSON.parse(configStr);
          if (newProfile) parsed.profileName = newProfile;
          else delete parsed.profileName;
          preview.Files['config.json'] = JSON.stringify(parsed, null, 2);
        } catch { /* skip */ }
      }
      await hub.updateRoot(rootProfileName, rootName, preview);
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to change profile');
    }
  };

  const handleDelete = async (profileName: string, rootName: string) => {
    if (!hub) return;
    setError(null);
    try {
      await hub.deleteRoot(profileName, rootName, true);
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to delete root');
    }
  };

  const handleCopy = async (profileName: string, rootName: string) => {
    if (!hub) return;
    setError(null);
    try {
      const preview = await hub.getRootPreview(profileName, rootName);
      if (!preview) { setError('Could not read root'); return; }
      await hub.createRoot(`${rootName}-copy`, preview, profileName);
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to copy root');
    }
  };

  // ── Git import ──
  const handlePreviewGit = useCallback(async () => {
    if (!hub || !gitUrl.trim()) return;
    setError(null);
    setLoading(true);
    setStatusMsg('Cloning repository...');
    try {
      const preview = await hub.previewImportFromGit(gitUrl.trim(), gitPath.trim() || null, gitRef.trim() || null);
      setImportPreview(preview);
      setImportRootName(preview.Manifest.Name);
      setStatusMsg(`Found ${Object.keys(preview.Preview.Files).length} files`);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to preview import');
      setStatusMsg(null);
    } finally {
      setLoading(false);
    }
  }, [hub, gitUrl, gitPath, gitRef]);

  const handleInstallGit = async () => {
    if (!hub || !importPreview || !importRootName.trim()) return;
    setError(null);
    setLoading(true);
    setStatusMsg('Installing root...');
    try {
      await hub.installSharedRoot(importRootName.trim(), importPreview);
      await refresh();
      setStatusMsg('Installed successfully!');
      setTimeout(goList, 800);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to install root');
      setStatusMsg(null);
    } finally {
      setLoading(false);
    }
  };

  // ── ZIP import ──
  const handleZipUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!hub || !file) return;
    setError(null);
    setLoading(true);
    setStatusMsg('Reading ZIP file...');
    try {
      const buffer = await file.arrayBuffer();
      const bytes = new Uint8Array(buffer);
      setStatusMsg('Previewing contents...');
      const preview = await hub.previewImportFromBytes(bytes);
      setZipPreview(preview);
      setZipRootName(preview.Manifest.Name || file.name.replace(/\.zip$/i, '').replace(/\.gmroot$/i, ''));
      setStatusMsg(`Found ${Object.keys(preview.Preview.Files).length} files`);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to read ZIP');
      setStatusMsg(null);
    } finally {
      setLoading(false);
      e.target.value = '';
    }
  };

  const handleInstallZip = async () => {
    if (!hub || !zipPreview || !zipRootName.trim()) return;
    setError(null);
    setLoading(true);
    setStatusMsg('Installing root...');
    try {
      await hub.installSharedRoot(zipRootName.trim(), zipPreview);
      await refresh();
      setStatusMsg('Installed successfully!');
      setTimeout(goList, 800);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to install root');
      setStatusMsg(null);
    } finally {
      setLoading(false);
    }
  };

  // ── Schema field helpers ──
  const addSchemaField = () => setNewSchemaFields(prev => [...prev, { key: '', title: '', type: 'string', multiline: false, required: false }]);
  const removeSchemaField = (i: number) => setNewSchemaFields(prev => prev.filter((_, idx) => idx !== i));
  const updateSchemaField = (i: number, patch: Partial<typeof newSchemaFields[0]>) =>
    setNewSchemaFields(prev => prev.map((f, idx) => idx === i ? { ...f, ...patch } : f));

  // ── Action helpers ──
  const addAction = () => setNewActions(prev => [...prev, {
    name: '', nameTemplate: '', promptTemplate: '',
    schemaFields: [{ key: 'name', title: 'Project Name', type: 'string', multiline: false, required: true }, { key: 'prompt', title: 'Task', type: 'string', multiline: true, required: true }],
    createScript: '',
  }]);
  const removeAction = (i: number) => setNewActions(prev => prev.filter((_, idx) => idx !== i));
  const updateAction = (i: number, patch: Partial<ActionDef>) =>
    setNewActions(prev => prev.map((a, idx) => idx === i ? { ...a, ...patch } : a));
  const updateActionSchemaField = (ai: number, fi: number, patch: Partial<typeof newSchemaFields[0]>) =>
    setNewActions(prev => prev.map((a, idx) => idx === ai ? { ...a, schemaFields: a.schemaFields.map((f, fidx) => fidx === fi ? { ...f, ...patch } : f) } : a));
  const addActionSchemaField = (ai: number) =>
    setNewActions(prev => prev.map((a, idx) => idx === ai ? { ...a, schemaFields: [...a.schemaFields, { key: '', title: '', type: 'string', multiline: false, required: false }] } : a));
  const removeActionSchemaField = (ai: number, fi: number) =>
    setNewActions(prev => prev.map((a, idx) => idx === ai ? { ...a, schemaFields: a.schemaFields.filter((_, fidx) => fidx !== fi) } : a));

  // ── Create view (structured form) ──
  if (view === 'create') {
    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={goList}><BackArrow /> Roots</button>
        </div>
        <div className="settings-header"><h2>New Root</h2></div>
        {error && <div className="settings-error">{error}</div>}

        <div className="form-group">
          <label>Root Name</label>
          <input type="text" value={newRootName} onChange={e => setNewRootName(e.target.value)} placeholder="my-root" autoFocus />
        </div>

        <div className="form-group">
          <label>Profile</label>
          <select value={newRootProfile} onChange={e => setNewRootProfile(e.target.value)}>
            <option value="">All profiles</option>
            {profiles.map(p => <option key={p.Name} value={p.Name}>{p.Name}</option>)}
          </select>
        </div>

        <div className="form-group">
          <label>Description</label>
          <input type="text" value={newDescription} onChange={e => setNewDescription(e.target.value)} placeholder="What this root does" />
        </div>

        <div className="form-group">
          <label>Name Template</label>
          <input type="text" value={newNameTemplate} onChange={e => setNewNameTemplate(e.target.value)} placeholder="{name}" />
          <div className="form-description">
            Controls the project folder name. Use <code>{'{fieldName}'}</code> to insert form field values.
            E.g. <code>jira-{'{ticketId}'}</code> → folder "jira-PROJ-123" when the user enters PROJ-123.
          </div>
        </div>

        <div className="form-group">
          <label>Prompt Template</label>
          <textarea value={newPromptTemplate} onChange={e => setNewPromptTemplate(e.target.value)} placeholder="{prompt}" rows={3} />
          <div className="form-description">
            The initial instruction sent to Claude when a project starts. Use <code>{'{fieldName}'}</code> to insert form field values.
            E.g. <code>Fix Jira ticket {'{ticketId}'}: {'{description}'}</code>
          </div>
        </div>

        <div className="root-actions-section">
          <div className="root-actions-header">
            <label>Actions</label>
            <button className="btn btn-secondary btn-sm" onClick={addAction}>+ Add Action</button>
          </div>
          {newActions.length === 0 ? (
            <>
              <div className="form-description" style={{ marginBottom: 10 }}>
                This root has one default action. Add more actions to give users different ways to create projects (e.g. "freeform" vs "from ticket"). Each action gets its own form, prompt, and scripts.
              </div>
              <div className="form-group">
                <label>Input Form</label>
                <div className="root-schema-fields">
                  {newSchemaFields.map((f, i) => (
                    <div key={i} className="root-schema-field">
                      <input type="text" placeholder="key" value={f.key} onChange={e => updateSchemaField(i, { key: e.target.value })} style={{ width: 80 }} />
                      <input type="text" placeholder="Label" value={f.title} onChange={e => updateSchemaField(i, { title: e.target.value })} style={{ flex: 1 }} />
                      <select value={f.type} onChange={e => updateSchemaField(i, { type: e.target.value })} style={{ width: 80 }}>
                        <option value="string">text</option>
                        <option value="boolean">toggle</option>
                      </select>
                      <label className="root-schema-check"><input type="checkbox" checked={f.multiline} onChange={e => updateSchemaField(i, { multiline: e.target.checked })} /><span>multi</span></label>
                      <label className="root-schema-check"><input type="checkbox" checked={f.required} onChange={e => updateSchemaField(i, { required: e.target.checked })} /><span>req</span></label>
                      <button className="root-schema-remove" onClick={() => removeSchemaField(i)}>x</button>
                    </div>
                  ))}
                  <button className="btn btn-secondary btn-sm" onClick={addSchemaField} style={{ alignSelf: 'flex-start' }}>+ Add Field</button>
                </div>
              </div>
            </>
          ) : (
            <>
              <div className="form-description" style={{ marginBottom: 10 }}>
                Each action has its own input form. Users pick an action when creating a project.
              </div>
              <details className="root-action-card" open>
                <summary><span>Default Action</span></summary>
                <div className="root-action-body">
                  <div className="form-group">
                    <label>Input Form</label>
                    <div className="root-schema-fields">
                      {newSchemaFields.map((f, i) => (
                        <div key={i} className="root-schema-field">
                          <input type="text" placeholder="key" value={f.key} onChange={e => updateSchemaField(i, { key: e.target.value })} style={{ width: 80 }} />
                          <input type="text" placeholder="Label" value={f.title} onChange={e => updateSchemaField(i, { title: e.target.value })} style={{ flex: 1 }} />
                          <select value={f.type} onChange={e => updateSchemaField(i, { type: e.target.value })} style={{ width: 80 }}>
                            <option value="string">text</option>
                            <option value="boolean">toggle</option>
                          </select>
                          <label className="root-schema-check"><input type="checkbox" checked={f.multiline} onChange={e => updateSchemaField(i, { multiline: e.target.checked })} /><span>multi</span></label>
                          <label className="root-schema-check"><input type="checkbox" checked={f.required} onChange={e => updateSchemaField(i, { required: e.target.checked })} /><span>req</span></label>
                          <button className="root-schema-remove" onClick={() => removeSchemaField(i)}>x</button>
                        </div>
                      ))}
                      <button className="btn btn-secondary btn-sm" onClick={addSchemaField} style={{ alignSelf: 'flex-start' }}>+ Field</button>
                    </div>
                  </div>
                </div>
              </details>
            </>
          )}
          {newActions.map((action, ai) => (
            <details key={ai} className="root-action-card" open>
              <summary>
                <span>{action.name || `Action ${ai + 1}`}</span>
                <button className="root-schema-remove" onClick={(e) => { e.preventDefault(); removeAction(ai); }}>remove</button>
              </summary>
              <div className="root-action-body">
                <div className="form-group">
                  <label>Action Name</label>
                  <input type="text" value={action.name} onChange={e => updateAction(ai, { name: e.target.value.toLowerCase().replace(/\s+/g, '-') })} placeholder="e.g. issue, freeform, deploy" />
                  <div className="form-description">Creates <code>config.{action.name || '...'}.json</code> overlay</div>
                </div>
                <div className="form-group">
                  <label>Name Template (override)</label>
                  <input type="text" value={action.nameTemplate} onChange={e => updateAction(ai, { nameTemplate: e.target.value })} placeholder="Leave empty to use base" />
                </div>
                <div className="form-group">
                  <label>Prompt Template (override)</label>
                  <textarea value={action.promptTemplate} onChange={e => updateAction(ai, { promptTemplate: e.target.value })} placeholder="Leave empty to use base" rows={2} />
                </div>
                <div className="form-group">
                  <label>Form Fields</label>
                  <div className="root-schema-fields">
                    {action.schemaFields.map((f, fi) => (
                      <div key={fi} className="root-schema-field">
                        <input type="text" placeholder="key" value={f.key} onChange={e => updateActionSchemaField(ai, fi, { key: e.target.value })} style={{ width: 80 }} />
                        <input type="text" placeholder="Label" value={f.title} onChange={e => updateActionSchemaField(ai, fi, { title: e.target.value })} style={{ flex: 1 }} />
                        <select value={f.type} onChange={e => updateActionSchemaField(ai, fi, { type: e.target.value })} style={{ width: 80 }}>
                          <option value="string">text</option>
                          <option value="boolean">toggle</option>
                        </select>
                        <label className="root-schema-check"><input type="checkbox" checked={f.multiline} onChange={e => updateActionSchemaField(ai, fi, { multiline: e.target.checked })} /><span>multi</span></label>
                        <label className="root-schema-check"><input type="checkbox" checked={f.required} onChange={e => updateActionSchemaField(ai, fi, { required: e.target.checked })} /><span>req</span></label>
                        <button className="root-schema-remove" onClick={() => removeActionSchemaField(ai, fi)}>x</button>
                      </div>
                    ))}
                    <button className="btn btn-secondary btn-sm" onClick={() => addActionSchemaField(ai)} style={{ alignSelf: 'flex-start' }}>+ Field</button>
                  </div>
                </div>
                <div className="form-group">
                  <label>Create Script (optional)</label>
                  <textarea value={action.createScript} onChange={e => updateAction(ai, { createScript: e.target.value })} rows={3} placeholder="#!/bin/bash" />
                </div>
              </div>
            </details>
          ))}
        </div>

        <details className="root-scripts-section">
          <summary>Scripts (optional)</summary>
          <div className="form-group">
            <label>Prepare Script (scripts/prepare.sh)</label>
            <textarea value={newPrepareScript} onChange={e => setNewPrepareScript(e.target.value)} rows={4} placeholder="#!/bin/bash&#10;# Runs before project creation" />
          </div>
          <div className="form-group">
            <label>Create Script (scripts/create.sh)</label>
            <textarea value={newCreateScript} onChange={e => setNewCreateScript(e.target.value)} rows={4} placeholder="#!/bin/bash&#10;# Runs during project creation" />
          </div>
          <div className="form-group">
            <label>Delete Script (scripts/delete.sh)</label>
            <textarea value={newDeleteScript} onChange={e => setNewDeleteScript(e.target.value)} rows={4} placeholder="#!/bin/bash&#10;# Cleanup when project is deleted" />
          </div>
        </details>

        <details className="root-scripts-section">
          <summary>Advanced (optional)</summary>
          <div className="form-group">
            <label>Environment Variables (JSON)</label>
            <textarea value={newEnvVars} onChange={e => setNewEnvVars(e.target.value)} rows={3} placeholder='{"KEY": "value"}' />
          </div>
          <div className="form-group">
            <label>Claude Args (comma-separated)</label>
            <input type="text" value={newClaudeArgs} onChange={e => setNewClaudeArgs(e.target.value)} placeholder="--model, sonnet" />
          </div>
        </details>

        <div className="settings-form-actions">
          <button className="btn btn-primary" onClick={handleCreate} disabled={loading || !newRootName.trim()}>
            {loading ? 'Creating...' : 'Create Root'}
          </button>
        </div>
      </>
    );
  }

  // ── Edit view ──
  if (view === 'edit') {
    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={goList}><BackArrow /> Roots</button>
        </div>
        <div className="settings-header"><h2>Edit: {editRootName}</h2></div>
        {error && <div className="settings-error">{error}</div>}
        <div className="form-group">
          <label>config.json</label>
          <textarea className="form-textarea" rows={12} value={editConfigJson} onChange={e => setEditConfigJson(e.target.value)} />
        </div>
        <div className="settings-form-actions">
          <button className="btn btn-primary" onClick={handleSaveEdit} disabled={loading}>
            {loading ? 'Saving...' : 'Save Changes'}
          </button>
        </div>
      </>
    );
  }

  // ── Import Git view ──
  if (view === 'import-git') {
    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={goList}><BackArrow /> Roots</button>
        </div>
        <div className="settings-header"><h2>Import from Git</h2></div>
        {error && <div className="settings-error">{error}</div>}
        {statusMsg && <div className="root-status">{statusMsg}</div>}

        <div className="form-group">
          <label>Git URL</label>
          <input type="text" value={gitUrl} onChange={e => setGitUrl(e.target.value)} placeholder="https://github.com/org/repo.git" autoFocus />
        </div>
        <div className="form-group">
          <label>Path (optional)</label>
          <input type="text" value={gitPath} onChange={e => setGitPath(e.target.value)} placeholder="subdirectory/path" />
        </div>
        <div className="form-group">
          <label>Ref (optional)</label>
          <input type="text" value={gitRef} onChange={e => setGitRef(e.target.value)} placeholder="main, v1.0, etc." />
        </div>

        {importPreview && (
          <>
            <div className="root-import-preview">
              <div className="root-import-preview-title">Preview</div>
              <div className="root-import-preview-files">
                {Object.keys(importPreview.Preview.Files).map(f => (
                  <div key={f} className="root-import-file">{f}</div>
                ))}
              </div>
            </div>
            <div className="form-group">
              <label>Install as</label>
              <input type="text" value={importRootName} onChange={e => setImportRootName(e.target.value)} />
            </div>
          </>
        )}

        <div className="settings-form-actions">
          {!importPreview ? (
            <button className="btn btn-primary" onClick={handlePreviewGit} disabled={loading || !gitUrl.trim()}>
              {loading ? 'Cloning...' : 'Preview'}
            </button>
          ) : (
            <button className="btn btn-primary" onClick={handleInstallGit} disabled={loading || !importRootName.trim()}>
              {loading ? 'Installing...' : 'Install Root'}
            </button>
          )}
        </div>
      </>
    );
  }

  // ── Import ZIP view ──
  if (view === 'import-zip') {
    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={goList}><BackArrow /> Roots</button>
        </div>
        <div className="settings-header"><h2>Import from ZIP</h2></div>
        {error && <div className="settings-error">{error}</div>}
        {statusMsg && <div className="root-status">{statusMsg}</div>}

        {!zipPreview ? (
          <div className="root-zip-upload">
            <label className="root-zip-dropzone">
              <svg width={32} height={32} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round">
                <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><polyline points="17 8 12 3 7 8" /><line x1="12" y1="3" x2="12" y2="15" />
              </svg>
              <span>Click to select a <strong>.zip</strong> or <strong>.gmroot</strong> file</span>
              <span className="root-zip-hint">Must contain a .godmode-root/ folder with config.json</span>
              <input type="file" accept=".zip,.gmroot" onChange={handleZipUpload} hidden disabled={loading} />
            </label>
          </div>
        ) : (
          <>
            <div className="root-import-preview">
              <div className="root-import-preview-title">Contents</div>
              <div className="root-import-preview-files">
                {Object.keys(zipPreview.Preview.Files).map(f => (
                  <div key={f} className="root-import-file">{f}</div>
                ))}
              </div>
            </div>
            <div className="form-group">
              <label>Install as</label>
              <input type="text" value={zipRootName} onChange={e => setZipRootName(e.target.value)} />
            </div>
            <div className="settings-form-actions">
              <button className="btn btn-primary" onClick={handleInstallZip} disabled={loading || !zipRootName.trim()}>
                {loading ? 'Installing...' : 'Install Root'}
              </button>
            </div>
          </>
        )}
      </>
    );
  }

  // ── List view ──
  return (
    <>
      <div className="settings-header">
        <h2>Roots</h2>
        <div style={{ display: 'flex', gap: 6 }}>
          <button className="settings-add-btn" onClick={() => { setView('import-zip'); setZipPreview(null); setStatusMsg(null); }}>
            <svg width={12} height={12} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
              <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><polyline points="17 8 12 3 7 8" /><line x1="12" y1="3" x2="12" y2="15" />
            </svg>
            Upload
          </button>
          <button className="settings-add-btn" onClick={() => { setView('import-git'); setImportPreview(null); setStatusMsg(null); }}>
            <svg width={12} height={12} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
              <circle cx="12" cy="12" r="4" /><line x1="1.05" y1="12" x2="7" y2="12" /><line x1="17.01" y1="12" x2="22.96" y2="12" />
            </svg>
            Git
          </button>
          <button className="settings-add-btn" onClick={() => setView('create')}>
            <span className="plus">+</span> Create
          </button>
        </div>
      </div>

      {error && <div className="settings-error">{error}</div>}

      {roots.length === 0 ? (
        <div className="settings-empty">No roots configured. Create or import one.</div>
      ) : (
        <>
          <div className="settings-list">
            {roots.map(r => (
              <div key={`${r.ProfileName}/${r.Name}`} className="settings-item">
                <div className="settings-item-info">
                  <div className="settings-item-name">{r.Name}</div>
                  {r.Description && <div className="settings-item-desc">{r.Description}</div>}
                </div>
                <div className="settings-item-actions">
                  <select
                    className="settings-badge"
                    style={{ cursor: 'pointer', background: 'var(--glass)' }}
                    value={r.ProfileName === 'Default' ? '' : (r.ProfileName ?? '')}
                    onChange={e => handleChangeProfile(r.ProfileName ?? 'Default', r.Name, e.target.value)}
                  >
                    <option value="">All profiles</option>
                    {profiles.map(p => <option key={p.Name} value={p.Name}>{p.Name}</option>)}
                  </select>
                  <IconBtn title="Edit" svg={ICON_EDIT} onClick={() => handleEdit(r.ProfileName ?? 'Default', r.Name)} />
                  <IconBtn title="Copy" svg={ICON_COPY} onClick={() => handleCopy(r.ProfileName ?? 'Default', r.Name)} />
                  <RowDelete onDelete={() => handleDelete(r.ProfileName ?? 'Default', r.Name)} />
                </div>
              </div>
            ))}
          </div>
          <div className="settings-count">{roots.length} root{roots.length !== 1 ? 's' : ''}</div>
        </>
      )}
    </>
  );
}
