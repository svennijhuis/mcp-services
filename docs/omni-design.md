# Omni design diagrams

Source: infrastructure-azure-white-label PR #9 at 16461bb, docs/extra/mcp-omni, plus the local test diagrams from the MCP Services room.

This file is the full diagram set. Do not delete a diagram. Do not turn a diagram into prose.

Code in this PR is the local slice only (diagrams 14 and 15). Diagrams 1 to 13 are the later APIM gateway. Do not implement APIM, JWT, or Azure in this PR.

## 1. Overview (later gateway)

```mermaid
flowchart LR
  U["User / agent"] --> E["Entra JWT"]
  E --> A["APIM\nwho may call what"]
  A --> O["Omni stamp\nhow to find + invoke"]
  O --> T["Tool processes"]
```

## 2. Full path, auth in APIM (later gateway)

```mermaid
flowchart TB
  subgraph clients["Clients"]
    Cursor["Cursor / Claude"]
    Agents["Remote agents / CI"]
  end

  Entra["Microsoft Entra ID\nissues JWT"]
  APIM["Azure API Management\nvalidate-jwt · roles / products\nrate limit · log azp\nper-caller allow: which MCP / tools"]
  Omni["mcp-omni --http\nno auth in process\nprivate App Service / ACR"]

  subgraph omniTools["Omni tools"]
    Discover["discover_servers\ndiscover_tools"]
    Schema["get_tool_schema"]
    Invoke["invoke_tool\n+ projection / maxChars"]
  end

  Reg["Registry JSON git\nenabled: false by default"]

  subgraph backends["mcp-services backends\nno auth in process"]
    FS["mcp-filesystem"]
    DB["mcp-database"]
    Roslyn["mcp-roslyn"]
    Index["mcp-index"]
    Learn["mcp-learnings"]
  end

  Cursor -->|"get token"| Entra
  Agents -->|"get token"| Entra
  Cursor -->|"Bearer JWT"| APIM
  Agents -->|"Bearer JWT"| APIM
  APIM -->|"allowed callers only\n/mcp-omni/v1/mcp"| Omni
  Omni --> Reg
  Reg --> Discover
  Discover --> Schema
  Schema --> Invoke
  Invoke --> FS
  Invoke --> DB
  Invoke --> Roslyn
  Invoke --> Index
  Invoke --> Learn
```

## 3. Sequence, client never talks to Omni directly (later gateway)

```mermaid
sequenceDiagram
  participant C as Client
  participant Entra as Entra
  participant APIM as APIM
  participant Omni as Omni
  participant Tool as One backend

  C->>Entra: MSAL or DefaultAzureCredential
  Entra-->>C: JWT
  C->>APIM: POST /mcp-omni/v1/mcp
  APIM->>APIM: JWT then role
  alt denied
    APIM-->>C: 401 or 403
  else allowed
    APIM->>Omni: private POST /mcp
    Omni->>Tool: private POST /mcp
    Tool-->>Omni: tool result
    Omni-->>APIM: projected text
    APIM-->>C: stream
  end
```

## 4. Trust boundaries (later gateway)

```mermaid
flowchart TB
  subgraph untrusted["Untrusted: internet and corp clients"]
    Client["IDE, agent, CI"]
  end

  subgraph entra["Entra (issuer we do not host)"]
    Sts["login.microsoftonline.com"]
  end

  subgraph edge["Trust boundary: APIM"]
    Gw["JWT, roles, product, rate limit, azp"]
  end

  subgraph stamp["Trusted only via private network"]
    Omni["Omni, no JWT check"]
    Five["Five backends, no JWT check"]
    Data["Private Postgres and workspace volume"]
  end

  Client -->|"1. get token"| Sts
  Client -->|"2. Bearer to public 443"| Gw
  Gw -->|"3. forward or 401/403"| Omni
  Omni --> Five
  Five --> Data
```

## 5. JWT acquisition (later gateway)

```mermaid
flowchart LR
  People["People: MSAL"] --> Entra["Entra"]
  Machines["Machines: MI or az login"] --> Entra
  Entra --> APIM["APIM validate-jwt + roles"]
  APIM --> Omni["Omni, no auth"]
```

## 6. One stamp (later gateway)

```mermaid
flowchart TB
  APIM["APIM\nExpose existing MCP"] -->|"only public entry"| Omni

  subgraph stamp["One Azure stamp (ACA / App Service)"]
    Omni["Omni : /mcp"]
    FS["filesystem"]
    DB["database"]
    R["roslyn"]
    I["index"]
    L["learnings"]
    Omni --- FS
    Omni --- DB
    Omni --- R
    Omni --- I
    Omni --- L
  end
```

## 7. Alice allowed, Bob denied (later gateway)

```mermaid
flowchart TB
  subgraph deny["Blocked: Bob → database"]
    Bob["Bob"] -->|"JWT, no DB role"| APIM1["APIM"]
    APIM1 -->|"403 / no tool access"| X["Stops here"]
  end

  subgraph allow["Allowed: Alice → filesystem"]
    Alice["Alice"] -->|"JWT + FS role"| APIM2["APIM"]
    APIM2 --> Omni["Omni /mcp"]
    Omni -->|"invoke_tool"| FS["mcp-filesystem"]
  end
```

