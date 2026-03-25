import { useAppStore } from '../../store';

interface Props {
  serverId: string;
}

export function EditServer({ serverId }: Props) {
  const conn = useAppStore(s => s.serverConnections.find(c => c.serverInfo.Id === serverId));
  const removeServer = useAppStore(s => s.removeServer);
  const setEditServerId = useAppStore(s => s.setEditServerId);

  if (!conn) return null;

  const handleDelete = () => {
    if (confirm('Remove this server?')) {
      removeServer(serverId);
    }
  };

  return (
    <div className="modal-overlay" onClick={() => setEditServerId(null)}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <h2>Server Settings</h2>
        <div className="form-group">
          <label>Name</label>
          <input type="text" value={conn.serverInfo.Name} readOnly />
        </div>
        <div className="form-group">
          <label>URL</label>
          <input type="text" value={conn.serverInfo.Url ?? ''} readOnly />
        </div>
        <div className="form-group">
          <label>Type</label>
          <input type="text" value={conn.serverInfo.Type} readOnly />
        </div>
        <div className="btn-group">
          <button className="btn btn-danger" onClick={handleDelete}>Remove</button>
          <button className="btn btn-secondary" onClick={() => setEditServerId(null)}>Close</button>
        </div>
      </div>
    </div>
  );
}
