import { useState, useEffect, useCallback, useMemo } from 'react';
import { useAppStore } from '../../store';
import type { ScheduleInfo, ScheduleConfig, ScheduleTarget } from '../../signalr/types';
import { Toggle, RowDelete } from '../settings-shared';
import '../settings-common.css';
import './ScheduleSettings.css';

const BackArrow = () => (
  <svg width={16} height={16} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round"><path d="M10 4L6 8l4 4"/></svg>
);

// ── Cron Builder ──

type Frequency = 'minutes' | 'hourly' | 'daily' | 'weekly' | 'monthly' | 'custom';

const WEEKDAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const WEEKDAY_CRON = [1, 2, 3, 4, 5, 6, 0]; // cron day-of-week values

interface CronState {
  frequency: Frequency;
  minuteInterval: number;    // for "every X minutes"
  hour: number;              // for daily/weekly/monthly
  minute: number;            // for daily/weekly/monthly/hourly
  weekdays: boolean[];       // for weekly (Mon-Sun)
  monthDay: number;          // for monthly (1-31)
  customCron: string;        // for custom/advanced
}

function parseCronToState(cron: string): CronState {
  const parts = cron.trim().split(/\s+/);
  const defaults: CronState = {
    frequency: 'daily', minuteInterval: 15, hour: 9, minute: 0,
    weekdays: [true, true, true, true, true, false, false],
    monthDay: 1, customCron: cron,
  };
  if (parts.length !== 5) return { ...defaults, frequency: 'custom' };

  const [min, hr, dom, , dow] = parts;

  // Every X minutes: */X * * * *
  if (min.startsWith('*/') && hr === '*' && dom === '*' && dow === '*') {
    const interval = parseInt(min.slice(2));
    if (!isNaN(interval)) return { ...defaults, frequency: 'minutes', minuteInterval: interval };
  }

  // Hourly: M * * * *
  if (hr === '*' && dom === '*' && dow === '*') {
    const m = parseInt(min);
    if (!isNaN(m)) return { ...defaults, frequency: 'hourly', minute: m };
  }

  // Monthly: M H D * *
  if (dow === '*' && dom !== '*') {
    const m = parseInt(min), h = parseInt(hr), d = parseInt(dom);
    if (!isNaN(m) && !isNaN(h) && !isNaN(d))
      return { ...defaults, frequency: 'monthly', minute: m, hour: h, monthDay: d };
  }

  // Weekly: M H * * D,D,D
  if (dom === '*' && dow !== '*') {
    const m = parseInt(min), h = parseInt(hr);
    if (!isNaN(m) && !isNaN(h)) {
      const days = dow.split(',').map(Number).filter(n => !isNaN(n));
      const weekdays = [false, false, false, false, false, false, false];
      for (const d of days) {
        const idx = WEEKDAY_CRON.indexOf(d);
        if (idx >= 0) weekdays[idx] = true;
      }
      return { ...defaults, frequency: 'weekly', minute: m, hour: h, weekdays };
    }
  }

  // Daily: M H * * *
  if (dom === '*' && dow === '*') {
    const m = parseInt(min), h = parseInt(hr);
    if (!isNaN(m) && !isNaN(h))
      return { ...defaults, frequency: 'daily', minute: m, hour: h };
  }

  return { ...defaults, frequency: 'custom' };
}

function stateToCron(s: CronState): string {
  switch (s.frequency) {
    case 'minutes':
      return `*/${s.minuteInterval} * * * *`;
    case 'hourly':
      return `${s.minute} * * * *`;
    case 'daily':
      return `${s.minute} ${s.hour} * * *`;
    case 'weekly': {
      const days = s.weekdays
        .map((on, i) => on ? WEEKDAY_CRON[i] : -1)
        .filter(d => d >= 0);
      if (days.length === 0) return `${s.minute} ${s.hour} * * *`; // fallback to daily
      return `${s.minute} ${s.hour} * * ${days.join(',')}`;
    }
    case 'monthly':
      return `${s.minute} ${s.hour} ${s.monthDay} * *`;
    case 'custom':
      return s.customCron;
  }
}

