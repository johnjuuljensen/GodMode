import { useAppStore } from '../store';

export function AppSettings() {
  const setShowAppSettings = useAppStore(s => s.setShowAppSettings);
  const featureRoots = useAppStore(s => s.featureRoots);
  const featureMcp = useAppStore(s => s.featureMcp);
  const featureProfiles = useAppStore(s => s.featureProfiles);
  const setFeatureFlag = useAppStore(s => s.setFeatureFlag);

  return (
    <div className="modal-overlay" onClick={() => setShowAppSettings(false)}>
      <div className="modal" onClick={e => e.stopPropagation()} style={{ maxWidth: 360 }}>
        <h2>Settings</h2>

        <div className="form-group">
          <label className="form-toggle">
            <input type="checkbox" checked={featureRoots} onChange={e => setFeatureFlag('featureRoots', e.target.checked)} />
            <span>Root Manager</span>
          </label>
          <div className="form-description">Show Root Manager button in header</div>
        </div>

        <div className="form-group">
          <label className="form-toggle">
            <input type="checkbox" checked={featureMcp} onChange={e => setFeatureFlag('featureMcp', e.target.checked)} />
            <span>MCP Servers</span>
          </label>
          <div className="form-description">Show MCP config button and badges</div>
        </div>

        <div className="form-group">
          <label className="form-toggle">
            <input type="checkbox" checked={featureProfiles} onChange={e => setFeatureFlag('featureProfiles', e.target.checked)} />
            <span>Profiles</span>
          </label>
          <div className="form-description">Show profile filter and settings</div>
        </div>

        <div className="btn-group">
          <button className="btn btn-secondary" onClick={() => setShowAppSettings(false)}>Close</button>
        </div>
      </div>
    </div>
  );
}
