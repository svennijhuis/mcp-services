import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";

export const SERVER_NAME = "mcp-services";
export const SERVER_VERSION = "0.1.0";

/**
 * Build a fully-configured MCP server instance.
 *
 * The server is transport-agnostic: connect it to a stdio transport for a
 * real CLI process, or to an in-memory transport for tests.
 */
export function createServer(): McpServer {
  const server = new McpServer({
    name: SERVER_NAME,
    version: SERVER_VERSION,
  });

  server.registerTool(
    "add",
    {
      title: "Add",
      description: "Add two numbers and return their sum.",
      inputSchema: {
        a: z.number().describe("First addend"),
        b: z.number().describe("Second addend"),
      },
    },
    async ({ a, b }) => ({
      content: [{ type: "text", text: String(a + b) }],
    }),
  );

  server.registerTool(
    "echo",
    {
      title: "Echo",
      description: "Return the provided message unchanged.",
      inputSchema: {
        message: z.string().describe("Message to echo back"),
      },
    },
    async ({ message }) => ({
      content: [{ type: "text", text: message }],
    }),
  );

  server.registerTool(
    "now",
    {
      title: "Current time",
      description: "Return the current server time as an ISO-8601 timestamp.",
      inputSchema: {},
    },
    async () => ({
      content: [{ type: "text", text: new Date().toISOString() }],
    }),
  );

  server.registerResource(
    "server-info",
    "info://server",
    {
      title: "Server info",
      description: "Basic metadata about this MCP server.",
      mimeType: "application/json",
    },
    async (uri) => ({
      contents: [
        {
          uri: uri.href,
          mimeType: "application/json",
          text: JSON.stringify(
            { name: SERVER_NAME, version: SERVER_VERSION },
            null,
            2,
          ),
        },
      ],
    }),
  );

  return server;
}
