import { useEffect, useState } from 'react';
import { KindLabel } from '../KindLabel/KindLabel';
import { adoptFolder, listUnmanaged } from '../../adoptFolder';
import type { UnmanagedFolder } from '../../signalr/types';

interface Props {
  serverId: string;
  profileName: string;
  rootName: string;
  /** How many sessions the root lists: when it changes (an adopt, a delete, a forget), the folders are read again. */
  sessionCount: number;
}

type Listing = { folders: UnmanagedFolder[]; error?: undefined } | { folders?: undefined; error: string };

/** What a candidate says it is, beside its name: its kind, else its branch when its inputs give one. */
const branchOf = (folder: UnmanagedFolder) => typeof folder.Inputs?.branch === 'string' ? folder.Inputs.branch : null;

/**
 * A root's folders that GodMode does not manage (#370), folded under "Not in GodMode (N)" below its sessions:
 * a tap lists them, each with Adopt, which makes a session of it. Read from the server when the root is shown
 * and again whenever its sessions change; nothing is shown when there are none. A list script that fails
 * shows the row, and why, when it is opened.
 */
export function UnmanagedGroup({ serverId, profileName, rootName, sessionCount }: Props) {
  const [listing, setListing] = useState<Listing | null>(null);
  const [open, setOpen] = useState(false);
  const [adopting, setAdopting] = useState<string | null>(null);
  const [generation, setGeneration] = useState(0);

  useEffect(() => {
    let current = true;
    listUnmanaged(serverId, profileName, rootName).then(
      folders => { if (current) setListing({ folders }); },
      (err: unknown) => { if (current) setListing({ error: err instanceof Error ? err.message : String(err) }); });
    return () => { current = false; };
  }, [serverId, profileName, rootName, sessionCount, generation]);

  if (!listing || listing.folders?.length === 0) return null;

  const adopt = async (folder: UnmanagedFolder) => {
    setAdopting(folder.Path);
    try {
      await adoptFolder(serverId, profileName, rootName, folder);
    } finally {
      setAdopting(null);
      setGeneration(g => g + 1);
    }
  };

  const toggle = () => {
    // Opening reads them again: what the root offers may have changed on the host since
    if (!open) setGeneration(g => g + 1);
    setOpen(!open);
  };

  return (
    <div className="unmanaged-group">
      <button className="project-list-older unmanaged-toggle" onClick={toggle} aria-expanded={open}
        title={listing.error ? `The root's list failed: ${listing.error}` : 'Folders in this root that are not GodMode sessions'}>
        {listing.folders ? `Not in GodMode (${listing.folders.length})` : 'Not in GodMode (?)'}
      </button>
      {open && (!listing.folders
        ? <div className="unmanaged-error">{listing.error}</div>
        : listing.folders.map(folder => (
          <div key={folder.Path} className="unmanaged-item">
            <div className="project-info">
              <div className="project-name-row">
                <div className="project-name">{folder.Name}</div>
                {folder.Kind && <KindLabel kind={folder.Kind} />}
              </div>
              {!folder.Kind && branchOf(folder) && <div className="project-meta">{branchOf(folder)}</div>}
            </div>
            <button className="unmanaged-adopt-btn" onClick={() => void adopt(folder)} disabled={adopting !== null}
              aria-label={`Adopt ${folder.Name}`}>
              {adopting === folder.Path ? 'Adopting…' : 'Adopt'}
            </button>
          </div>
        )))}
    </div>
  );
}
