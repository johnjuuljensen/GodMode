/** Connector catalog — curated list of MCP servers users can add to profiles. */

export interface CatalogEnvField {
  label: string;
  description: string;
  secret: boolean;
  required: boolean;
  docsUrl?: string;
}

export interface CatalogArgTemplate {
  label: string;
  description: string;
  secret: boolean;
  required: boolean;
}

/** Template for an HTTP header value — user fills in the value, stored in Headers on the config. */
export interface CatalogHeaderTemplate {
  label: string;
  description: string;
  secret: boolean;
  required: boolean;
  /** Header value template — use {value} as placeholder for user input, e.g. "Bearer {value}" */
  valueTemplate: string;
  docsUrl?: string;
}

/** Prerequisite step that must be completed before adding a connector. */
export interface CatalogSetupStep {
  /** Unique key for this step */
  key: string;
  /** Human label for the step */
  label: string;
  /** Explanation shown below the label */
  description: string;
  /** CLI command to check availability (e.g. "gws") — server runs `which <command>` */
  checkCommand?: string;
  /** Install instruction shown when checkCommand fails */
  installHint?: string;
  /** URL to installation docs */
  installUrl?: string;
  /** Setup command the user must run (e.g. "gws auth setup") — shown as copyable instruction */
  setupCommand?: string;
  /** URL to setup/auth docs */
  setupUrl?: string;
}

export interface CatalogConnector {
  id: string;
  name: string;
  description: string;
  category: string;
  /** App manifest (YAML/JSON) that can be copied to clipboard for quick setup */
  manifest?: string;
  stability: 'stable' | 'beta';
  maintainer: string;
  source: string;
  docsUrl: string;
  logoUrl: string;
  transport: 'stdio' | 'sse' | 'http';
  /** Prerequisite steps shown during setup — user must complete these before the connector is added */
  setupSteps?: CatalogSetupStep[];
  config: {
    command?: string;
    args?: string[];
    env?: Record<string, CatalogEnvField>;
    argTemplates?: Record<string, CatalogArgTemplate>;
    /** For SSE connectors: header templates keyed by header name (e.g. "Authorization") */
    headerTemplates?: Record<string, CatalogHeaderTemplate>;
    url?: string;
    auth?: string;
    note?: string;
  };
}

