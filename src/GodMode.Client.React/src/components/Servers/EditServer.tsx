import { useAppStore } from '../../store';

interface Props {
  index: number;
}

export function EditServer({ index }: Props) {
  const server = useAppStore(s => s.servers[index]);
  const removeServer = useAppStore(s => s.removeServer);
  const setEditServerIndex = useAppStore(s => s.setEditServerIndex);

  if (!server) return null;

  const handleDelete = () => {
    if (confirm('Remove this server?')) {
      removeServer(index);
    }
  };

  return (
    <div className="modal-overlay" onClick={() => setEditServerIndex(null)}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <h2>Server Settings</h2>
        <div className="form-group">
          <label>Name</label>
          <input type="text" value={server.serverInfo.Name} readOnly />
        </div>
        <div className="form-group">
          <label>URL</label>
          <input type="text" value={server.serverInfo.Url ?? ''} readOnly />
        </div>
        <div className="form-group">
          <label>Type</label>
          <input type="text" value={server.serverInfo.Type} readOnly />
        </div>
        <div className="btn-group">
          <button className="btn btn-danger" onClick={handleDelete}>Remove</button>
          <button className="btn btn-secondary" onClick={() => setEditServerIndex(null)}>Close</button>
        </div>
      </div>
    </div>
  );
}