function describeCron(s: CronState): string {
  const timeStr = `${String(s.hour).padStart(2, '0')}:${String(s.minute).padStart(2, '0')}`;
  switch (s.frequency) {
    case 'minutes':
      return `Every ${s.minuteInterval} minutes`;
    case 'hourly':
      return s.minute === 0 ? 'Every hour, on the hour' : `Every hour at :${String(s.minute).padStart(2, '0')}`;
    case 'daily':
      return `Every day at ${timeStr}`;
    case 'weekly': {
      const dayNames = s.weekdays
        .map((on, i) => on ? WEEKDAYS[i] : null)
        .filter(Boolean);
      if (dayNames.length === 0) return `No days selected`;
      if (dayNames.length === 7) return `Every day at ${timeStr}`;
      if (dayNames.length === 5 && !s.weekdays[5] && !s.weekdays[6]) return `Weekdays at ${timeStr}`;
      if (dayNames.length === 2 && s.weekdays[5] && s.weekdays[6]) return `Weekends at ${timeStr}`;
      return `${dayNames.join(', ')} at ${timeStr}`;
    }
    case 'monthly':
      return `Monthly on day ${s.monthDay} at ${timeStr}`;
    case 'custom':
      return `Custom: ${s.customCron}`;
  }
}

function CronBuilder({ cron, onChange }: { cron: string; onChange: (cron: string) => void }) {
  const [state, setState] = useState(() => parseCronToState(cron));

  const update = useCallback((patch: Partial<CronState>) => {
    setState(prev => {
      const next = { ...prev, ...patch };
      onChange(stateToCron(next));
      return next;
    });
  }, [onChange]);

  // Sync from external cron changes (e.g. on edit open)
  useEffect(() => {
    setState(parseCronToState(cron));
  }, [cron]);

  const description = useMemo(() => describeCron(state), [state]);

  return (
    <div className="cron-builder">
      <div className="cron-builder-frequency">
        <label>Repeat</label>
        <div className="cron-freq-buttons">
          {([
            ['minutes', 'Minutes'],
            ['hourly', 'Hourly'],
            ['daily', 'Daily'],
            ['weekly', 'Weekly'],
            ['monthly', 'Monthly'],
            ['custom', 'Advanced'],
          ] as [Frequency, string][]).map(([f, label]) => (
            <button
              key={f}
              className={`cron-freq-btn ${state.frequency === f ? 'active' : ''}`}
              onClick={() => update({ frequency: f })}
              type="button"
            >{label}</button>
          ))}
        </div>
      </div>

      {state.frequency === 'minutes' && (
        <div className="cron-builder-row">
          <label>Every</label>
          <select value={state.minuteInterval} onChange={e => update({ minuteInterval: parseInt(e.target.value) })}>
            {[5, 10, 15, 20, 30].map(n => <option key={n} value={n}>{n} minutes</option>)}
          </select>
        </div>
      )}

      {state.frequency === 'hourly' && (
        <div className="cron-builder-row">
          <label>At minute</label>
          <select value={state.minute} onChange={e => update({ minute: parseInt(e.target.value) })}>
            {[0, 5, 10, 15, 20, 30, 45].map(n => <option key={n} value={n}>:{String(n).padStart(2, '0')}</option>)}
          </select>
        </div>
      )}

      {(state.frequency === 'daily' || state.frequency === 'weekly' || state.frequency === 'monthly') && (
        <div className="cron-builder-row">
          <label>At</label>
          <div className="cron-time-picker">
            <select value={state.hour} onChange={e => update({ hour: parseInt(e.target.value) })}>
              {Array.from({ length: 24 }, (_, i) => (
                <option key={i} value={i}>{String(i).padStart(2, '0')}</option>
              ))}
            </select>
            <span className="cron-time-sep">:</span>
            <select value={state.minute} onChange={e => update({ minute: parseInt(e.target.value) })}>
              {[0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55].map(n => (
                <option key={n} value={n}>{String(n).padStart(2, '0')}</option>
              ))}
            </select>
          </div>
        </div>
      )}

      {state.frequency === 'weekly' && (
        <div className="cron-builder-row">
          <label>On</label>
          <div className="cron-weekday-picker">
            {WEEKDAYS.map((day, i) => (
              <button
                key={day}
                type="button"
                className={`cron-weekday-btn ${state.weekdays[i] ? 'active' : ''}`}
                onClick={() => {
                  const next = [...state.weekdays];
                  next[i] = !next[i];
                  update({ weekdays: next });
                }}
              >{day}</button>
            ))}
          </div>
        </div>
      )}

      {state.frequency === 'monthly' && (
        <div className="cron-builder-row">
          <label>Day of month</label>
          <select value={state.monthDay} onChange={e => update({ monthDay: parseInt(e.target.value) })}>
            {Array.from({ length: 28 }, (_, i) => (
              <option key={i + 1} value={i + 1}>{i + 1}</option>
            ))}
          </select>
        </div>
      )}

      {state.frequency === 'custom' && (
        <div className="cron-builder-row">
          <label>Cron expression</label>
          <input
            type="text"
            value={state.customCron}
            onChange={e => update({ customCron: e.target.value })}
            placeholder="min hour dom month dow"
          />
          <div className="form-hint">5-field cron: minute (0-59) hour (0-23) day (1-31) month (1-12) weekday (0-6, 0=Sun)</div>
        </div>
      )}

      <div className="cron-description">{description}</div>
    </div>
  );
}

