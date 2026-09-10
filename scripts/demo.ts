/**
 * End-to-end demo: launch the built MCP server as a real child process over
 * stdio, then drive it with an MCP client exactly like a host (e.g. an IDE)
 * would. Run with `npm run demo` (after `npm run build`).
 */
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const here = dirname(fileURLToPath(import.meta.url));
const serverEntry = resolve(here, "..", "dist", "index.js");

async function main(): Promise<void> {
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [serverEntry],
  });
  const client = new Client({ name: "demo-client", version: "0.0.0" });
  await client.connect(transport);

  console.log("Connected to MCP server over stdio.\n");

  const { tools } = await client.listTools();
  console.log("Available tools:");
  for (const tool of tools) {
    console.log(`  - ${tool.name}: ${tool.description ?? ""}`);
  }
  console.log();

  const add = await client.callTool({
    name: "add",
    arguments: { a: 21, b: 21 },
  });
  console.log("add(21, 21) ->", text(add.content));

  const echo = await client.callTool({
    name: "echo",
    arguments: { message: "hello from the demo client" },
  });
  console.log("echo(...)   ->", text(echo.content));

  const now = await client.callTool({ name: "now", arguments: {} });
  console.log("now()       ->", text(now.content));

  const info = await client.readResource({ uri: "info://server" });
  const infoContent = info.contents[0] as { text?: string } | undefined;
  console.log("resource info://server ->", infoContent?.text);

  await client.close();
  console.log("\nDemo completed successfully.");
}

function text(content: unknown): string {
  const items = content as Array<{ type: string; text?: string }>;
  return items
    .filter((c) => c.type === "text")
    .map((c) => c.text)
    .join(" ");
}

main().catch((error) => {
  console.error("Demo failed:", error);
  process.exit(1);
});