## 8. Happy path and deny (later gateway)

```mermaid
sequenceDiagram
  participant C as Client
  participant Entra as Entra
  participant APIM as APIM MCP
  participant Omni as Omni
  participant DB as mcp-database

  C->>Entra: Get token (roles)
  C->>APIM: Streamable HTTP /mcp + Bearer
  APIM->>APIM: validate-azure-ad-token<br/>product / role check
  alt Missing database role
    APIM-->>C: Deny
  else Allowed
    APIM->>Omni: Forward MCP (private)
    C->>Omni: discover_tools
    Omni-->>C: Short list (no full schemas)
    C->>Omni: get_tool_schema(database, query)
    Omni-->>C: One schema
    C->>Omni: invoke_tool(...)
    Omni->>DB: Local call in stamp
    DB-->>Omni: Result
    Omni-->>C: Projected / capped result
  end
```

## 9. Deny only, database role missing (later gateway)

```mermaid
sequenceDiagram
  participant Bob as Bob
  participant Entra as Entra
  participant APIM as APIM
  participant Omni as Omni

  Bob->>Entra: Token request
  Entra-->>Bob: JWT without MCP.Database
  Bob->>APIM: tools/call invoke_tool server=database
  APIM->>APIM: validate-jwt ok, role missing
  APIM-->>Bob: 403
  Note over Omni: No request
```

## 10. Phases (later gateway)

```mermaid
flowchart LR
  A["A: One stamp + APIM MCP + Entra"] --> B["B: Harden roles + monitor"]
  B -.-> M["Later: marketplace"]
```

## 11. JWT paths, people and machines (later gateway)

```mermaid
flowchart TB
  subgraph people["People (browser / IDE)"]
    MSAL["MSAL / sign-in UI"]
    MSAL -->|"acquireTokenSilent\nscope: api://…/access_as_user"| Entra1["login.microsoftonline.com"]
  end

  subgraph machines["Machines (agents, services, CI)"]
    MI["Managed identity\nor az login on laptop"]
    MI -->|"DefaultAzureCredential\nscope: api://…/.default"| Entra2["login.microsoftonline.com"]
  end

  Entra1 --> JWT["Access token JWT"]
  Entra2 --> JWT
  JWT -->|"Authorization: Bearer …"| APIM["APIM MCP\nvalidate-jwt + roles"]
```

## 12. Machine agent sequence (later gateway)

```mermaid
sequenceDiagram
  participant Agent as Agent / service
  participant Entra as Entra
  participant APIM as APIM MCP
  participant Omni as Omni stamp

  Agent->>Entra: Token request (.default)\nMI or az-login credential
  Entra-->>Agent: JWT (roles e.g. MCP.Filesystem)
  Agent->>APIM: MCP /mcp + Bearer JWT
  APIM->>APIM: validate-jwt + role check
  alt No database role
    APIM-->>Agent: 403
  else Allowed
    APIM->>Omni: Forward
    Omni-->>Agent: MCP result
  end
```

## 13. Ownership, APIM versus mcp-services (later gateway)

```mermaid
flowchart TB
  subgraph apim["APIM (this infra repo, later)"]
    RegApi["One MCP registration\nExpose existing MCP\nmcp-omni/v1/mcp"]
    Pol["validate-jwt + family roles\nrate limit + azp log"]
  end

  subgraph repo["mcp-services monorepo (later, other PR)"]
    Omni["NEW mcp-omni\nalways-on 4 tools\nregistry.json"]
    FS["mcp-filesystem\nunchanged tools"]
    DB["mcp-database\nunchanged tools"]
    R["mcp-roslyn\nunchanged tools"]
    I["mcp-index\nunchanged tools"]
    L["mcp-learnings\nunchanged tools"]
    Host["McpServices.Hosting\nno auth added"]
  end

  RegApi --> Pol
  Pol -->|"only allowed calls\nprivate network"| Omni
  Omni --> FS
  Omni --> DB
  Omni --> R
  Omni --> I
  Omni --> L
  FS --- Host
  DB --- Host
  R --- Host
  I --- Host
  L --- Host
  Omni --- Host
```

## 14. Local test now, no APIM

```mermaid
flowchart LR
  Client[IDE / Claude / Cursor] -->|stdio| Omni[mcp-omni]
  Omni -->|HTTP /mcp| FS[filesystem :5100]
  Omni -->|HTTP /mcp| Ros[roslyn :5101]
  Omni -->|HTTP /mcp| DB[database :5102]
  Omni -->|HTTP /mcp| Ix[index :5103]
  Omni -->|HTTP /mcp| Ln[learnings :5104]
```

## 15. Local discover, schema, invoke

```mermaid
sequenceDiagram
  participant C as Client
  participant O as Omni
  participant B as One backend
  C->>O: discover_servers
  O-->>C: id, title, summary only
  C->>O: discover_tools(serverId)
  O->>B: tools/list
  O-->>C: names and one line, no schema
  C->>O: get_tool_schema(serverId, tool)
  O->>B: tools/list
  O-->>C: one inputSchema
  C->>O: invoke_tool(serverId, tool, args)
  O->>B: tools/call
  O-->>C: projected text, max 8000 chars
```
