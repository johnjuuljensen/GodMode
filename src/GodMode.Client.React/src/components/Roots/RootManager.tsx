import { useState, useEffect, useCallback, useMemo } from 'react';
import { useAppStore } from '../../store';
import type {
  RootTemplate, RootPreview, SchemaFieldDefinition, CreateActionInfo,
  RootTemplateParameter, SharedRootPreview, ProjectRootInfo, InferenceStatus,
} from '../../signalr/types';
import './RootManager.css';

type View = 'list' | 'create' | 'import';

interface FooterAction {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  primary?: boolean;
}

export function RootManager() {
  const servers = useAppStore(s => s.servers);
  const rootManagerServerId = useAppStore(s => s.rootManagerServerId);
  const rootManagerInitialTab = useAppStore(s => s.rootManagerInitialTab);
  const setShowRootManager = useAppStore(s => s.setShowRootManager);
  const refreshProjects = useAppStore(s => s.refreshProjects);

  const connectedServers = useMemo(
    () => servers.filter(s => s.connectionState === 'connected'),
    [servers],
  );

  const [selectedServerId, setSelectedServerId] = useState<string>(
    rootManagerServerId ?? connectedServers[0]?.registration.url ?? ''
  );
  const selectedServer = servers.find(s => s.registration.url === selectedServerId);
  const hub = selectedServer?.hub;
  const roots = selectedServer?.roots ?? [];

  const [view, setView] = useState<View>(rootManagerInitialTab === 'create' ? 'create' : rootManagerInitialTab === 'import' ? 'import' : 'list');
  const [footerAction, setFooterAction] = useState<FooterAction | null>(null);

  const close = () => setShowRootManager(false);
  const backToList = () => { setView('list'); setFooterAction(null); };

  return (
    <div className="modal-overlay" onClick={close}>
      <div className="modal root-manager-modal" onClick={e => e.stopPropagation()}>
        <div className="root-manager-header">
          {view !== 'list' && (
            <button className="btn btn-secondary btn-sm" onClick={backToList}>← Back</button>
          )}
          <h2>{view === 'list' ? 'Roots' : view === 'create' ? 'New Root' : 'Import Root'}</h2>
          {view === 'list' && connectedServers.length > 1 && (
            <select
              className="root-manager-server-select"
              value={selectedServerId}
              onChange={e => setSelectedServerId(e.target.value)}
            >
              {connectedServers.map(s => (
                <option key={s.registration.url} value={s.registration.url}>
                  {s.registration.displayName || s.registration.url}
                </option>
              ))}
            </select>
          )}
        </div>

        <div className="root-manager-body">
          {view === 'list' && hub && (
            <>
              <RootList
                roots={roots}
                hub={hub}
              />
              <div className="root-manager-actions">
                <button className="btn btn-primary" onClick={() => setView('create')}>
                  New Root
                </button>
                <button className="btn btn-secondary" onClick={() => setView('import')}>
                  Import Root
                </button>
              </div>
            </>
          )}
          {view === 'create' && hub && (
            <CreateRootPane
              hub={hub}
              profiles={selectedServer?.profiles ?? []}
              onCreated={() => { refreshProjects(selectedServerId); backToList(); }}
              onFooterAction={setFooterAction}
            />
          )}
          {view === 'import' && hub && (
            <ImportRootPane
              hub={hub}
              onInstalled={() => { refreshProjects(selectedServerId); setView('list'); }}
            />
          )}
        </div>

        <div className="root-manager-footer">
          {footerAction && (
            <button
              className={`btn ${footerAction.primary !== false ? 'btn-primary' : 'btn-secondary'}`}
              onClick={footerAction.onClick}
              disabled={footerAction.disabled}
            >
              {footerAction.label}
            </button>
          )}
          <button className="btn btn-secondary" onClick={close}>Close</button>
        </div>
      </div>
    </div>
  );
}

// ─── Root List Tab ───────────────────────────────────────────────────────────

