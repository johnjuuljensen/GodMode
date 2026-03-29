import { useAppStore } from '../../store';
import './AppSettings.css';

const FEATURES: { key: 'roots' | 'mcp' | 'profiles'; label: string; description: string }[] = [
  { key: 'roots', label: 'Manage Roots', description: 'Root manager button and root picker in project creation' },
  { key: 'mcp', label: 'MCP Features', description: 'MCP browser, server badges, and MCP panel in project creation' },
  { key: 'profiles', label: 'Profiles', description: 'Profile settings, profile filter, and create profile button' },
];

export function AppSettings() {
  const setShowAppSettings = useAppStore(s => s.setShowAppSettings);
  const featureVisibility = useAppStore(s => s.featureVisibility);
  const setFeatureVisibility = useAppStore(s => s.setFeatureVisibility);

  return (
    <div className="modal-overlay" onClick={() => setShowAppSettings(false)}>
      <div className="modal app-settings-modal" onClick={e => e.stopPropagation()}>
        <h2>Settings</h2>

        <div className="settings-section">
          <h3 className="settings-section-title">Feature Visibility</h3>
          <p className="settings-section-desc">Toggle which features appear in the toolbar and UI.</p>

          <div className="settings-toggle-list">
            {FEATURES.map(f => (
              <label key={f.key} className="settings-toggle-row">
                <div className="settings-toggle-info">
                  <span className="settings-toggle-label">{f.label}</span>
                  <span className="settings-toggle-desc">{f.description}</span>
                </div>
                <div className="settings-toggle-switch">
                  <input
                    type="checkbox"
                    checked={featureVisibility[f.key]}
                    onChange={e => setFeatureVisibility(f.key, e.target.checked)}
                  />
                  <span className="toggle-track" />
                </div>
              </label>
            ))}
          </div>
        </div>

        <div className="btn-group">
          <button className="btn btn-secondary" onClick={() => setShowAppSettings(false)}>Close</button>
        </div>
      </div>
    </div>
  );
}
