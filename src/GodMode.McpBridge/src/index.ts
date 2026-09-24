#!/usr/bin/env node

/**
 * GodMode MCP Bridge — stdio MCP server that lets Claude interact with GodMode.
 *
 * Environment variables (injected by GodMode.Server):
 *   GODMODE_PROJECT_ID    — the project this Claude instance belongs to
 *   GODMODE_PROJECT_TOKEN — per-project auth token for the internal API
 *   GODMODE_SERVER_URL    — base URL of the GodMode server (e.g. http://localhost:31337)
 */

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { request as httpRequest } from "node:http";
import { request as httpsRequest } from "node:https";
import { z } from "zod";

const PROJECT_ID = process.env.GODMODE_PROJECT_ID;
const PROJECT_TOKEN = process.env.GODMODE_PROJECT_TOKEN;
const SERVER_URL = process.env.GODMODE_SERVER_URL || "http://localhost:31337";

if (!PROJECT_ID || !PROJECT_TOKEN) {
  console.error(
    "GODMODE_PROJECT_ID and GODMODE_PROJECT_TOKEN must be set. " +
      "This MCP server is meant to be launched by GodMode.Server."
  );
  process.exit(1);
}

/** POST to the GodMode internal API with project token auth. */
async function apiCall(
  endpoint: string,
  body: Record<string, unknown>
): Promise<{ ok: boolean; data?: unknown; error?: string }> {
  try {
    const res = await fetch(`${SERVER_URL}/api/internal/${endpoint}`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${PROJECT_TOKEN}`,
        "X-GodMode-Project-Id": PROJECT_ID!,
      },
      body: JSON.stringify(body),
    });

    if (!res.ok) {
      const text = await res.text();
      return { ok: false, error: `HTTP ${res.status}: ${text}` };
    }

    const data = await res.json();
    return { ok: true, data };
  } catch (err) {
    return { ok: false, error: String(err) };
  }
}

// Create MCP server
const server = new McpServer({
  name: "godmode-bridge",
  version: "1.0.0",
});

// ── Tool: godmode_submit_result ──

server.tool(
  "godmode_submit_result",
  "Submit the structured result of your work. Use this when you've completed your task to pass results to the next pipeline stage or to record what you accomplished. The result should be a JSON string containing the key outputs of your work.",
  {
    result: z
      .string()
      .describe(
        "JSON string of structured result object. Include relevant outputs like URLs, file paths, summaries, data, etc."
      ),
    summary: z
      .string()
      .optional()
      .describe("Brief one-line summary of what was accomplished"),
  },
  async ({ result, summary }) => {
    let parsed: unknown;
    try {
      parsed = JSON.parse(result);
    } catch {
      return {
        content: [
          { type: "text" as const, text: "Failed to parse result: invalid JSON string" },
        ],
        isError: true,
      };
    }

    const res = await apiCall("result", { result: parsed, summary });
    if (!res.ok) {
      return {
        content: [
          { type: "text" as const, text: `Failed to submit result: ${res.error}` },
        ],
        isError: true,
      };
    }
    return {
      content: [
        {
          type: "text" as const,
          text: `Result submitted successfully.${summary ? ` Summary: ${summary}` : ""}`,
        },
      ],
    };
  }
);

// ── Tool: godmode_update_status ──

server.tool(
  "godmode_update_status",
  "Update your status message visible to the user in the GodMode UI. Use this to communicate progress on long-running tasks, e.g. 'Analyzing codebase...', 'Running tests...', 'Creating PR...'.",
  {
    message: z
      .string()
      .describe(
        "Status message to display (keep it short, 1-2 sentences)"
      ),
  },
  async ({ message }) => {
    const res = await apiCall("status", { message });
    if (!res.ok) {
      return {
        content: [
          { type: "text" as const, text: `Failed to update status: ${res.error}` },
        ],
        isError: true,
      };
    }
    return {
      content: [{ type: "text" as const, text: `Status updated: ${message}` }],
    };
  }
);

// ── Tool: godmode_request_human_review ──

server.tool(
  "godmode_request_human_review",
  "Pause and request human review. Use this when you need a decision, want approval before proceeding with a risky action, or want the user to verify your work before continuing. The project will enter a 'waiting for input' state until the user responds.",
  {
    question: z
      .string()
      .describe(
        "The question or decision you need the human to address"
      ),
    context: z
      .string()
      .optional()
      .describe(
        "Additional context to help the human make a decision (e.g. what you've done so far, options considered)"
      ),
  },
  async ({ question, context }) => {
    const res = await apiCall("review", { question, context });
    if (!res.ok) {
      return {
        content: [
          {
            type: "text" as const,
            text: `Failed to request review: ${res.error}`,
          },
        ],
        isError: true,
      };
    }
    return {
      content: [
        {
          type: "text" as const,
          text: `Human review requested. The project is now paused waiting for user input. Question: ${question}`,
        },
      ],
    };
  }
);

// ── Tool: permission_prompt ──

/** What claude expects back from a --permission-prompt-tool, as JSON text. */
type PermissionResult =
  | { behavior: "allow"; updatedInput: unknown }
  | { behavior: "deny"; message: string };

/**
 * POSTs and waits for the response however long it takes: the server answers when the user
 * does. fetch gives up on a response after 5 minutes (undici's headersTimeout); node:http has
 * no such limit. Rejects when the connection fails or drops, as it does on a server restart.
 */
function postAndWait(endpoint: string, body: unknown): Promise<unknown> {
  const url = new URL(`/api/internal/${endpoint}`, SERVER_URL);
  const payload = JSON.stringify(body);
  const send = url.protocol === "https:" ? httpsRequest : httpRequest;
  return new Promise((resolve, reject) => {
    const req = send(
      url,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Content-Length": Buffer.byteLength(payload),
          Authorization: `Bearer ${PROJECT_TOKEN}`,
          "X-GodMode-Project-Id": PROJECT_ID!,
        },
      },
      (res) => {
        let text = "";
        res.setEncoding("utf8");
        res.on("data", (chunk: string) => (text += chunk));
        res.on("error", reject);
        res.on("end", () => {
          if (res.statusCode !== 200) {
            reject(new Error(`HTTP ${res.statusCode}: ${text}`));
            return;
          }
          try {
            resolve(JSON.parse(text));
          } catch (err) {
            reject(err);
          }
        });
      }
    );
    req.on("error", reject);
    req.end(payload);
  });
}

/** A connection failure is an AggregateError with an empty message; its code says what happened. */
function describeError(err: unknown): string {
  if (!(err instanceof Error)) return String(err);
  const code = (err as NodeJS.ErrnoException).code;
  return err.message || code || err.name;
}

server.tool(
  "permission_prompt",
  "GodMode's permission prompt: asks the user whether a tool call may run, and answers AskUserQuestion. Called by Claude Code itself (--permission-prompt-tool), not meant to be called directly.",
  {
    tool_name: z.string().describe("The tool that wants to run"),
    input: z.record(z.string(), z.unknown()).describe("The tool's input"),
    tool_use_id: z.string().optional().describe("The id of the tool_use block"),
  },
  async ({ tool_name, input, tool_use_id }) => {
    let result: PermissionResult;
    try {
      result = (await postAndWait("permission", {
        toolName: tool_name,
        input,
        toolUseId: tool_use_id,
      })) as PermissionResult;
    } catch (err) {
      // Nobody can answer (the server restarted or is gone): claude sees a deny
      result = {
        behavior: "deny",
        message: `GodMode could not ask the user for permission (${describeError(err)}). Treat this as denied.`,
      };
    }
    return { content: [{ type: "text" as const, text: JSON.stringify(result) }] };
  }
);

// Start the server
async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((err) => {
  console.error("GodMode MCP Bridge failed to start:", err);
  process.exit(1);
});
