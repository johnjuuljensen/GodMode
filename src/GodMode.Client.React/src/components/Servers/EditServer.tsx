import { useAppStore } from '../../store';
import '../settings-common.css';

interface Props {
  serverId: string;
}

export function EditServer({ serverId }: Props) {
  const conn = useAppStore(s => s.serverConnections.find(c => c.serverInfo.Id === serverId));
  const removeServer = useAppStore(s => s.removeServer);

  if (!conn) return null;

  const handleDelete = () => {
    if (confirm('Remove this server?')) {
      removeServer(serverId);
    }
  };

  return (
    <>
      <div className="settings-header">
        <h2>Server Settings</h2>
      </div>

      <div className="settings-list">
        <div className="settings-item" style={{ flexDirection: 'column', alignItems: 'stretch', gap: 8 }}>
          <div className="settings-item-name">{conn.serverInfo.Name}</div>
          <div className="settings-item-meta">
            <span className="settings-badge">{conn.serverInfo.Type}</span>
            <span>{conn.serverInfo.Url ?? ''}</span>
          </div>
        </div>
      </div>

      <div className="settings-form-actions">
        <button className="btn btn-danger" onClick={handleDelete} style={{ width: '100%', padding: 11, fontSize: 14 }}>Remove Server</button>
      </div>
    </>
  );
}