// ── Main Component ──

type View = 'list' | 'create' | 'edit';

export function ScheduleSettings() {
  const serverConnections = useAppStore(s => s.serverConnections);

  const conn = serverConnections.find(c => c.connectionState === 'connected');
  const hub = conn?.hub;
  const profiles = conn?.profiles ?? [];
  const roots = conn?.roots ?? [];

  const [schedules, setSchedules] = useState<ScheduleInfo[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<View>('list');
  const [editingSchedule, setEditingSchedule] = useState<ScheduleInfo | null>(null);

  // Form state
  const [formProfile, setFormProfile] = useState(profiles[0]?.Name ?? '');
  const [formName, setFormName] = useState('');
  const [formDescription, setFormDescription] = useState('');
  const [formCron, setFormCron] = useState('0 9 * * *');
  const [formEnabled, setFormEnabled] = useState(true);
  const [formRootName, setFormRootName] = useState('');
  const [formActionName, setFormActionName] = useState('');
  const [formProjectName, setFormProjectName] = useState('');
  const [formPrompt, setFormPrompt] = useState('');
  const [formReuseProject, setFormReuseProject] = useState(false);

  const filteredRoots = roots.filter(r => r.ProfileName === formProfile);

  const loadSchedules = useCallback(async () => {
    if (!hub) return;
    setLoading(true);
    setError(null);
    try {
      const scheduleResults = await Promise.all(
        profiles.map(p => hub.getSchedules(p.Name)),
      );
      setSchedules(scheduleResults.flat());
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load schedules');
    } finally {
      setLoading(false);
    }
  }, [hub, profiles]);

  useEffect(() => { loadSchedules(); }, [loadSchedules]);

  useEffect(() => {
    const available = roots.filter(r => r.ProfileName === formProfile);
    if (available.length > 0 && !available.find(r => r.Name === formRootName)) {
      setFormRootName(available[0].Name);
      setFormActionName('');
    } else if (available.length === 0) {
      setFormRootName('');
      setFormActionName('');
    }
  }, [formProfile, roots, formRootName]);

  const resetForm = () => {
    setFormName('');
    setFormDescription('');
    setFormCron('0 9 * * *');
    setFormEnabled(true);
    setFormRootName(filteredRoots[0]?.Name ?? '');
    setFormActionName('');
    setFormProjectName('');
    setFormPrompt('');
    setFormReuseProject(false);
    setEditingSchedule(null);
  };

  const goList = () => { setView('list'); setError(null); resetForm(); };

  const openCreate = () => {
    resetForm();
    setFormProfile(profiles[0]?.Name ?? '');
    setView('create');
  };

  const openEdit = (s: ScheduleInfo) => {
    setEditingSchedule(s);
    setFormProfile(s.ProfileName);
    setFormName(s.Name);
    setFormDescription(s.Description ?? '');
    setFormCron(s.Cron);
    setFormEnabled(s.Enabled);
    setFormRootName(s.Target?.RootName ?? '');
    setFormActionName(s.Target?.ActionName ?? '');
    const inputs = s.Target?.Inputs as Record<string, unknown> | undefined;
    setFormProjectName((inputs?.name as string) ?? '');
    setFormPrompt((inputs?.prompt as string) ?? '');
    setFormReuseProject(s.Target?.ReuseProject ?? false);
    setView('edit');
  };

  const buildConfig = (): ScheduleConfig => {
    const inputs: Record<string, unknown> = {};
    if (formProjectName.trim()) inputs.name = formProjectName.trim();
    if (formPrompt.trim()) inputs.prompt = formPrompt.trim();
    const target: ScheduleTarget = {
      RootName: formRootName || null,
      ActionName: formActionName || null,
      Inputs: Object.keys(inputs).length > 0 ? inputs : null,
      ReuseProject: formReuseProject || undefined,
    };
    return {
      Description: formDescription.trim() || null,
      Enabled: formEnabled,
      Cron: formCron.trim(),
      Target: target,
    };
  };

  const handleCreate = async () => {
    if (!hub || !formName.trim() || !formProfile || !formCron.trim()) return;
    setError(null);
    try {
      await hub.createSchedule(formProfile, formName.trim(), buildConfig());
      goList();
      await loadSchedules();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create schedule');
    }
  };

  const handleUpdate = async () => {
    if (!hub || !editingSchedule || !formCron.trim()) return;
    setError(null);
    try {
      await hub.updateSchedule(editingSchedule.ProfileName, editingSchedule.Name, buildConfig());
      goList();
      await loadSchedules();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to update schedule');
    }
  };

  const handleDelete = async (profileName: string, name: string) => {
    if (!hub) return;
    setError(null);
    try {
      await hub.deleteSchedule(profileName, name);
      await loadSchedules();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to delete schedule');
    }
  };

  const handleToggleEnabled = async (s: ScheduleInfo) => {
    if (!hub) return;
    setError(null);
    try {
      await hub.toggleSchedule(s.ProfileName, s.Name, !s.Enabled);
      await loadSchedules();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to toggle schedule');
    }
  };

  const selectedRoot = filteredRoots.find(r => r.Name === formRootName);
  const actions = selectedRoot?.Actions ?? [];

  // Human-readable cron for list view
  const describeCronInList = (cron: string) => describeCron(parseCronToState(cron));

  // ── Create / Edit form ──
  if (view === 'create' || view === 'edit') {
    const isEdit = view === 'edit';
    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={goList}><BackArrow /> Schedules</button>
        </div>
        <div className="settings-header"><h2>{isEdit ? 'Edit Schedule' : 'New Schedule'}</h2></div>
        {error && <div className="settings-error">{error}</div>}

        {!isEdit && (
          <div className="form-group">
            <label>Profile</label>
            <select value={formProfile} onChange={e => setFormProfile(e.target.value)}>
              {profiles.map(p => <option key={p.Name} value={p.Name}>{p.Name}</option>)}
            </select>
          </div>
        )}

        <div className="form-group">
          <label>Name</label>
          <input type="text" value={formName} onChange={e => setFormName(e.target.value)} placeholder="e.g. daily-review" disabled={isEdit} autoFocus={!isEdit} />
        </div>

        <div className="form-group">
          <label>Description</label>
          <input type="text" value={formDescription} onChange={e => setFormDescription(e.target.value)} placeholder="Optional description" />
        </div>

        <div className="form-group">
          <label>Root</label>
          <select value={formRootName} onChange={e => setFormRootName(e.target.value)}>
            {filteredRoots.map(r => <option key={r.Name} value={r.Name}>{r.Name}</option>)}
          </select>
        </div>

        {actions.length > 0 && (
          <div className="form-group">
            <label>Action</label>
            <select value={formActionName} onChange={e => setFormActionName(e.target.value)}>
              <option value="">(default)</option>
              {actions.map(a => <option key={a.Name} value={a.Name}>{a.Name}</option>)}
            </select>
          </div>
        )}

        <div className="form-group">
          <label>Project Name</label>
          <input type="text" value={formProjectName} onChange={e => setFormProjectName(e.target.value)}
            placeholder="e.g. daily-review-{date} (supports {date}, {time}, {datetime})" />
          <div className="form-description">Name for the created project. Leave empty for auto-generated timestamp name.</div>
        </div>

        <div className="form-group">
          <label>Prompt</label>
          <textarea value={formPrompt} onChange={e => setFormPrompt(e.target.value)}
            placeholder="What should Claude do when this schedule fires?"
            rows={3} />
          <div className="form-description">The initial prompt for the Claude session. Leave empty to use the root's default.</div>
        </div>

        <div className="schedule-enabled-row">
          <Toggle checked={formReuseProject} onChange={setFormReuseProject} />
          <div>
            <span className="schedule-enabled-label">Reuse same project</span>
            <div className="form-description" style={{ marginTop: 2 }}>
              {formReuseProject
                ? 'Each trigger reuses the same project folder — no new projects created. Good for recurring checks.'
                : 'Each trigger creates a new project. Good for daily reports or unique tasks.'}
            </div>
          </div>
        </div>

        <div className="form-group">
          <label>Schedule</label>
          <CronBuilder cron={formCron} onChange={setFormCron} />
        </div>

        <div className="schedule-enabled-row">
          <Toggle checked={formEnabled} onChange={setFormEnabled} />
          <span className="schedule-enabled-label">Enabled</span>
        </div>

        <div className="settings-form-actions">
          <button className="btn btn-primary" onClick={isEdit ? handleUpdate : handleCreate}
            disabled={!formCron.trim() || (!isEdit && !formName.trim())}>
            {isEdit ? 'Save' : 'Create Schedule'}
          </button>
        </div>
      </>
    );
  }

  // ── List view ──
  return (
    <>
      <div className="settings-header">
        <h2>Schedules</h2>
        <button className="settings-add-btn" onClick={openCreate}>
          <span className="plus">+</span> Add
        </button>
      </div>

      {error && <div className="settings-error">{error}</div>}

      {loading ? (
        <div className="settings-empty">Loading...</div>
      ) : schedules.length === 0 ? (
        <div className="settings-empty">No schedules configured. Tap Add to create one.</div>
      ) : (
        <>
          <div className="settings-list">
            {schedules.map(s => (
              <div key={`${s.ProfileName}:${s.Name}`} className="settings-item settings-item-stacked">
                <div className="settings-item-row">
                  <div className="settings-item-info" style={{ cursor: 'pointer' }} onClick={() => openEdit(s)}>
                    <div className="settings-item-name">
                      {s.Name}
                      {!s.Enabled && <span className="settings-badge disabled">disabled</span>}
                    </div>
                    <div className="settings-item-meta">
                      <span className="settings-badge">{s.ProfileName}</span>
                      <span className="settings-badge">{describeCronInList(s.Cron)}</span>
                      {s.Target?.RootName && <span>{s.Target.RootName}{s.Target.ActionName ? ` / ${s.Target.ActionName}` : ''}</span>}
                    </div>
                    {s.Description && <div className="settings-item-desc">{s.Description}</div>}
                  </div>
                  <div className="settings-item-actions">
                    <Toggle checked={s.Enabled} onChange={() => handleToggleEnabled(s)} />
                    <RowDelete onDelete={() => handleDelete(s.ProfileName, s.Name)} />
                  </div>
                </div>
                {s.NextRunDisplay && (
                  <div className="settings-code-block">
                    <span className="settings-item-desc">Next run: {s.NextRunDisplay}</span>
                  </div>
                )}
              </div>
            ))}
          </div>
          <div className="settings-count">{schedules.length} schedule{schedules.length !== 1 ? 's' : ''}</div>
        </>
      )}
    </>
  );
}
