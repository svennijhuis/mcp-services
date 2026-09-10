# mcp-learnings

The self-improvement loop. Agents record what worked and what failed while they work; the server deduplicates, corroborates and decays those learnings, answers "what should I do / avoid for this task" before the next session starts, and turns recurring lessons into improvement proposals that can be handed to Cursor (Cloud Agents API or the local `agent` CLI) to open a draft PR against, for example, `svennijhuis/agentPacks`.

Project: `src/servers/McpServices.Learnings` · Tests: `tests/McpServices.Learnings.Tests`

## Running

```
mcp-learnings [transport options] [--repo <owner/name>] [--store sqlite:<path>|postgres:<conn>] [--dispatch off|manual|auto] [--dispatch-mode dry-run|cursor-cloud|cursor-cli|webhook]
mcp-learnings record --worked|--failed|--partial "<title>" [--detail ..] [--tag ..]... [--tool ..] [--repo ..]     # CLI mode
mcp-learnings import <docs/learnings.md> | export <path> [--since 30d] | recommend "<task>" | stats
```

| Option | Meaning |
| --- | --- |
| `--repo <owner/name>` | Default repository for learnings and proposals; env `MCP_LEARNINGS_REPO`. |
| `--target-repo <owner/name>` (repeatable) | Extra repositories proposals may target; env `MCP_LEARNINGS_TARGET_REPOS` (comma separated). Anything else is refused. |
| `--store <spec>` | `sqlite:<file>` (default `~/.mcp-services/mcp-learnings/learnings.db`) or `postgres:<conn>`; env `MCP_LEARNINGS_STORE`. |
| `--data-dir <dir>` | Where dry-run proposal files are written (default `~/.mcp-services/mcp-learnings`). |
| `--dispatch off\|manual\|auto` | `off`: only dry-run. `manual` (default): `dispatch_proposal` may go external. `auto`: `create_proposal` dispatches immediately when the guard rails allow it. |
| `--dispatch-mode` | Default mode for `dispatch_proposal` (default `dry-run`). |
| `--min-corroborations <n>` | Learning occurrences required before an external dispatch (default 3). |
| `--max-dispatches-per-day <n>` | Rolling-day cap on external dispatches (default 2). |
| `--base-ref <branch>` | Branch proposals start from (default `main`). |
| `--cursor-api <url>` | Cloud Agents API base (default `https://api.cursor.com`). Key from env `CURSOR_API_KEY` only. |
| `--cursor-model <model>` | Model for cloud agents (default: Cursor picks). |
| `--cursor-cli <command>` | Cursor CLI executable for `cursor-cli` mode (default `agent`). |
| `--webhook <url>` | Webhook for `webhook` mode; bearer token from env `MCP_LEARNINGS_WEBHOOK_TOKEN`. |
| `--decay-half-life-days <n>` | Confidence half-life (default 90). |

Secrets (`CURSOR_API_KEY`, `MCP_LEARNINGS_WEBHOOK_TOKEN`) are read from the process environment only; there is no flag for them, and the agentPacks `mcp.json` validator would reject them in `env` anyway.

## Tools

### Recording and retrieving

| Tool | Purpose |
| --- | --- |
| `record_learning` | `worked`/`failed`/`partial` with title, detail, category, tags, tool, files, model. Repeats of the same learning bump `occurrences` instead of duplicating. |
| `record_feedback` | Thumbs up/down for a tool, server or skill; aggregated by `summarize_period`. |
| `get_recommendations` | Before a task: a do / avoid / caution list with confidence and evidence for this repo (plus global learnings). Conflicting learnings are surfaced as cautions. |
| `query_learnings` | Full-text search with filters (repo, outcome, category, tag, tool, since). |
| `mark_learning_useful` | Boost recommendations that actually helped. |
| `forget_learning` | Delete a wrong or obsolete learning. |
| `summarize_period` | Digest for a period (`7d`, `30d`, ISO date): counts, most corroborated learnings, tools with most negative feedback, proposals. |

### Squad compatibility

| Tool | Purpose |
| --- | --- |
| `import_learnings_md` | Import a squad-style `docs/learnings.md` (append-only `## date — /squad` entries) or an export from this server. Idempotent. |
| `export_learnings_md` | Append learnings to a markdown log in the same shape; squad entries round-trip exactly. |

The CLI equivalents (`mcp-learnings import|export`) let a git hook or CI step keep the markdown file and the store in sync without an MCP session.

### Proposals and dispatch

| Tool | Purpose |
| --- | --- |
| `create_proposal` | Aggregate learnings (by ids or filter) into a proposal: target repo, rationale, likely files, acceptance criteria and a ready-to-run agent prompt. |
| `list_proposals` / `get_proposal` | Browse (statuses: draft, dispatched, pr_opened, merged, rejected, failed); full prompt and dispatch history. |
| `dispatch_proposal` | Hand a proposal to something that opens a draft PR. Modes: `dry-run` (prompt to a file), `cursor-cloud` (Cloud Agents API), `cursor-cli` (local `agent` + git + gh), `webhook`. |
| `get_dispatch_status` | Poll the Cloud Agent and record the PR URL once available. |
| `update_proposal_status` | Set merged/rejected after human review, optionally with the PR URL. |

Resource `learnings://proposals/{id}`; prompt `improvement_pr` renders a proposal as an instruction for an agent.

## Guard rails for external dispatch

An external dispatch (anything other than dry-run) only happens when all of these hold:

1. `--dispatch` is not `off`.
2. The proposal is `draft` or `failed` (no double dispatch).
3. The target repo is `--repo` or in `--target-repo`.
4. The proposal's corroborations reach `--min-corroborations`.
5. Fewer than `--max-dispatches-per-day` external dispatches happened in the last 24 hours.
6. The chosen dispatcher is available (`CURSOR_API_KEY` set, `agent` on `PATH`, webhook configured).

Every dispatch is recorded with request id, external id, status and detail, so the trail from learning → proposal → PR is auditable. PRs are always opened as drafts; a human merges.

## Client configuration

```json
{
  "mcpServers": {
    "learnings": {
      "command": "mcp-learnings",
      "args": ["--repo", "svennijhuis/agentPacks", "--dispatch", "manual"]
    }
  }
}
```

Docker: the `learnings` service shares the bundled PostgreSQL with `mcp-index`, reads `MCP_LEARNINGS_REPO`, `MCP_LEARNINGS_TARGET_REPOS`, `MCP_LEARNINGS_DISPATCH` and passes `CURSOR_API_KEY` through from the host environment; it listens on `http://127.0.0.1:5104/mcp`.

## Suggested workflow for an agent

1. `get_recommendations` with a one-line description of the task.
2. Do the work.
3. `record_learning` for anything surprising (worked or failed), `record_feedback` for tools.
4. Weekly (or via automation): `summarize_period`, `create_proposal` for learnings with several occurrences, `dispatch_proposal` in `dry-run` to read the prompt, then `cursor-cloud` when it looks right.
