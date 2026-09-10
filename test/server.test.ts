import { describe, expect, it } from "vitest";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createServer, SERVER_NAME } from "../src/server.js";

async function connectedClient(): Promise<Client> {
  const server = createServer();
  const client = new Client({ name: "test-client", version: "0.0.0" });
  const [clientTransport, serverTransport] =
    InMemoryTransport.createLinkedPair();

  await Promise.all([
    server.connect(serverTransport),
    client.connect(clientTransport),
  ]);

  return client;
}

describe("mcp-services server", () => {
  it("advertises its registered tools", async () => {
    const client = await connectedClient();
    const { tools } = await client.listTools();
    const names = tools.map((t) => t.name).sort();
    expect(names).toEqual(["add", "echo", "now"]);
    await client.close();
  });

  it("adds two numbers", async () => {
    const client = await connectedClient();
    const result = await client.callTool({
      name: "add",
      arguments: { a: 2, b: 3 },
    });
    expect(result.content).toEqual([{ type: "text", text: "5" }]);
    await client.close();
  });

  it("echoes a message", async () => {
    const client = await connectedClient();
    const result = await client.callTool({
      name: "echo",
      arguments: { message: "hello mcp" },
    });
    expect(result.content).toEqual([{ type: "text", text: "hello mcp" }]);
    await client.close();
  });

  it("returns an ISO timestamp for now", async () => {
    const client = await connectedClient();
    const result = await client.callTool({ name: "now", arguments: {} });
    const content = result.content as Array<{ type: string; text: string }>;
    expect(content[0].type).toBe("text");
    expect(() => new Date(content[0].text).toISOString()).not.toThrow();
    expect(new Date(content[0].text).toISOString()).toBe(content[0].text);
    await client.close();
  });

  it("exposes a server-info resource", async () => {
    const client = await connectedClient();
    const result = await client.readResource({ uri: "info://server" });
    const parsed = JSON.parse((result.contents[0] as { text: string }).text);
    expect(parsed.name).toBe(SERVER_NAME);
    await client.close();
  });
});
