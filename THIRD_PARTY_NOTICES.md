# Third-party notices

All code in this repository was written for this repository and is licensed under the MIT License
(see `LICENSE`). Several open-source MCP servers were studied as reference for tool contracts,
naming and behaviour. Where a design was taken over, the origin is listed here so the attribution
required by the MIT License is preserved even though no source files were copied verbatim.

## Reference implementations (MIT)

| Project | What was used as reference | License |
|---|---|---|
| [modelcontextprotocol/servers](https://github.com/modelcontextprotocol/servers) — `src/filesystem` (TypeScript) | Tool set and semantics of `mcp-filesystem`: `read_text_file` with head/tail, `read_media_file`, `read_multiple_files`, `edit_file` with diff preview and whitespace-insensitive fallback, `directory_tree`, `list_directory_with_sizes`, allowed-directory sandboxing with symlink resolution. | MIT, Copyright (c) 2024 Anthropic, PBC |
| [MadQ/RoslynMcp](https://github.com/MadQ/RoslynMcp) (C#) | Breadth of the Roslyn tool surface for `mcp-roslyn`: workspace loading, symbol navigation, diagnostics, complexity metrics, call graphs, scripting and build tools. | MIT |
| [pinkroosterai/SharpMCP](https://github.com/pinkroosterai/SharpMCP) (C#) | `SymbolFinder`-based reference/implementation lookup, workspace caching and refresh strategy. | MIT |
| [darylmcd/Roslyn-Backed-MCP](https://github.com/darylmcd/Roslyn-Backed-MCP) (C#) | `workspaceId` sessions and the preview/apply (`dryRun`) pattern for refactorings returning unified diffs. | MIT |
| [haymon-ai/dbmcp](https://github.com/haymon-ai/dbmcp) (Rust) | Multi-database alias registry and read-only guard design for `mcp-database`. | MIT |
| [executeautomation/mcp-database-server](https://github.com/executeautomation/mcp-database-server) (TypeScript) | Tool contract for `read_query` / `write_query` / `describe_table` / `export_query`. | MIT |

MIT License text for the projects above:

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge, publish, distribute,
> sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions: The above copyright notice and this
> permission notice shall be included in all copies or substantial portions of the Software.
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.

## NuGet dependencies

| Package | License |
|---|---|
| ModelContextProtocol, ModelContextProtocol.AspNetCore | MIT (Model Context Protocol / Anthropic, Microsoft) |
| Microsoft.CodeAnalysis.* (Roslyn), Microsoft.Build.Locator, Microsoft.Build.Framework | MIT (.NET Foundation) |
| Microsoft.Data.Sqlite, Microsoft.Data.SqlClient, Microsoft.Extensions.* | MIT (.NET Foundation) |
| Npgsql | PostgreSQL License |
| Pgvector, Pgvector.Npgsql | MIT |
| DiffPlex | Apache License 2.0 |
| System.IO.Hashing | MIT (.NET Foundation) |
| xunit, xunit.runner.visualstudio | Apache License 2.0 |
| coverlet.collector | MIT |

Docker images: `mcr.microsoft.com/dotnet/*` (Microsoft container images EULA / MIT for the .NET
components) and `pgvector/pgvector` (PostgreSQL License; pgvector itself is PostgreSQL-licensed).
