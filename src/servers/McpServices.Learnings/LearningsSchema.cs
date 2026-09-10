using McpServices.Storage;

namespace McpServices.Learnings;

/// <summary>
/// Four tables: <c>learnings</c> (deduplicated by fingerprint, with an occurrence counter),
/// <c>feedback</c> (tool-call level thumbs), <c>proposals</c> (aggregated improvement requests) and
/// <c>dispatches</c> (a log of every attempt to hand a proposal to Cursor).
/// </summary>
public static class LearningsSchema
{
    public static IReadOnlyList<Migration> Migrations { get; } =
    [
        new(1, "initial",
            SqliteSql: """
                CREATE TABLE learnings (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    fingerprint TEXT NOT NULL UNIQUE,
                    outcome TEXT NOT NULL,
                    category TEXT NOT NULL,
                    title TEXT NOT NULL,
                    detail TEXT NOT NULL DEFAULT '',
                    tags TEXT NOT NULL DEFAULT '[]',
                    repo TEXT,
                    files TEXT NOT NULL DEFAULT '[]',
                    tool_or_skill TEXT,
                    provider TEXT,
                    model TEXT,
                    evidence TEXT,
                    source TEXT NOT NULL,
                    meta TEXT NOT NULL DEFAULT '{}',
                    occurrences INTEGER NOT NULL DEFAULT 1,
                    useful_count INTEGER NOT NULL DEFAULT 0,
                    tokens TEXT NOT NULL,
                    created_at BIGINT NOT NULL,
                    updated_at BIGINT NOT NULL,
                    last_seen_at BIGINT NOT NULL
                );
                CREATE INDEX learnings_repo ON learnings(repo, outcome);
                CREATE INDEX learnings_seen ON learnings(last_seen_at);
                CREATE VIRTUAL TABLE learnings_fts USING fts5(title, detail, tags, tokens, content='learnings', content_rowid='id', tokenize='porter unicode61');
                CREATE TRIGGER learnings_ai AFTER INSERT ON learnings BEGIN
                    INSERT INTO learnings_fts(rowid, title, detail, tags, tokens) VALUES (new.id, new.title, new.detail, new.tags, new.tokens);
                END;
                CREATE TRIGGER learnings_ad AFTER DELETE ON learnings BEGIN
                    INSERT INTO learnings_fts(learnings_fts, rowid, title, detail, tags, tokens) VALUES ('delete', old.id, old.title, old.detail, old.tags, old.tokens);
                END;
                CREATE TRIGGER learnings_au AFTER UPDATE ON learnings BEGIN
                    INSERT INTO learnings_fts(learnings_fts, rowid, title, detail, tags, tokens) VALUES ('delete', old.id, old.title, old.detail, old.tags, old.tokens);
                    INSERT INTO learnings_fts(rowid, title, detail, tags, tokens) VALUES (new.id, new.title, new.detail, new.tags, new.tokens);
                END;
                CREATE TABLE feedback (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tool TEXT NOT NULL,
                    worked INTEGER NOT NULL,
                    note TEXT,
                    repo TEXT,
                    provider TEXT,
                    created_at BIGINT NOT NULL
                );
                CREATE INDEX feedback_tool ON feedback(tool, created_at);
                CREATE TABLE proposals (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    status TEXT NOT NULL,
                    target_repo TEXT NOT NULL,
                    title TEXT NOT NULL,
                    rationale TEXT NOT NULL,
                    learning_ids TEXT NOT NULL DEFAULT '[]',
                    likely_files TEXT NOT NULL DEFAULT '[]',
                    acceptance_criteria TEXT NOT NULL DEFAULT '[]',
                    prompt TEXT NOT NULL,
                    corroborations INTEGER NOT NULL DEFAULT 0,
                    dispatch_mode TEXT,
                    external_id TEXT,
                    pr_url TEXT,
                    last_error TEXT,
                    created_at BIGINT NOT NULL,
                    updated_at BIGINT NOT NULL
                );
                CREATE INDEX proposals_status ON proposals(status, created_at);
                CREATE TABLE dispatches (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    proposal_id INTEGER NOT NULL,
                    mode TEXT NOT NULL,
                    request_id TEXT NOT NULL,
                    external_id TEXT,
                    status TEXT NOT NULL,
                    detail TEXT,
                    created_at BIGINT NOT NULL
                );
                CREATE INDEX dispatches_proposal ON dispatches(proposal_id, created_at);
                CREATE INDEX dispatches_created ON dispatches(created_at);
                """,
            PostgresSql: """
                CREATE TABLE learnings (
                    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    fingerprint TEXT NOT NULL UNIQUE,
                    outcome TEXT NOT NULL,
                    category TEXT NOT NULL,
                    title TEXT NOT NULL,
                    detail TEXT NOT NULL DEFAULT '',
                    tags TEXT NOT NULL DEFAULT '[]',
                    repo TEXT,
                    files TEXT NOT NULL DEFAULT '[]',
                    tool_or_skill TEXT,
                    provider TEXT,
                    model TEXT,
                    evidence TEXT,
                    source TEXT NOT NULL,
                    meta TEXT NOT NULL DEFAULT '{}',
                    occurrences INTEGER NOT NULL DEFAULT 1,
                    useful_count INTEGER NOT NULL DEFAULT 0,
                    tokens TEXT NOT NULL,
                    created_at BIGINT NOT NULL,
                    updated_at BIGINT NOT NULL,
                    last_seen_at BIGINT NOT NULL,
                    tsv tsvector GENERATED ALWAYS AS (
                        setweight(to_tsvector('english', title), 'A') ||
                        setweight(to_tsvector('english', tags), 'B') ||
                        setweight(to_tsvector('english', tokens), 'B') ||
                        setweight(to_tsvector('english', detail), 'C')) STORED
                );
                CREATE INDEX learnings_repo ON learnings(repo, outcome);
                CREATE INDEX learnings_seen ON learnings(last_seen_at);
                CREATE INDEX learnings_tsv ON learnings USING GIN (tsv);
                CREATE TABLE feedback (
                    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    tool TEXT NOT NULL,
                    worked INTEGER NOT NULL,
                    note TEXT,
                    repo TEXT,
                    provider TEXT,
                    created_at BIGINT NOT NULL
                );
                CREATE INDEX feedback_tool ON feedback(tool, created_at);
                CREATE TABLE proposals (
                    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    status TEXT NOT NULL,
                    target_repo TEXT NOT NULL,
                    title TEXT NOT NULL,
                    rationale TEXT NOT NULL,
                    learning_ids TEXT NOT NULL DEFAULT '[]',
                    likely_files TEXT NOT NULL DEFAULT '[]',
                    acceptance_criteria TEXT NOT NULL DEFAULT '[]',
                    prompt TEXT NOT NULL,
                    corroborations INTEGER NOT NULL DEFAULT 0,
                    dispatch_mode TEXT,
                    external_id TEXT,
                    pr_url TEXT,
                    last_error TEXT,
                    created_at BIGINT NOT NULL,
                    updated_at BIGINT NOT NULL
                );
                CREATE INDEX proposals_status ON proposals(status, created_at);
                CREATE TABLE dispatches (
                    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    proposal_id BIGINT NOT NULL,
                    mode TEXT NOT NULL,
                    request_id TEXT NOT NULL,
                    external_id TEXT,
                    status TEXT NOT NULL,
                    detail TEXT,
                    created_at BIGINT NOT NULL
                );
                CREATE INDEX dispatches_proposal ON dispatches(proposal_id, created_at);
                CREATE INDEX dispatches_created ON dispatches(created_at);
                """),
    ];
}
