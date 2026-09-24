/**
 * A GodModeHub stand-in that serves fixed lists and records what the client sends. Tests drive the
 * server's pushes through `callbacks`. Each test file still mocks '../signalr/hub' and
 * '../services/hostApi' itself (vi.mock is hoisted per file).
 */
import type { HubCallbacks } from '../signalr/hub';
import type {
  ProjectSummary, ProjectRootInfo, ProfileInfo, ProjectState, ProjectStatus, ServerInfo,
} from '../signalr/types';
import { useAppStore, type ServerConnection } from '../store';

export class FakeHub {
  callbacks: HubCallbacks = {};
  projects: ProjectSummary[];
  roots: ProjectRootInfo[];
  /** Every text sent through ReplyAndResume, and every AnswerQuestion's answers, in order. */
  replies: { projectId: string; text: string }[] = [];
  answers: { projectId: string; requestId: string; answers: Record<string, string> }[] = [];
  constructor(projects: ProjectSummary[], roots: ProjectRootInfo[]) {
    this.projects = projects;
    this.roots = roots;
  }
  setCallbacks(callbacks: HubCallbacks) { this.callbacks = callbacks; }
  async connect() { this.callbacks.onStateChanged?.('connected'); }
  async disconnect() {}
  async listProjects() { return this.projects; }
  async listProjectRoots() { return this.roots; }
  async listProfiles(): Promise<ProfileInfo[]> { return []; }
  async getAttention() { return []; }
  async subscribeProject() {}
  async unsubscribeProject() {}
  async replyAndResume(projectId: string, text: string) { this.replies.push({ projectId, text }); }
  async answerQuestion(projectId: string, requestId: string, answers: Record<string, string>) {
    this.answers.push({ projectId, requestId, answers });
  }
}

export const project = (id: string, name: string, state: ProjectState, updatedAt: string): ProjectSummary => ({
  Id: id, Name: name, State: state, UpdatedAt: updatedAt, RootName: 'work', ProfileName: 'Default',
});
export const root: ProjectRootInfo = { Name: 'work', ProfileName: 'Default', Actions: [] } as unknown as ProjectRootInfo;
export const status = (id: string, state: ProjectState) =>
  ({ Id: id, Name: id, State: state, UpdatedAt: '2026-09-24T12:00:00Z', RootName: 'work', ProfileName: 'Default' }) as ProjectStatus;

/** Puts the hubs in the store as servers, keyed by their id, and connects each. */
export async function connectServers(hubs: Record<string, FakeHub>) {
  const conn = (id: string, hub: FakeHub): ServerConnection => ({
    serverInfo: { Id: id, Name: `Server ${id}`, Type: 'local', State: 'Running' } as ServerInfo,
    hub: hub as unknown as ServerConnection['hub'],
    connectionState: 'disconnected', projects: [], roots: [], profiles: [],
  });
  useAppStore.setState({ serverConnections: Object.entries(hubs).map(([id, hub]) => conn(id, hub)) });
  for (const id of Object.keys(hubs)) await useAppStore.getState().connectServer(id);
}