function RootList({ roots, hub }: {
  roots: ProjectRootInfo[];
  hub: InstanceType<typeof import('../../signalr/hub').GodModeHub>;
}) {
  const [exporting, setExporting] = useState<string | null>(null);

  const handleExport = async (root: ProjectRootInfo) => {
    const profileName = root.ProfileName ?? 'Default';
    setExporting(root.Name);
    try {
      const bytes = await hub.exportRoot(profileName, root.Name);
      const blob = new Blob([bytes as BlobPart], { type: 'application/zip' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `${root.Name}.gmroot`;
      a.click();
      URL.revokeObjectURL(url);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      // Strip SignalR prefix like "HubException: "
      const clean = msg.replace(/^.*?HubException:\s*/i, '');
      alert(clean || 'Export failed');
    } finally {
      setExporting(null);
    }
  };

  // Group by profile
  const grouped = useMemo(() => {
    const map = new Map<string, ProjectRootInfo[]>();
    for (const r of roots) {
      const p = r.ProfileName ?? 'Default';
      if (!map.has(p)) map.set(p, []);
      map.get(p)!.push(r);
    }
    return map;
  }, [roots]);

  if (roots.length === 0) {
    return (
      <div className="root-list-empty">
        <p>No roots configured yet.</p>
        <p style={{ fontSize: 12, color: 'var(--text-tertiary)' }}>
          Create a new root from a template, or import one from a file, URL, or git repo.
        </p>
      </div>
    );
  }

  return (
    <div className="root-list">
      {[...grouped.entries()].map(([profileName, profileRoots]) => (
        <div key={profileName}>
          {grouped.size > 1 && (
            <div className="root-list-profile-header">{profileName}</div>
          )}
          {profileRoots.map(root => (
            <div className="root-list-item" key={root.Name}>
              <div className="root-list-item-info">
                <div className="root-list-item-name">{root.Name}</div>
                {root.Description && (
                  <div className="root-list-item-desc">{root.Description}</div>
                )}
                {root.Actions && root.Actions.length > 0 && (
                  <div className="root-list-item-actions">
                    {root.Actions.map((a: CreateActionInfo) => (
                      <span key={a.Name} className="root-action-badge">{a.Name}</span>
                    ))}
                  </div>
                )}
              </div>
              <div className="root-list-item-buttons">
                {root.HasConfig && (
                  <button
                    className="btn btn-secondary btn-sm"
                    onClick={() => handleExport(root)}
                    disabled={exporting === root.Name}
                    title="Download as .gmroot file"
                  >
                    {exporting === root.Name ? '...' : 'Export'}
                  </button>
                )}
              </div>
            </div>
          ))}
        </div>
      ))}
    </div>
  );
}

// ─── Create Root View ────────────────────────────────────────────────────────

const FIELD_TYPES = [
  { value: 'string', label: 'Text' },
  { value: 'multiline', label: 'Multiline' },
  { value: 'boolean', label: 'Boolean' },
  { value: 'enum', label: 'Dropdown' },
];

interface SchemaField {
  key: string;
  title: string;
  fieldType: string;
  isRequired: boolean;
  enumValues: string;
}

function emptyField(): SchemaField {
  return { key: '', title: '', fieldType: 'string', isRequired: false, enumValues: '' };
}

// ─── Template Categories ──────────────────────────────────────────────────────

interface TemplateCategory {
  key: string;
  label: string;
  templates: string[];  // template names
}

const TEMPLATE_CATEGORIES: TemplateCategory[] = [
  { key: 'basic',    label: 'Basic',    templates: ['local-folder'] },
  { key: 'code',     label: 'Code',     templates: ['blank', 'git-clone', 'git-worktree', 'feature', 'bugfix', 'npm-project', 'dotnet-project', 'monorepo'] },
  { key: 'admin',    label: 'Admin',    templates: ['github-issue', 'pr-review'] },
  { key: 'creative', label: 'Creative', templates: ['ad-hoc'] },
  { key: 'media',    label: 'Media',    templates: [] },
];

function categorizeTemplates(templates: RootTemplate[]): { category: TemplateCategory; items: RootTemplate[] }[] {
  const byName = new Map(templates.map(t => [t.Name, t]));
  const used = new Set<string>();
  const result: { category: TemplateCategory; items: RootTemplate[] }[] = [];

  for (const cat of TEMPLATE_CATEGORIES) {
    const items: RootTemplate[] = [];
    for (const name of cat.templates) {
      const t = byName.get(name);
      if (t) { items.push(t); used.add(name); }
    }
    if (items.length > 0) result.push({ category: cat, items });
  }

  // Any uncategorized templates go into Code
  const uncategorized = templates.filter(t => !used.has(t.Name));
  if (uncategorized.length > 0) {
    const codeGroup = result.find(g => g.category.key === 'code');
    if (codeGroup) codeGroup.items.push(...uncategorized);
    else result.push({ category: { key: 'code', label: 'Code', templates: [] }, items: uncategorized });
  }

  return result;
}

function TemplatePicker({ templates, onSelect }: {
  templates: RootTemplate[];
  onSelect: (template: RootTemplate) => void;
}) {
  const [expandedCategory, setExpandedCategory] = useState<string>('basic');
  const groups = useMemo(() => categorizeTemplates(templates), [templates]);

  const toggleCategory = (key: string) =>
    setExpandedCategory(prev => prev === key ? '' : key);

  return (
    <div className="template-accordion">
      {groups.map(({ category, items }) => (
        <div key={category.key} className="template-accordion-group">
          <button
            className={`template-accordion-header ${expandedCategory === category.key ? 'expanded' : ''}`}
            onClick={() => toggleCategory(category.key)}
          >
            <span className="template-accordion-label">{category.label}</span>
            <span className="template-accordion-count">{items.length}</span>
            <span className="template-accordion-chevron">{expandedCategory === category.key ? '▾' : '▸'}</span>
          </button>
          {expandedCategory === category.key && (
            <div className="template-accordion-body">
              {items.map(t => (
                <button
                  key={t.Name}
                  className="template-accordion-item"
                  onClick={() => onSelect(t)}
                >
                  <div className="template-accordion-item-name">{t.DisplayName}</div>
                  <div className="template-accordion-item-desc">{t.Description}</div>
                </button>
              ))}
            </div>
          )}
        </div>
      ))}
    </div>
  );
}

function CreateRootPane({ hub, profiles, onCreated, onFooterAction }: {
  hub: InstanceType<typeof import('../../signalr/hub').GodModeHub>;
  profiles: { Name: string }[];
  onCreated: () => void;
  onFooterAction: (action: FooterAction | null) => void;
}) {
  const [templates, setTemplates] = useState<RootTemplate[]>([]);
  const [selectedTemplate, setSelectedTemplate] = useState<RootTemplate | null>(null);
  const [templateParams, setTemplateParams] = useState<Record<string, string>>({});
  const [rootName, setRootName] = useState('');
  const [profileName, setProfileName] = useState(profiles[0]?.Name ?? 'Default');
  const [schemaFields, setSchemaFields] = useState<SchemaField[]>([]);
  const [preview, setPreview] = useState<RootPreview | null>(null);
  const [expandedFiles, setExpandedFiles] = useState<Record<string, boolean>>({});
  const [llmPrompt, setLlmPrompt] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [fieldsConfirmed, setFieldsConfirmed] = useState(false);
  const [scriptsConfirmed, setScriptsConfirmed] = useState(false);
  const [scriptMode, setScriptMode] = useState<'manual' | 'ai'>('manual');
  const [manualScriptSh, setManualScriptSh] = useState('');
  const [manualScriptPs, setManualScriptPs] = useState('');
  const [converting, setConverting] = useState<'sh-to-ps' | 'ps-to-sh' | null>(null);
  const [inferenceStatus, setInferenceStatus] = useState<InferenceStatus | null>(null);
  const [apiKeyInput, setApiKeyInput] = useState('');
  const [savingKey, setSavingKey] = useState(false);

  useEffect(() => {
    hub.listRootTemplates().then(setTemplates).catch(() => {});
    hub.getInferenceStatus().then(setInferenceStatus).catch(() => {});
  }, [hub]);

  const templateParameters: RootTemplateParameter[] = selectedTemplate?.Parameters ?? [];

  useEffect(() => {
    const defaults: Record<string, string> = {};
    for (const p of templateParameters) defaults[p.Key] = p.DefaultValue ?? '';
    setTemplateParams(defaults);
  }, [selectedTemplate]);

  const handleSelectTemplate = (template: RootTemplate) => {
    setSelectedTemplate(template);
    if (!rootName) setRootName(template.Name);
  };

  const handleChangeTemplate = () => {
    setSelectedTemplate(null);
    setTemplateParams({});
    setPreview(null);
    setSchemaFields([]);
    setError(null);
  };

  const addField = useCallback(() => setSchemaFields(prev => [...prev, emptyField()]), []);
  const updateField = useCallback((i: number, u: Partial<SchemaField>) =>
    setSchemaFields(prev => prev.map((f, idx) => idx === i ? { ...f, ...u } : f)), []);
  const removeField = useCallback((i: number) =>
    setSchemaFields(prev => prev.filter((_, idx) => idx !== i)), []);

  const handleLoadTemplate = async () => {
    if (!selectedTemplate) return;
    setLoading(true); setError(null);
    try {
      const p = await hub.previewRootFromTemplate(selectedTemplate.Name, templateParams);
      setPreview(p);
      if (p.ValidationError) setError(p.ValidationError);
      parseSchemaFromPreview(p);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load template');
    } finally { setLoading(false); }
  };

  const parseSchemaFromPreview = (p: RootPreview) => {
    const schemaFile = Object.entries(p.Files).find(([path]) => path.endsWith('schema.json'));
    if (!schemaFile) return;
    try {
      const schema = JSON.parse(schemaFile[1]);
      const properties = schema.properties as Record<string, Record<string, unknown>> | undefined;
      if (!properties) return;
      const required = new Set<string>(Array.isArray(schema.required) ? schema.required : []);
      const fields: SchemaField[] = [];
      for (const [key, prop] of Object.entries(properties)) {
        const type = prop.type as string;
        let fieldType = 'string';
        if (type === 'boolean') fieldType = 'boolean';
        else if (Array.isArray(prop.enum)) fieldType = 'enum';
        else if (prop.format === 'multiline' || prop['x-multiline'] === true) fieldType = 'multiline';
        fields.push({
          key, title: (prop.title as string) || key, fieldType,
          isRequired: required.has(key),
          enumValues: Array.isArray(prop.enum) ? (prop.enum as string[]).join(', ') : '',
        });
      }
      if (fields.length > 0) setSchemaFields(fields);
    } catch { /* ignore */ }
  };

  const handleLlmGenerate = async () => {
    if (!llmPrompt.trim()) return;
    setLoading(true); setError(null);
    try {
      const fieldDefs: SchemaFieldDefinition[] = schemaFields
        .filter(f => f.key.trim())
        .map(f => ({
          Key: f.key.trim(), Title: f.title || f.key, FieldType: f.fieldType,
          IsRequired: f.isRequired, IsMultiline: f.fieldType === 'multiline',
          EnumValues: f.fieldType === 'enum' ? f.enumValues.split(',').map(v => v.trim()).filter(Boolean) : null,
        }));
      const result = await hub.generateRootWithLlm({
        UserInstruction: llmPrompt.trim(),
        CurrentFiles: preview?.Files ?? null,
        SchemaFields: fieldDefs.length > 0 ? fieldDefs : null,
      });
      setPreview(result);
      if (result.ValidationError) setError(result.ValidationError);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'LLM generation failed');
    } finally { setLoading(false); }
  };

  const handleSave = async () => {
    if (!preview || !rootName.trim()) return;
    setSaving(true); setError(null);
    try {
      await hub.createRoot(profileName, rootName.trim(), preview);
      onCreated();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create root');
    } finally { setSaving(false); }
  };

  const applyManualScripts = useCallback(() => {
    if (!preview) return;
    const files = { ...preview.Files };
    if (manualScriptSh.trim()) files['scripts/create.sh'] = manualScriptSh;
    if (manualScriptPs.trim()) files['scripts/create.ps1'] = manualScriptPs;
    setPreview({ ...preview, Files: files });
  }, [preview, manualScriptSh, manualScriptPs]);

  const handleConvertScript = async (direction: 'sh-to-ps' | 'ps-to-sh') => {
    const source = direction === 'sh-to-ps' ? manualScriptSh : manualScriptPs;
    if (!source.trim()) return;
    setConverting(direction);
    setError(null);
    try {
      const fromLang = direction === 'sh-to-ps' ? 'Bash/Shell (.sh)' : 'PowerShell (.ps1)';
      const toLang = direction === 'sh-to-ps' ? 'PowerShell (.ps1)' : 'Bash/Shell (.sh)';
      const result = await hub.generateRootWithLlm({
        UserInstruction: `Convert this ${fromLang} script to ${toLang}. Return ONLY the converted script, no explanation or markdown fencing.\n\n${source}`,
        CurrentFiles: null,
        SchemaFields: null,
      });
      // Extract the script content from the first file in the result
      const content = Object.values(result.Files)[0] ?? '';
      if (direction === 'sh-to-ps') setManualScriptPs(content);
      else setManualScriptSh(content);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Conversion failed');
    } finally { setConverting(null); }
  };

  const toggleFile = (path: string) => setExpandedFiles(prev => ({ ...prev, [path]: !prev[path] }));
  const hasFiles = preview && Object.keys(preview.Files).length > 0;

  // ── Progressive step gating ──
  const hasName = rootName.trim().length > 0;
  const hasRequiredParams = templateParameters
    .filter(p => p.Required)
    .every(p => (templateParams[p.Key] ?? '').trim().length > 0);
  const templateLoaded = !!preview;

  // ── Drive footer action based on current step ──
  useEffect(() => {
    if (!selectedTemplate) {
      onFooterAction(null);
    } else if (!templateLoaded && hasName && hasRequiredParams) {
      // Step 3: ready to load template
      onFooterAction({ label: loading ? 'Loading...' : 'Continue', onClick: handleLoadTemplate, disabled: loading });
    } else if (templateLoaded && !fieldsConfirmed) {
      // Step 4: confirm input fields
      onFooterAction({ label: 'Continue', onClick: () => setFieldsConfirmed(true) });
    } else if (fieldsConfirmed && !scriptsConfirmed) {
      // Step 5: confirm scripts
      onFooterAction({ label: hasFiles ? 'Continue' : 'Skip', onClick: () => setScriptsConfirmed(true) });
    } else if (scriptsConfirmed) {
      // Step 6: create root
      onFooterAction({ label: saving ? 'Creating...' : 'Create Root', onClick: handleSave, disabled: saving || !hasName || !hasFiles });
    } else {
      onFooterAction(null);
    }
  }, [selectedTemplate, templateLoaded, hasName, hasRequiredParams, fieldsConfirmed, scriptsConfirmed, loading, saving, hasFiles]);

  // Cleanup footer on unmount
  useEffect(() => () => onFooterAction(null), []);

  // ── Phase 1: Template selection (no template chosen yet) ──
  if (!selectedTemplate) {
    return (
      <div className="create-root-pane">
        <p style={{ fontSize: 13, color: 'var(--text-secondary)', margin: '0 0 12px' }}>
          Choose a template to get started:
        </p>
        <TemplatePicker templates={templates} onSelect={handleSelectTemplate} />
      </div>
    );
  }

  // ── Phase 2: Progressive form — reveal fields step by step ──
  return (
    <div className="create-root-pane">
      {/* Step 0: Selected template badge (always visible) */}
      <div className="selected-template-badge">
        <div className="selected-template-info">
          <span className="selected-template-name">{selectedTemplate.DisplayName}</span>
          <span className="selected-template-desc">{selectedTemplate.Description}</span>
        </div>
        <button className="btn btn-secondary btn-sm" onClick={handleChangeTemplate}>Change</button>
      </div>

      {/* Step 1–2: Name + profile + template params (expanded) */}
      {!templateLoaded && (
        <>
          <div style={{ display: 'flex', gap: 8, marginBottom: 14 }}>
            <div className="form-group" style={{ flex: 1, marginBottom: 0 }}>
              <label>Root Name</label>
              <input type="text" value={rootName} onChange={e => setRootName(e.target.value)} placeholder="e.g. my-project-root" autoFocus />
            </div>
            <div className="form-group" style={{ flex: 1, marginBottom: 0 }}>
              <label>Profile</label>
              {profiles.length > 1 ? (
                <select value={profileName} onChange={e => setProfileName(e.target.value)}>
                  {profiles.map(p => <option key={p.Name} value={p.Name}>{p.Name}</option>)}
                </select>
              ) : (
                <input type="text" value={profileName} onChange={e => setProfileName(e.target.value)} />
              )}
            </div>
          </div>

          {hasName && templateParameters.length > 0 && (
            <div className="create-root-section">
              <h4 className="create-root-section-title">Template settings</h4>
              {templateParameters.map(p => (
                <div className="form-group" key={p.Key}>
                  <label>{p.Title}{p.Required && <span className="form-required">*</span>}</label>
                  {p.Description && <div className="form-description">{p.Description}</div>}
                  <input type="text" value={templateParams[p.Key] ?? ''} onChange={e => setTemplateParams(prev => ({ ...prev, [p.Key]: e.target.value }))} />
                </div>
              ))}
            </div>
          )}
        </>
      )}

      {/* Step 1–2: Collapsed summary (after template loaded) */}
      {templateLoaded && (
        <div className="selected-template-badge">
          <div className="selected-template-info">
            <span className="selected-template-name">{rootName}</span>
            <span className="selected-template-desc">
              {profileName}{templateParameters.length > 0 ? ` · ${templateParameters.map(p => templateParams[p.Key] || p.Key).join(', ')}` : ''}
            </span>
          </div>
          <button className="btn btn-secondary btn-sm" onClick={() => { setPreview(null); setFieldsConfirmed(false); setScriptsConfirmed(false); }}>Edit</button>
        </div>
      )}

      {/* Step 4: Schema fields (after template loaded) */}
      {templateLoaded && !fieldsConfirmed && (
        <div className="create-root-section">
          <h4 className="create-root-section-title">Input fields</h4>
          <p style={{ fontSize: 11, color: 'var(--text-tertiary)', margin: '0 0 8px' }}>
            Define the fields users will fill in when creating a project from this root.
          </p>
          {schemaFields.length > 0 && (
            <div className="schema-field-header">
              <span className="field-key">Key</span>
              <span className="field-title">Title</span>
              <span className="field-type">Type</span>
              <span className="field-required">Req</span>
              <span className="field-remove" />
            </div>
          )}
          <div className="schema-fields">
            {schemaFields.map((field, i) => (
              <div className="schema-field-row" key={i}>
                <input className="field-key" type="text" value={field.key} onChange={e => updateField(i, { key: e.target.value })} placeholder="key" />
                <input className="field-title" type="text" value={field.title} onChange={e => updateField(i, { title: e.target.value })} placeholder="Display title" />
                <select className="field-type" value={field.fieldType} onChange={e => updateField(i, { fieldType: e.target.value })}>
                  {FIELD_TYPES.map(ft => <option key={ft.value} value={ft.value}>{ft.label}</option>)}
                </select>
                <input className="field-required" type="checkbox" checked={field.isRequired} onChange={e => updateField(i, { isRequired: e.target.checked })} />
                <button className="field-remove" onClick={() => removeField(i)}>&#x2715;</button>
              </div>
            ))}
          </div>
          <button className="btn btn-secondary btn-sm" style={{ marginTop: 8 }} onClick={addField}>+ Add Field</button>
        </div>
      )}

      {/* Confirmed fields summary (collapsed) */}
      {templateLoaded && fieldsConfirmed && (
        <div className="selected-template-badge" style={{ marginBottom: 14 }}>
          <div className="selected-template-info">
            <span className="selected-template-name">Input fields</span>
            <span className="selected-template-desc">
              {schemaFields.length === 0
                ? 'No custom fields'
                : schemaFields.map(f => f.title || f.key).join(', ')}
            </span>
          </div>
          <button className="btn btn-secondary btn-sm" onClick={() => setFieldsConfirmed(false)}>Edit</button>
        </div>
      )}

      {/* Step 5: Scripts (after fields confirmed, before scripts confirmed) */}
      {fieldsConfirmed && !scriptsConfirmed && (
        <div className="create-root-section">
          <h4 className="create-root-section-title">Scripts</h4>
          <div className="script-mode-tabs">
            <button className={`script-mode-tab ${scriptMode === 'manual' ? 'active' : ''}`} onClick={() => setScriptMode('manual')}>
              Write manually
            </button>
            <button className={`script-mode-tab ${scriptMode === 'ai' ? 'active' : ''}`} onClick={() => setScriptMode('ai')}>
              Generate with AI
            </button>
          </div>

          {scriptMode === 'manual' && (
            <div className="script-manual">
              <div className="form-group">
                <label>Shell script <span className="form-description">(.sh — macOS/Linux)</span></label>
                <textarea
                  className="form-textarea script-textarea"
                  value={manualScriptSh}
                  onChange={e => setManualScriptSh(e.target.value)}
                  placeholder={"#!/bin/bash\n# Your create script here..."}
                  rows={6}
                />
              </div>
              <div className="script-convert-bar">
                <button
                  className="btn btn-secondary btn-sm"
                  onClick={() => handleConvertScript('sh-to-ps')}
                  disabled={!manualScriptSh.trim() || !!converting}
                  title="Convert shell script to PowerShell"
                >
                  {converting === 'sh-to-ps' ? 'Converting...' : '.sh → .ps1'}
                </button>
                <button
                  className="btn btn-secondary btn-sm"
                  onClick={() => handleConvertScript('ps-to-sh')}
                  disabled={!manualScriptPs.trim() || !!converting}
                  title="Convert PowerShell script to shell"
                >
                  {converting === 'ps-to-sh' ? 'Converting...' : '.ps1 → .sh'}
                </button>
              </div>
              <div className="form-group">
                <label>PowerShell script <span className="form-description">(.ps1 — Windows)</span></label>
                <textarea
                  className="form-textarea script-textarea"
                  value={manualScriptPs}
                  onChange={e => setManualScriptPs(e.target.value)}
                  placeholder="# Your create script here..."
                  rows={6}
                />
              </div>
              {(manualScriptSh.trim() || manualScriptPs.trim()) && (
                <button className="btn btn-secondary btn-sm" onClick={applyManualScripts}>
                  Apply scripts
                </button>
              )}
            </div>
          )}

          {scriptMode === 'ai' && (
            <div className="llm-section">
              {inferenceStatus && !inferenceStatus.IsConfigured ? (
                <LlmSetup
                  hub={hub}
                  apiKeyInput={apiKeyInput}
                  setApiKeyInput={setApiKeyInput}
                  saving={savingKey}
                  setSaving={setSavingKey}
                  onConfigured={setInferenceStatus}
                />
              ) : (
                <>
                  <p style={{ fontSize: 11, color: 'var(--text-tertiary)', margin: '0 0 8px' }}>
                    Describe what scripts should do. The LLM will generate cross-platform scripts.
                    {inferenceStatus?.Provider && (
                      <span style={{ marginLeft: 6, opacity: 0.7 }}>
                        ({inferenceStatus.Provider}{inferenceStatus.Model ? ` / ${inferenceStatus.Model}` : ''})
                      </span>
                    )}
                  </p>
                  <textarea className="form-textarea" value={llmPrompt} onChange={e => setLlmPrompt(e.target.value)}
                    placeholder="e.g. Clone the repo, create a feature branch, run npm install" rows={3} />
                  <button className="btn btn-primary btn-sm" onClick={handleLlmGenerate} disabled={loading || !llmPrompt.trim()} style={{ marginTop: 8 }}>
                    {loading ? 'Generating...' : 'Generate with LLM'}
                  </button>
                </>
              )}
            </div>
          )}
        </div>
      )}

      {/* Scripts confirmed summary (collapsed) */}
      {fieldsConfirmed && scriptsConfirmed && (
        <div className="selected-template-badge" style={{ marginBottom: 14 }}>
          <div className="selected-template-info">
            <span className="selected-template-name">Scripts</span>
            <span className="selected-template-desc">
              {llmPrompt.trim() || 'No LLM-generated scripts'}
            </span>
          </div>
          <button className="btn btn-secondary btn-sm" onClick={() => setScriptsConfirmed(false)}>Edit</button>
        </div>
      )}

      {error && <div className="form-error" style={{ marginTop: 8 }}>{error}</div>}

      {/* Step 6: File preview (final step) */}
      {scriptsConfirmed && hasFiles && (
        <div className="create-root-section">
          <h4 className="create-root-section-title">Generated files</h4>
          <div className="file-preview-list">
            {Object.entries(preview!.Files).map(([path, content]) => (
              <div className="file-preview-item" key={path}>
                <div className="file-preview-header" onClick={() => toggleFile(path)}>
                  <span>{path}</span>
                  <span>{expandedFiles[path] ? '▾' : '▸'}</span>
                </div>
                {expandedFiles[path] && <div className="file-preview-content">{content}</div>}
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// ─── Import Root Tab ─────────────────────────────────────────────────────────

type ImportMode = 'file' | 'url' | 'git';

function ImportRootPane({ hub, onInstalled }: {
  hub: InstanceType<typeof import('../../signalr/hub').GodModeHub>;
  onInstalled: () => void;
}) {
  const [mode, setMode] = useState<ImportMode>('url');
  const [url, setUrl] = useState('');
  const [repoUrl, setRepoUrl] = useState('');
  const [subPath, setSubPath] = useState('');
  const [gitRef, setGitRef] = useState('');
  const [localName, setLocalName] = useState('');
  const [loading, setLoading] = useState(false);
  const [installing, setInstalling] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<SharedRootPreview | null>(null);
  const [expandedFiles, setExpandedFiles] = useState<Record<string, boolean>>({});

  const handleFileUpload = useCallback(async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    setLoading(true); setError(null); setPreview(null);
    try {
      const buffer = await file.arrayBuffer();
      const result = await hub.previewImportFromBytes(new Uint8Array(buffer));
      setPreview(result);
      if (!localName) setLocalName(result.Manifest.Name);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to read package');
    } finally { setLoading(false); }
  }, [hub, localName]);

  const handleUrlPreview = async () => {
    if (!url.trim()) return;
    setLoading(true); setError(null); setPreview(null);
    try {
      const result = await hub.previewImportFromUrl(url.trim());
      setPreview(result);
      if (!localName) setLocalName(result.Manifest.Name);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch package');
    } finally { setLoading(false); }
  };

  const handleGitPreview = async () => {
    if (!repoUrl.trim()) return;
    setLoading(true); setError(null); setPreview(null);
    try {
      const result = await hub.previewImportFromGit(repoUrl.trim(), subPath.trim() || null, gitRef.trim() || null);
      setPreview(result);
      if (!localName) setLocalName(result.Manifest.Name);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch from git');
    } finally { setLoading(false); }
  };

  const handleInstall = async () => {
    if (!preview) return;
    setInstalling(true); setError(null);
    try {
      await hub.installSharedRoot(preview, localName.trim() || null);
      onInstalled();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to install root');
    } finally { setInstalling(false); }
  };

  const toggleFile = (path: string) => setExpandedFiles(prev => ({ ...prev, [path]: !prev[path] }));

  const scriptFiles = preview ? Object.entries(preview.Files).filter(([p]) => p.endsWith('.sh') || p.endsWith('.ps1')) : [];
  const configFiles = preview ? Object.entries(preview.Files).filter(([p]) => p.endsWith('.json')) : [];

  if (preview) {
    return (
      <div className="import-root-pane">
        {/* Manifest info */}
        <div className="import-manifest">
          <div className="import-manifest-name">{preview.Manifest.DisplayName}</div>
          {preview.Manifest.Description && <div className="import-manifest-desc">{preview.Manifest.Description}</div>}
          <div className="import-manifest-meta">
            {preview.Manifest.Author && <span>Author: {preview.Manifest.Author}</span>}
            {preview.Manifest.Version && <span>Version: {preview.Manifest.Version}</span>}
            {preview.Manifest.Platforms && <span>Platforms: {preview.Manifest.Platforms.join(', ')}</span>}
          </div>
        </div>

        <div className="form-group">
          <label>Install as</label>
          <input type="text" value={localName} onChange={e => setLocalName(e.target.value)} placeholder="Local root name" />
        </div>

        {scriptFiles.length > 0 && (
          <>
            <h4 style={{ fontSize: 13, marginBottom: 4, color: 'var(--warning, #f59e0b)' }}>Scripts (review before installing)</h4>
            <div className="file-preview-list">
              {scriptFiles.map(([path, content]) => (
                <div className="file-preview-item" key={path}>
                  <div className="file-preview-header" onClick={() => toggleFile(path)}>
                    <span>{path}</span><span>{expandedFiles[path] ? '▾' : '▸'}</span>
                  </div>
                  {expandedFiles[path] && <div className="file-preview-content">{content}</div>}
                </div>
              ))}
            </div>
          </>
        )}

        {configFiles.length > 0 && (
          <>
            <h4 style={{ fontSize: 13, marginBottom: 4, marginTop: 8 }}>Configuration</h4>
            <div className="file-preview-list">
              {configFiles.map(([path, content]) => (
                <div className="file-preview-item" key={path}>
                  <div className="file-preview-header" onClick={() => toggleFile(path)}>
                    <span>{path}</span><span>{expandedFiles[path] ? '▾' : '▸'}</span>
                  </div>
                  {expandedFiles[path] && <div className="file-preview-content">{content}</div>}
                </div>
              ))}
            </div>
          </>
        )}

        <div className="btn-group" style={{ marginTop: 12 }}>
          <button className="btn btn-primary" onClick={handleInstall} disabled={installing || !localName.trim()}>
            {installing ? 'Installing...' : 'Install Root'}
          </button>
          <button className="btn btn-secondary" onClick={() => setPreview(null)} disabled={installing}>Back</button>
        </div>

        {error && <div className="form-error" style={{ marginTop: 8 }}>{error}</div>}
      </div>
    );
  }

  return (
    <div className="import-root-pane">
      <div className="import-mode-tabs">
        {(['file', 'url', 'git'] as ImportMode[]).map(m => (
          <button key={m} className={`import-mode-tab ${mode === m ? 'active' : ''}`} onClick={() => setMode(m)}>
            {m === 'file' ? 'File' : m === 'url' ? 'URL' : 'Git'}
          </button>
        ))}
      </div>

      {mode === 'file' && (
        <div className="form-group">
          <label>Select .gmroot file</label>
          <input type="file" accept=".gmroot,.zip" onChange={handleFileUpload} />
        </div>
      )}

      {mode === 'url' && (
        <>
          <div className="form-group">
            <label>Package URL</label>
            <input type="text" value={url} onChange={e => setUrl(e.target.value)} placeholder="https://example.com/my-root.gmroot" />
          </div>
          <button className="btn btn-primary" onClick={handleUrlPreview} disabled={loading || !url.trim()}>
            {loading ? 'Fetching...' : 'Preview'}
          </button>
        </>
      )}

      {mode === 'git' && (
        <>
          <div className="form-group">
            <label>Repository URL</label>
            <input type="text" value={repoUrl} onChange={e => setRepoUrl(e.target.value)} placeholder="https://github.com/user/repo" />
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <div className="form-group" style={{ flex: 1 }}>
              <label>Subdirectory <span className="form-description">(optional)</span></label>
              <input type="text" value={subPath} onChange={e => setSubPath(e.target.value)} placeholder="path/to/root" />
            </div>
            <div className="form-group" style={{ flex: 1 }}>
              <label>Branch/Tag <span className="form-description">(optional)</span></label>
              <input type="text" value={gitRef} onChange={e => setGitRef(e.target.value)} placeholder="main" />
            </div>
          </div>
          <button className="btn btn-primary" onClick={handleGitPreview} disabled={loading || !repoUrl.trim()}>
            {loading ? 'Fetching...' : 'Preview'}
          </button>
        </>
      )}

      {error && <div className="form-error" style={{ marginTop: 8 }}>{error}</div>}
    </div>
  );
}

// ─── LLM Setup (API key configuration) ───────────────────────────────────────

function LlmSetup({ hub, apiKeyInput, setApiKeyInput, saving, setSaving, onConfigured }: {
  hub: InstanceType<typeof import('../../signalr/hub').GodModeHub>;
  apiKeyInput: string;
  setApiKeyInput: (v: string) => void;
  saving: boolean;
  setSaving: (v: boolean) => void;
  onConfigured: (status: InferenceStatus) => void;
}) {
  const [error, setError] = useState<string | null>(null);

  const handleSaveKey = async () => {
    if (!apiKeyInput.trim()) return;
    setSaving(true);
    setError(null);
    try {
      const status = await hub.configureInferenceApiKey(apiKeyInput.trim());
      if (status.IsConfigured) {
        onConfigured(status);
      } else {
        setError(status.Error ?? 'Failed to configure API key');
      }
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Failed to save API key');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="llm-setup">
      <p style={{ fontSize: 12, color: 'var(--text-secondary)', margin: '0 0 10px' }}>
        LLM script generation requires an Anthropic API key. Enter your key below, or get one from the Anthropic Console.
      </p>
      <div className="form-group" style={{ marginBottom: 8 }}>
        <label>Anthropic API Key</label>
        <input
          type="password"
          value={apiKeyInput}
          onChange={e => setApiKeyInput(e.target.value)}
          placeholder="sk-ant-..."
          onKeyDown={e => e.key === 'Enter' && handleSaveKey()}
        />
      </div>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
        <button className="btn btn-primary btn-sm" onClick={handleSaveKey} disabled={saving || !apiKeyInput.trim()}>
          {saving ? 'Saving...' : 'Save & Activate'}
        </button>
        <a
          href="https://console.anthropic.com/settings/keys"
          target="_blank"
          rel="noopener noreferrer"
          style={{ fontSize: 12, color: 'var(--accent)' }}
        >
          Get an API key
        </a>
      </div>
      {error && <div className="form-error" style={{ marginTop: 8 }}>{error}</div>}
    </div>
  );
}
