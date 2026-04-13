import { useState, useEffect, useCallback, useMemo } from 'react';
import { useAppStore } from '../../store';
import type { StorageEntry } from '../../signalr/types';
import '../settings-common.css';
import './StorageBrowser.css';

const BackArrow = () => (
  <svg width={16} height={16} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round"><path d="M10 4L6 8l4 4"/></svg>
);

const FolderIcon = () => (
  <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
    <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" />
  </svg>
);

const FileIcon = () => (
  <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
    <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" /><polyline points="14 2 14 8 20 8" />
  </svg>
);

function formatSize(bytes: number): string {
  if (bytes === 0) return '';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1048576).toFixed(1)} MB`;
}

type Filter = 'projects' | 'roots' | 'profiles' | 'all';

// Hidden at root level per filter
const HIDDEN_ALWAYS = new Set(['.godmode-keys', '.godmode-logs', '.archived']);
const HIDDEN_IN_PROJECTS = new Set(['.profiles', '.godmode-keys', '.godmode-logs', '.archived', '.webhooks', '.pipelines']);

// Hidden inside project folders
const HIDDEN_INSIDE_PROJECT = new Set(['.godmode']);

function shouldHide(entry: StorageEntry, filter: Filter, currentPath: string): boolean {
  const name = entry.Name;

  // Always hide these
  if (HIDDEN_ALWAYS.has(name) && currentPath === '.') return true;

  // Inside a project folder, hide .godmode state
  if (HIDDEN_INSIDE_PROJECT.has(name) && currentPath !== '.' && !currentPath.startsWith('.profiles')) return true;

  // Hide logs everywhere
  if (name === 'logs' || name === '.godmode-logs') return true;

  // Filter-specific hiding at root level
  if (currentPath === '.') {
    if (filter === 'projects') return HIDDEN_IN_PROJECTS.has(name) || name.startsWith('.');
    if (filter === 'roots') return !name.startsWith('.') && name !== '.profiles'; // show only root dirs with .godmode-root
    if (filter === 'profiles') return name !== '.profiles';
  }

  return false;
}

export function StorageBrowser() {
  const serverConnections = useAppStore(s => s.serverConnections);
  const conn = serverConnections.find(c => c.connectionState === 'connected');
  const hub = conn?.hub;

  const [currentPath, setCurrentPath] = useState('.');
  const [entries, setEntries] = useState<StorageEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<Filter>('projects');

  // File viewer/editor
  const [editingFile, setEditingFile] = useState<string | null>(null);
  const [fileContent, setFileContent] = useState('');
  const [fileDirty, setFileDirty] = useState(false);
  const [saving, setSaving] = useState(false);

  // New item / upload
  const [showNewDir, setShowNewDir] = useState(false);
  const [newDirName, setNewDirName] = useState('');
  const [uploading, setUploading] = useState(false);

  const browse = useCallback(async (path: string) => {
    if (!hub) return;
    setLoading(true);
    setError(null);
    setEditingFile(null);
    try {
      const result = await hub.browseStorage(path);
      setEntries(result);
      setCurrentPath(path);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to browse');
    } finally {
      setLoading(false);
    }
  }, [hub]);

  useEffect(() => { browse('.'); }, [browse]);

  const filteredEntries = useMemo(() =>
    entries.filter(e => !shouldHide(e, filter, currentPath)),
  [entries, filter, currentPath]);

  const navigateUp = () => {
    if (currentPath === '.') return;
    const parts = currentPath.split('/');
    parts.pop();
    browse(parts.length === 0 ? '.' : parts.join('/'));
  };

  const openEntry = async (entry: StorageEntry) => {
    if (entry.IsDirectory) {
      browse(entry.Path);
    } else {
      if (!hub) return;
      try {
        const content = await hub.readStorageFile(entry.Path);
        setEditingFile(entry.Path);
        setFileContent(content);
        setFileDirty(false);
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Failed to read file');
      }
    }
  };

  const saveFile = async () => {
    if (!hub || !editingFile) return;
    setSaving(true);
    try {
      await hub.writeStorageFile(editingFile, fileContent);
      setFileDirty(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  };

  const downloadFile = async (entry: StorageEntry) => {
    if (!hub) return;
    try {
      const content = await hub.readStorageFile(entry.Path);
      const blob = new Blob([content], { type: 'text/plain' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = entry.Name;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to download');
    }
  };

  const deleteEntry = async (entry: StorageEntry) => {
    if (!hub) return;
    const msg = entry.IsDirectory
      ? `Delete folder "${entry.Name}"? (must be empty)`
      : `Delete file "${entry.Name}"?`;
    if (!confirm(msg)) return;
    try {
      await hub.deleteStorageEntry(entry.Path);
      browse(currentPath);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to delete');
    }
  };

  const createDir = async () => {
    if (!hub || !newDirName.trim()) return;
    const path = currentPath === '.' ? newDirName.trim() : `${currentPath}/${newDirName.trim()}`;
    try {
      await hub.createStorageDirectory(path);
      setShowNewDir(false);
      setNewDirName('');
      browse(currentPath);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create folder');
    }
  };

  const handleUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = e.target.files;
    if (!hub || !files?.length) return;
    setUploading(true);
    setError(null);
    try {
      for (const file of Array.from(files)) {
        const text = await file.text();
        const path = currentPath === '.' ? file.name : `${currentPath}/${file.name}`;
        await hub.writeStorageFile(path, text);
      }
      browse(currentPath);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Upload failed');
    } finally {
      setUploading(false);
      e.target.value = '';
    }
  };

  const breadcrumbs = currentPath === '.' ? ['Files'] : ['Files', ...currentPath.split('/')];
  const depth = currentPath === '.' ? 0 : currentPath.split('/').length;

  // Context hint: what level are we at?
  const levelHint = filter === 'projects' && currentPath === '.'
    ? 'Select a root to see its projects'
    : filter === 'projects' && depth === 1
    ? 'Projects in this root'
    : null;

  // ── File editor view ──
  if (editingFile) {
    const fileName = editingFile.split('/').pop() ?? editingFile;
    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={() => setEditingFile(null)}><BackArrow /> Back</button>
          <div className="storage-file-actions">
            {fileDirty && (
              <button className="btn btn-primary btn-sm" onClick={saveFile} disabled={saving}>
                {saving ? 'Saving...' : 'Save'}
              </button>
            )}
          </div>
        </div>
        <div className="storage-file-header">
          <FileIcon />
          <span className="storage-file-name">{fileName}</span>
          <span className="storage-file-path">{editingFile}</span>
        </div>
        {error && <div className="settings-error">{error}</div>}
        <textarea
          className="storage-editor"
          value={fileContent}
          onChange={e => { setFileContent(e.target.value); setFileDirty(true); }}
          spellCheck={false}
        />
      </>
    );
  }

  // ── Browse view ──
  return (
    <>
      <div className="settings-header">
        <h2>File Browser</h2>
        <div className="storage-header-actions">
          <button className="storage-action-btn" onClick={() => { setShowNewDir(true); setNewDirName(''); }} title="New folder">
            <FolderIcon />
            <span>New Folder</span>
          </button>
          <label className="storage-action-btn storage-upload-btn" title="Upload files">
            <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
              <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><polyline points="17 8 12 3 7 8" /><line x1="12" y1="3" x2="12" y2="15" />
            </svg>
            <span>{uploading ? 'Uploading...' : 'Upload'}</span>
            <input type="file" multiple onChange={handleUpload} hidden disabled={uploading} />
          </label>
        </div>
      </div>

      <div className="storage-filter-bar">
        {(['projects', 'roots', 'profiles', 'all'] as Filter[]).map(f => (
          <button
            key={f}
            className={`storage-filter-btn ${filter === f ? 'active' : ''}`}
            onClick={() => { setFilter(f); if (currentPath !== '.') browse('.'); }}
          >
            {f === 'projects' ? 'Projects' : f === 'roots' ? 'Roots' : f === 'profiles' ? 'Profiles' : 'All'}
          </button>
        ))}
      </div>

      <div className="storage-breadcrumbs">
        {breadcrumbs.map((crumb, i) => (
          <span key={i}>
            {i > 0 && <span className="storage-breadcrumb-sep">/</span>}
            <button
              className="storage-breadcrumb"
              onClick={() => {
                if (i === 0) browse('.');
                else browse(currentPath.split('/').slice(0, i).join('/'));
              }}
            >{crumb}</button>
          </span>
        ))}
        {levelHint && <span className="storage-level-hint">{levelHint}</span>}
      </div>

      {error && <div className="settings-error">{error}</div>}

      {showNewDir && (
        <div className="storage-new-dir">
          <FolderIcon />
          <input
            type="text"
            value={newDirName}
            onChange={e => setNewDirName(e.target.value)}
            placeholder="Folder name"
            autoFocus
            onKeyDown={e => {
              if (e.key === 'Enter') createDir();
              if (e.key === 'Escape') setShowNewDir(false);
            }}
          />
          <button className="btn btn-primary btn-sm" onClick={createDir} disabled={!newDirName.trim()}>Create</button>
          <button className="storage-new-cancel" onClick={() => setShowNewDir(false)}>
            <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
              <line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" />
            </svg>
          </button>
        </div>
      )}

      {loading ? (
        <div className="settings-empty">Loading...</div>
      ) : filteredEntries.length === 0 ? (
        <div className="settings-empty">
          {currentPath !== '.' && (
            <button className="storage-up-btn" onClick={navigateUp}>
              <BackArrow /> Up
            </button>
          )}
          {currentPath === '.' ? 'No items match this filter' : 'Empty directory'}
        </div>
      ) : (
        <div className="storage-list">
          {currentPath !== '.' && (
            <button className="storage-entry storage-entry-up" onClick={navigateUp}>
              <BackArrow />
              <span className="storage-entry-name">..</span>
            </button>
          )}
          {filteredEntries.map(entry => {
            const badge = filter === 'projects' && entry.IsDirectory
              ? (depth === 0 ? 'root' : depth === 1 ? 'project' : null)
              : null;
            return (
            <div key={entry.Path} className="storage-entry">
              <button className="storage-entry-main" onClick={() => openEntry(entry)}>
                {entry.IsDirectory ? <FolderIcon /> : <FileIcon />}
                <span className="storage-entry-name">{entry.Name}</span>
                {badge && <span className={`storage-badge storage-badge-${badge}`}>{badge}</span>}
                <span className="storage-entry-meta">
                  {!entry.IsDirectory && formatSize(entry.Size)}
                </span>
              </button>
              {!entry.IsDirectory && (
                <button className="storage-entry-action" onClick={() => downloadFile(entry)} title="Download">
                  <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
                    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><polyline points="7 10 12 15 17 10" /><line x1="12" y1="15" x2="12" y2="3" />
                  </svg>
                </button>
              )}
              <button className="storage-entry-action storage-entry-action-danger" onClick={() => deleteEntry(entry)} title="Delete">
                <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
                  <polyline points="3 6 5 6 21 6" /><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
                </svg>
              </button>
            </div>
            );
          })}
        </div>
      )}
    </>
  );
}
