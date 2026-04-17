import { useAppStore } from '../store';
import { Toggle } from './settings-shared';
import './settings-common.css';

const FLAGS: { key: 'featureRoots' | 'featureMcp' | 'featureProfiles'; label: string; desc: string }[] = [
  { key: 'featureRoots',    label: 'Root Manager',  desc: 'Show Root Manager in settings menu' },
  { key: 'featureMcp',      label: 'MCP Servers',   desc: 'Show MCP config and badges' },
  { key: 'featureProfiles', label: 'Profiles',      desc: 'Show profile filter and settings' },
];

export function AppSettings() {
  const featureRoots = useAppStore(s => s.featureRoots);
  const featureMcp = useAppStore(s => s.featureMcp);
  const featureProfiles = useAppStore(s => s.featureProfiles);
  const setFeatureFlag = useAppStore(s => s.setFeatureFlag);

  const values: Record<string, boolean> = { featureRoots, featureMcp, featureProfiles };

  return (
    <>
      <div className="settings-header">
        <h2>Settings</h2>
      </div>
      <div className="settings-list">
        {FLAGS.map(f => (
          <div key={f.key} className="settings-item">
            <div className="settings-item-info">
              <div className="settings-item-name">{f.label}</div>
              <div className="settings-item-desc">{f.desc}</div>
            </div>
            <Toggle checked={values[f.key]} onChange={v => setFeatureFlag(f.key, v)} />
          </div>
        ))}
      </div>
    </>
  );
}
