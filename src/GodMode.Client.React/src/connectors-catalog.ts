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

export interface CatalogConnector {
  id: string;
  name: string;
  description: string;
  category: string;
  stability: 'stable' | 'beta';
  maintainer: string;
  source: string;
  docsUrl: string;
  logoUrl: string;
  transport: 'stdio' | 'sse';
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
    name: 'Jira',
    description: 'Search and manage Jira issues, sprints, and projects. Also includes Confluence page access.',
    category: 'admin',
    stability: 'stable',
    maintainer: 'Atlassian',
    source: 'https://mcp.atlassian.com',
    docsUrl: 'https://www.atlassian.com/platform/mcp',
    logoUrl: 'https://cdn.simpleicons.org/jira',
    transport: 'sse',
    config: {
      url: 'https://mcp.atlassian.com/v1/sse',
      auth: 'oauth',
      headerTemplates: {
        Authorization: {
          label: 'Atlassian API Token',
          description: 'Create an API token at id.atlassian.com/manage-profile/security/api-tokens',
          secret: true,
          required: true,
          valueTemplate: 'Bearer {value}',
          docsUrl: 'https://id.atlassian.com/manage-profile/security/api-tokens',
        },
      },
      note: 'Uses Atlassian API token for authentication.',
    },
  },
  {
    id: 'grafana',
    name: 'Grafana',
    description: 'Query dashboards, alerts, Loki logs, and Prometheus metrics from your Azure-hosted Grafana instance.',
    category: 'tech',
    stability: 'stable',
    maintainer: 'Community',
    source: 'https://github.com/0xteamhq/mcp-grafana',
    docsUrl: 'https://www.npmjs.com/package/@leval/mcp-grafana',
    logoUrl: 'https://cdn.simpleicons.org/grafana',
    transport: 'stdio',
    config: {
      command: 'npx',
      args: ['-y', '@leval/mcp-grafana'],
      env: {
        GRAFANA_URL: {
          label: 'Grafana URL',
          description: 'Full URL of your Grafana instance, e.g. https://myworkspace.grafana.azure.com',
          secret: false,
          required: true,
        },
        GRAFANA_SERVICE_ACCOUNT_TOKEN: {
          label: 'Service Account Token',
          description: 'Grafana service account token (glsa_...). Create under Administration > Service Accounts.',
          secret: true,
          required: true,
          docsUrl: 'https://grafana.com/docs/grafana/latest/administration/service-accounts/',
        },
      },
    },
  },
  {
    id: 'azure',
    name: 'Azure',
    description: 'Interact with Azure resources: storage, databases, resource groups, subscriptions, and more via Azure CLI.',
    category: 'tech',
    stability: 'stable',
    maintainer: 'Microsoft',
    source: 'https://github.com/Azure/azure-mcp',
    docsUrl: 'https://github.com/Azure/azure-mcp#readme',
    logoUrl: 'https://cdn.simpleicons.org/microsoftazure',
    transport: 'stdio',
    config: {
      command: 'npx',
      args: ['-y', '@azure/mcp@latest'],
      env: {
        AZURE_SUBSCRIPTION_ID: {
          label: 'Subscription ID',
          description: 'Your Azure subscription ID. Scope defaults to this subscription.',
          secret: false,
          required: false,
        },
      },
      note: 'Requires Azure CLI installed and authenticated on the host (az login).',
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
    logoUrl: 'https://www.vanta.com/favicon.ico',
    transport: 'stdio',
    config: {
      command: 'npx',
      args: ['-y', '@vantasdk/vanta-mcp-server'],
      env: {
        VANTA_CLIENT_ID: {
          label: 'OAuth Client ID',
          description: 'Vanta OAuth client ID from your Vanta API settings',
          secret: false,
          required: true,
          docsUrl: 'https://app.vanta.com/oauth/clients',
        },
        VANTA_CLIENT_SECRET: {
          label: 'OAuth Client Secret',
          description: 'Vanta OAuth client secret',
          secret: true,
          required: true,
          docsUrl: 'https://app.vanta.com/oauth/clients',
        },
      },
    },
  },
  {
    id: 'gmail',
    name: 'Gmail',
    description: 'Read, search, and draft emails in Gmail. Supports label management and thread access.',
    category: 'admin',
    stability: 'stable',
    maintainer: 'Anthropic',
    source: 'https://claude.ai/connectors',
    docsUrl: 'https://support.claude.ai/hc/en-us/articles/connectors',
    logoUrl: 'https://cdn.simpleicons.org/gmail',
    transport: 'sse',
    config: {
      url: 'https://gmail.mcp.claude.ai/mcp',
      auth: 'oauth',
      headerTemplates: {
        Authorization: {
          label: 'Google OAuth Token',
          description: 'OAuth access token for Gmail. Generate via Google Cloud Console OAuth playground.',
          secret: true,
          required: true,
          valueTemplate: 'Bearer {value}',
          docsUrl: 'https://developers.google.com/oauthplayground/',
        },
      },
      note: 'Anthropic-hosted connector. Requires a Google OAuth access token.',
    },
  },
  {
    id: 'google-calendar',
    name: 'Google Calendar',
    description: 'Read and create calendar events, check availability, and manage schedules across Google Calendar.',
    category: 'admin',
    stability: 'stable',
    maintainer: 'Anthropic',
    source: 'https://claude.ai/connectors',
    docsUrl: 'https://support.claude.ai/hc/en-us/articles/connectors',
    logoUrl: 'https://cdn.simpleicons.org/googlecalendar',
    transport: 'sse',
    config: {
      url: 'https://gcal.mcp.claude.ai/mcp',
      auth: 'oauth',
      headerTemplates: {
        Authorization: {
          label: 'Google OAuth Token',
          description: 'OAuth access token for Google Calendar. Generate via Google Cloud Console OAuth playground.',
          secret: true,
          required: true,
          valueTemplate: 'Bearer {value}',
          docsUrl: 'https://developers.google.com/oauthplayground/',
        },
      },
      note: 'Anthropic-hosted connector. Requires a Google OAuth access token.',
    },
  },
  {
    id: 'google-drive',
    name: 'Google Drive',
    description: 'Browse, search, and read files in Google Drive including Docs, Sheets, and PDFs.',
    category: 'admin',
    stability: 'stable',
    maintainer: 'Anthropic',
    source: 'https://claude.ai/connectors',
    docsUrl: 'https://support.claude.ai/hc/en-us/articles/connectors',
    logoUrl: 'https://cdn.simpleicons.org/googledrive',
    transport: 'sse',
    config: {
      url: 'https://drive.mcp.claude.ai/mcp',
      auth: 'oauth',
      headerTemplates: {
        Authorization: {
          label: 'Google OAuth Token',
          description: 'OAuth access token for Google Drive. Generate via Google Cloud Console OAuth playground.',
          secret: true,
          required: true,
          valueTemplate: 'Bearer {value}',
          docsUrl: 'https://developers.google.com/oauthplayground/',
        },
      },
      note: 'Anthropic-hosted connector. Requires a Google OAuth access token.',
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
    id: 'powerpoint',
    name: 'PowerPoint',
    description: 'Create and edit PowerPoint presentations. Manage slides, text, and layouts using natural language.',
    category: 'admin',
    stability: 'beta',
    maintainer: 'Anthropic',
    source: 'https://claude.ai/connectors',
    docsUrl: 'https://support.claude.ai/hc/en-us/articles/connectors',
    logoUrl: 'https://cdn.simpleicons.org/microsoftpowerpoint',
    transport: 'sse',
    config: {
      url: 'https://powerpoint.mcp.claude.ai/mcp',
      auth: 'oauth',
      headerTemplates: {
        Authorization: {
          label: 'Microsoft OAuth Token',
          description: 'OAuth access token for Microsoft services.',
          secret: true,
          required: true,
          valueTemplate: 'Bearer {value}',
        },
      },
      note: 'Anthropic-hosted beta connector. Requires a Microsoft OAuth access token.',
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
          description: 'Fireflies.ai API key from your dashboard settings',
          secret: true,
          required: true,
          docsUrl: 'https://fireflies.ai/dashboard/settings/api',
        },
      },
    },
  },
  {
    id: 'slack',
    name: 'Slack',
    description: 'Read messages, post to channels, list users, and search across your Slack workspace.',
    category: 'admin',
    stability: 'stable',
    maintainer: 'Anthropic / Model Context Protocol',
    source: 'https://github.com/modelcontextprotocol/servers/tree/main/src/slack',
    docsUrl: 'https://github.com/modelcontextprotocol/servers/tree/main/src/slack#readme',
    logoUrl: 'https://cdn.simpleicons.org/slack',
    transport: 'stdio',
    config: {
      command: 'npx',
      args: ['-y', '@modelcontextprotocol/server-slack'],
      env: {
        SLACK_BOT_TOKEN: {
          label: 'Bot Token',
          description: 'Slack bot OAuth token (xoxb-...). Create a Slack app and install it to your workspace.',
          secret: true,
          required: true,
          docsUrl: 'https://api.slack.com/apps',
        },
        SLACK_TEAM_ID: {
          label: 'Team / Workspace ID',
          description: 'Your Slack workspace ID (e.g. T01234ABCD). Found in your workspace URL or admin settings.',
          secret: false,
          required: true,
        },
      },
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