export const CONNECTOR_CATALOG: CatalogConnector[] = [
  {
    id: 'github',
    name: 'GitHub',
    description: 'Access repositories, pull requests, issues, and code. Read and write to repos, manage PRs, search code.',
    category: 'tech',
    stability: 'stable',
    maintainer: 'Anthropic / Model Context Protocol',
    source: 'https://github.com/modelcontextprotocol/servers/tree/main/src/github',
    docsUrl: 'https://github.com/modelcontextprotocol/servers/tree/main/src/github#readme',
    logoUrl: 'https://cdn.simpleicons.org/github',
    transport: 'stdio',
    config: {
      command: 'npx',
      args: ['-y', '@modelcontextprotocol/server-github'],
      env: {
        GITHUB_PERSONAL_ACCESS_TOKEN: {
          label: 'Personal Access Token',
          description: 'GitHub PAT with repo, issues, and pull_requests scopes',
          secret: true,
          required: true,
          docsUrl: 'https://github.com/settings/tokens',
        },
      },
    },
  },
  {
    id: 'jira',
    name: 'Jira & Confluence',
    description: 'Search and manage Jira issues, sprints, projects, and Confluence pages via Atlassian Rovo MCP.',
    category: 'admin',
    stability: 'stable',
    maintainer: 'Atlassian',
    source: 'https://mcp.atlassian.com',
    docsUrl: 'https://support.atlassian.com/atlassian-rovo-mcp-server/docs/getting-started-with-the-atlassian-remote-mcp-server/',
    logoUrl: 'https://cdn.simpleicons.org/jira',
    transport: 'http',
    config: {
      url: 'https://mcp.atlassian.com/v1/mcp',
      note: 'Atlassian-hosted MCP server. OAuth sign-in happens when you click Connect.',
    },
  },
  {
    id: 'grafana',
    name: 'Grafana',
    description: 'Query dashboards, alerts, Loki logs, and Prometheus metrics from your Grafana instance.',
    category: 'tech',
    stability: 'stable',
    maintainer: 'GodMode',
    source: 'https://github.com/MortenKre/Godmode-Google-MCP',
    docsUrl: 'https://grafana.com/docs/grafana/latest/administration/service-accounts/',
    logoUrl: 'https://cdn.simpleicons.org/grafana',
    transport: 'sse',
    config: {
      url: 'https://mcp.ingodmode.xyz/grafana',
      headerTemplates: {
        'X-Grafana-Url': {
          label: 'Grafana URL',
          description: 'Full URL of your Grafana instance, e.g. https://myworkspace.grafana.net',
          secret: false,
          required: true,
          valueTemplate: '{value}',
        },
        'X-Grafana-Token': {
          label: 'Service Account Token',
          description: 'Grafana service account token (glsa_...). Create under Administration > Service Accounts.',
          secret: true,
          required: true,
          valueTemplate: '{value}',
          docsUrl: 'https://grafana.com/docs/grafana/latest/administration/service-accounts/',
        },
      },
      note: 'Hosted MCP server. No local install needed. Enter your Grafana URL and service account token.',
    },
  },
  {
    id: 'azure',
    name: 'Azure',
    description: 'Manage Azure resources, AKS, PostgreSQL, Storage, Key Vault, Service Bus, DNS, users, and billing.',
    category: 'tech',
    stability: 'stable',
    maintainer: 'GodMode',
    source: 'https://github.com/MortenKre/Godmode-Google-MCP',
    docsUrl: 'https://github.com/MortenKre/Godmode-Google-MCP#readme',
    logoUrl: 'https://cdn.simpleicons.org/microsoftazure/0078D4',
    transport: 'sse',
    config: {
      url: 'https://mcp.ingodmode.xyz/azure',
      auth: 'oauth',
      note: 'Hosted MCP server. Sign in with your Microsoft account to access Azure resources.',
    },
  },
  {
    id: 'vanta',
    name: 'Vanta',
    description: 'Access compliance frameworks, security tests, and remediation status. Supports SOC 2, ISO 27001, HIPAA, GDPR.',
    category: 'admin',
    stability: 'beta',
    maintainer: 'Vanta',
    source: 'https://github.com/VantaInc/vanta-mcp-server',
    docsUrl: 'https://github.com/VantaInc/vanta-mcp-server#readme',
    logoUrl: 'https://cdn.simpleicons.org/vanta',
    transport: 'stdio',
    config: {
      command: 'npx',
      args: ['-y', '@vantasdk/vanta-mcp-server'],
      env: {
        VANTA_CLIENT_ID: {
          label: 'Client ID',
          description: 'Vanta API OAuth client ID',
          secret: false,
          required: true,
          docsUrl: 'https://app.vanta.com/c/mega/settings/developer-console',
        },
        VANTA_CLIENT_SECRET: {
          label: 'Client Secret',
          description: 'Vanta API OAuth client secret',
          secret: true,
          required: true,
          docsUrl: 'https://app.vanta.com/c/mega/settings/developer-console',
        },
      },
      note: 'Credentials are from the Vanta developer console.',
    },
  },
  {
    id: 'google-workspace',
    name: 'Google Workspace',
    description: 'Gmail, Google Calendar, Google Drive, and Google Meet. Read, send, search emails, manage events, browse files, and access meeting transcripts.',
    category: 'admin',
    stability: 'stable',
    maintainer: 'GodMode',
    source: 'https://github.com/MortenKre/Godmode-Google-MCP',
    docsUrl: 'https://github.com/MortenKre/Godmode-Google-MCP#readme',
    logoUrl: 'https://cdn.simpleicons.org/google',
    transport: 'http',
    config: {
      url: 'https://mcp.ingodmode.xyz/mcp',
      note: 'Hosted MCP server. Google OAuth sign-in happens automatically when Claude Code connects.',
    },
  },
  {
    id: 'local-files',
    name: 'Local Files',
    description: 'Give Claude Code access to a specific directory on the server. Scoped to the path you specify.',
    category: 'tech',
    stability: 'stable',
    maintainer: 'Anthropic / Model Context Protocol',
    source: 'https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem',
    docsUrl: 'https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem#readme',
    logoUrl: 'https://cdn.simpleicons.org/files',
    transport: 'stdio',
    config: {
      command: 'npx',
      args: ['-y', '@modelcontextprotocol/server-filesystem', '{path}'],
      argTemplates: {
        path: {
          label: 'Directory Path',
          description: 'Absolute path to the directory Claude should have access to, e.g. /data/workspace',
          secret: false,
          required: true,
        },
      },
      env: {},
    },
  },
  {
    id: 'fireflies',
    name: 'Fireflies',
    description: 'Search and retrieve meeting transcripts, generate summaries, and query recordings from Fireflies.ai.',
    category: 'admin',
    stability: 'stable',
    maintainer: 'Community',
    source: 'https://github.com/cassler/fireflies-mcp-server',
    docsUrl: 'https://www.npmjs.com/package/fireflies-mcp-server',
    logoUrl: 'https://fireflies.ai/favicon.ico',
    transport: 'stdio',
    config: {
      command: 'npx',
      args: ['-y', 'fireflies-mcp-server'],
      env: {
        FIREFLIES_API_KEY: {
          label: 'API Key',
          description: 'Fireflies.ai API key from Developer Settings.',
          secret: true,
          required: true,
          docsUrl: 'https://app.fireflies.ai/settings/developer-settings',
        },
      },
    },
  },
  {
    id: 'slack',
    name: 'Slack',
    description: 'Read messages, post to channels, list users, and search across your Slack workspace.',
    manifest: `_metadata:
  major_version: 1
  minor_version: 1
display_information:
  name: GodMode
  description: GodMode MCP bridge for Slack
  background_color: "#4A154B"
features:
  bot_user:
    display_name: GodMode
    always_online: true
oauth_config:
  scopes:
    bot:
      - channels:read
      - channels:history
      - chat:write
      - groups:read
      - groups:history
      - im:read
      - im:history
      - mpim:read
      - mpim:history
      - reactions:read
      - reactions:write
      - users:read
      - users.profile:read
      - team:read
settings:
  org_deploy_enabled: false
  socket_mode_enabled: false
  token_rotation_enabled: false`,
    category: 'admin',
    stability: 'stable',
    maintainer: 'Anthropic / Model Context Protocol',
    source: 'https://github.com/modelcontextprotocol/servers/tree/main/src/slack',
    docsUrl: 'https://github.com/modelcontextprotocol/servers/tree/main/src/slack#readme',
    logoUrl: 'https://cdn.simpleicons.org/slack/4A154B',
    transport: 'stdio',
    config: {
      command: 'npx',
      args: ['-y', '@modelcontextprotocol/server-slack'],
      env: {
        SLACK_BOT_TOKEN: {
          label: 'Bot OAuth Token (xoxb-...)',
          description: 'Go to api.slack.com/apps → your app → OAuth & Permissions → Install to Workspace → copy "Bot User OAuth Token" (starts with xoxb-). Required scopes: channels:read, channels:history, users:read, chat:write.',
          secret: true,
          required: true,
          docsUrl: 'https://api.slack.com/apps',
        },
        SLACK_TEAM_ID: {
          label: 'Workspace ID (T...)',
          description: 'Open Slack in browser → the URL contains your workspace ID (e.g. app.slack.com/client/T01234ABCD). Or: Slack app → Settings → About → Workspace ID.',
          secret: false,
          required: true,
        },
      },
      note: 'Setup: 1) Create a Slack app at api.slack.com/apps. 2) Add Bot Token Scopes under OAuth & Permissions (channels:read, channels:history, users:read, chat:write). 3) Install to your workspace. 4) Copy the Bot Token and Workspace ID.',
    },
  },
];

/** Look up a catalog entry by matching server name or command/args pattern */
export function findCatalogEntry(serverName: string): CatalogConnector | undefined {
  // Direct ID match
  const direct = CONNECTOR_CATALOG.find(c => c.id === serverName);
  if (direct) return direct;
  // Fuzzy: name contains the server name (case-insensitive)
  return CONNECTOR_CATALOG.find(c => c.name.toLowerCase() === serverName.toLowerCase());
}

/** Category labels for display */
export const CATEGORY_LABELS: Record<string, string> = {
  tech: 'Tech',
  admin: 'Admin',
};

/** Maps connector IDs to OAuth provider names (mirrors OAuthProviderMapping on server) */
export const CONNECTOR_TO_OAUTH_PROVIDER: Record<string, string> = {
  azure: 'microsoft',
};
