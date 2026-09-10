# mcp-roslyn

Semantic C# tooling on top of the Roslyn compiler: load a solution once, then navigate, diagnose, refactor (with diff preview), analyse snippets, run C# scripts and drive `dotnet build`/`dotnet test`. Compared with text search this answers "who calls this", "what implements this" and "will this rename break anything" exactly.

Project: `src/servers/McpServices.Roslyn` · Tests: `tests/McpServices.Roslyn.Tests` (uses `tests/fixtures/SampleSolution`)

## Requirements

- A .NET SDK on the machine (the server locates it through `MSBuildLocator`; `list_workspaces` reports the instance it picked under `msbuild`).
- Projects must be restorable: the server runs a design-time build, so run `dotnet restore` once if `load_solution` reports missing assets.
- `mcp-roslyn` is published framework-dependent on purpose (the MSBuild BuildHost needs the shared runtime); set `DOTNET_ROOT` if `dotnet` is not on `PATH` of the MCP client.

## Running

```
mcp-roslyn [transport options] [--root <dir>]... [--no-scripting] [--no-build] [--restrict]
```

| Option | Meaning |
| --- | --- |
| `--root <dir>` (repeatable, also positional) | Directories searched by `list_workspaces` and allowed for loading. Default: current directory; env `MCP_ROSLYN_ROOT`. |
| `--restrict` | Only allow loading solutions/projects under the roots. |
| `--no-scripting` | Remove `run_script` (it executes C# inside the server process). Recommended for shared or remote setups. |
| `--no-build` | Remove `build_project` and `test_run`. |
| `--max-results <n>` | Cap for navigation results before paging (default 2000). |

## Tools

### Workspace

| Tool | Purpose |
| --- | --- |
| `list_workspaces` | `.sln`/`.slnx`/`.csproj` files under the roots plus loaded workspaces and MSBuild info. |
| `load_solution` / `load_project` | Load into an `MSBuildWorkspace` and return a `workspaceId` (re-loading the same path reuses it). Load diagnostics are reported, not swallowed. |
| `workspace_status` | Projects, document counts, load warnings, whether files changed since load. |
| `unload_workspace` | Free the workspace. |
| `list_projects`, `get_project_info`, `list_source_files`, `list_namespaces` | Structure of the loaded solution. |

### Navigation

| Tool | Purpose |
| --- | --- |
| `find_symbols` | Declared symbols by name (`matchMode` exact/prefix/contains), kind and project filters, paged. |
| `get_file_symbols` | Outline of one file. |
| `get_type_members` | Members of a type, optionally inherited (with `declaredIn`). |
| `get_symbol_info` | Everything about a symbol by name or `file`/`line`/`column`: signature, docs, type/method/property details, optional source. |
| `go_to_definition` | Source locations for the symbol at a position. |
| `find_references` | All references grouped by definition; `kind` distinguishes explicit and implicit references. |
| `find_implementations` | Implementations, derived types and overrides, with the relation. |
| `find_callers` | Callers of a method/property/event with call sites. |
| `get_call_graph` | Callers and/or callees up to depth 3 (`sourceOnly` to skip framework). |
| `get_type_hierarchy` | Base types, interfaces and derived types. |
| `get_symbols_in_scope` | What is visible at a position (locals, parameters, members, types). |

### Diagnostics and analysis

| Tool | Purpose |
| --- | --- |
| `get_diagnostics` | Compiler (and optionally analyzer) diagnostics for solution/project/file with severity filter, id filter and a `byId` summary. |
| `compile_check` | Fast "does it compile" per project with error counts. |
| `find_unused_symbols` | Private/internal symbols without references (public only with `includePublic`); skips overrides, attributed members, interface implementations and entry points. |
| `get_complexity_metrics` | Cyclomatic complexity, nesting depth, line counts per method and type. |
| `get_namespace_dependencies` | Namespace dependency graph folded to a depth, with cycle detection. |
| `get_nuget_dependencies` | Package references from csproj + `Directory.Packages.props`, resolved versions from `project.assets.json`. |

### Refactoring (preview by default)

Every refactoring returns unified diffs per file; nothing is written unless `apply: true`. Renames are refused when they would introduce new compile errors.

| Tool | Purpose |
| --- | --- |
| `rename_symbol` | Solution-wide rename (optionally strings/comments and overloads). |
| `format_document` | Roslyn formatter with the workspace's `.editorconfig`. |
| `organize_usings` | Remove unnecessary usings and sort (System first, then alphabetical, global/static/alias groups) for a file or whole project. |
| `list_code_fixes` | Code fix providers available for the diagnostics in a file. |
| `apply_code_fix` | Apply a fix for a diagnostic id (optionally `fixAll` across the document, `fixTitle` to pick a specific action). |

### Snippets, scripting and build

| Tool | Purpose |
| --- | --- |
| `analyze_snippet` | Compile a code fragment in an ad-hoc workspace: diagnostics, declared symbols, needed usings, optional syntax outline. |
| `get_syntax_tree` | Syntax tree (file or code) from a line, with depth limit and optional tokens. |
| `run_script` | Execute a C# script (`CSharpScript`) with timeout; returns return value, output and variables. Disabled by `--no-scripting`. |
| `build_project` | `dotnet build` with configuration/properties, parsed into structured errors and warnings. |
| `test_run` | `dotnet test` with filter, TRX parsed into per-test results. |

### Resources and prompts

| Kind | Uri / name | Content |
| --- | --- | --- |
| Resource | `roslyn://workspaces` | Loaded workspaces. |
| Resource | `roslyn://{workspaceId}/projects` | Projects of a workspace. |
| Resource | `roslyn://{workspaceId}/diagnostics` | Current diagnostics. |
| Prompt | `explain_symbol` | Markdown briefing for a symbol: signature, docs, locations, source and callers. |

## Client configuration

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "mcp-roslyn",
      "args": ["--root", "/home/sven/src/my-repo", "--restrict"]
    }
  }
}
```

Docker: `docker/Dockerfile.roslyn` uses the SDK image, mounts the workspace at `/workspace` and runs with `--restrict --no-scripting` on `http://127.0.0.1:5101/mcp`. Loading a solution inside the container restores packages into the `roslyn-nuget` volume.

## Typical flow

1. `list_workspaces` → pick the `.sln`.
2. `load_solution` → `workspaceId`; check `loadDiagnostics`.
3. `compile_check` to know the baseline.
4. Navigate (`find_symbols`, `get_symbol_info`, `find_references`, `get_call_graph`).
5. Refactor with `apply: false`, read the diff, then `apply: true`.
6. `compile_check` or `test_run` again.
