using McpServices.Storage;

namespace McpServices.Index;

/// <summary>
/// Content-addressed layout: <c>files</c> map a path in a repository to a content hash; symbols,
/// chunks and embeddings hang off the hash, so branch switches, renames and worktrees reuse work.
/// </summary>
public static class IndexSchema
{
    public const string WriterLockPrefix = "index-";

    public static IReadOnlyList<Migration> Migrations { get; } =
    [
        new(1, "initial",
            SqliteSql: """
                CREATE TABLE repositories (
                    repo_id TEXT PRIMARY KEY,
                    root TEXT NOT NULL,
                    name TEXT NOT NULL,
                    created_at BIGINT NOT NULL,
                    last_indexed_at BIGINT,
                    head_sha TEXT,
                    generation INTEGER NOT NULL DEFAULT 0,
                    file_count INTEGER NOT NULL DEFAULT 0,
                    symbol_count INTEGER NOT NULL DEFAULT 0,
                    chunk_count INTEGER NOT NULL DEFAULT 0,
                    last_duration_ms INTEGER,
                    last_error TEXT
                );
                CREATE TABLE files (
                    repo_id TEXT NOT NULL,
                    path TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    mtime_ms BIGINT NOT NULL,
                    language TEXT NOT NULL,
                    generation INTEGER NOT NULL,
                    indexed_at BIGINT NOT NULL,
                    PRIMARY KEY (repo_id, path)
                );
                CREATE INDEX files_hash ON files(content_hash);
                CREATE TABLE contents (
                    content_hash TEXT PRIMARY KEY,
                    language TEXT NOT NULL,
                    line_count INTEGER NOT NULL,
                    symbol_count INTEGER NOT NULL,
                    chunk_count INTEGER NOT NULL,
                    first_seen_at BIGINT NOT NULL
                );
                CREATE TABLE symbols (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    content_hash TEXT NOT NULL,
                    name TEXT NOT NULL,
                    full_name TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    container TEXT,
                    signature TEXT NOT NULL,
                    doc TEXT,
                    start_line INTEGER NOT NULL,
                    end_line INTEGER NOT NULL,
                    tokens TEXT NOT NULL
                );
                CREATE INDEX symbols_hash ON symbols(content_hash);
                CREATE INDEX symbols_name ON symbols(name);
                CREATE VIRTUAL TABLE symbols_fts USING fts5(name, full_name, signature, doc, tokens, content='symbols', content_rowid='id', tokenize='porter unicode61');
                CREATE TRIGGER symbols_ai AFTER INSERT ON symbols BEGIN
                    INSERT INTO symbols_fts(rowid, name, full_name, signature, doc, tokens) VALUES (new.id, new.name, new.full_name, new.signature, new.doc, new.tokens);
                END;
                CREATE TRIGGER symbols_ad AFTER DELETE ON symbols BEGIN
                    INSERT INTO symbols_fts(symbols_fts, rowid, name, full_name, signature, doc, tokens) VALUES ('delete', old.id, old.name, old.full_name, old.signature, old.doc, old.tokens);
                END;
                CREATE TABLE chunks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    content_hash TEXT NOT NULL,
                    ordinal INTEGER NOT NULL,
                    start_line INTEGER NOT NULL,
                    end_line INTEGER NOT NULL,
                    heading TEXT,
                    text TEXT NOT NULL,
                    tokens TEXT NOT NULL
                );
                CREATE INDEX chunks_hash ON chunks(content_hash, ordinal);
                CREATE VIRTUAL TABLE chunks_fts USING fts5(heading, text, tokens, content='chunks', content_rowid='id', tokenize='porter unicode61');
                CREATE TRIGGER chunks_ai AFTER INSERT ON chunks BEGIN
                    INSERT INTO chunks_fts(rowid, heading, text, tokens) VALUES (new.id, new.heading, new.text, new.tokens);
                END;
                CREATE TRIGGER chunks_ad AFTER DELETE ON chunks BEGIN
                    INSERT INTO chunks_fts(chunks_fts, rowid, heading, text, tokens) VALUES ('delete', old.id, old.heading, old.text, old.tokens);
                END;
                CREATE TABLE embeddings (
                    content_hash TEXT NOT NULL,
                    ordinal INTEGER NOT NULL,
                    model TEXT NOT NULL,
                    vector BLOB NOT NULL,
                    PRIMARY KEY (content_hash, ordinal, model)
                );
                CREATE TABLE notes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    repo_id TEXT NOT NULL,
                    note TEXT NOT NULL,
                    files TEXT NOT NULL DEFAULT '[]',
                    file_hashes TEXT NOT NULL DEFAULT '[]',
                    tags TEXT NOT NULL DEFAULT '[]',
                    tokens TEXT NOT NULL,
                    useful_count INTEGER NOT NULL DEFAULT 0,
                    created_at BIGINT NOT NULL,
                    updated_at BIGINT NOT NULL
                );
                CREATE INDEX notes_repo ON notes(repo_id);
                CREATE VIRTUAL TABLE notes_fts USING fts5(note, tags, tokens, content='notes', content_rowid='id', tokenize='porter unicode61');
                CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN
                    INSERT INTO notes_fts(rowid, note, tags, tokens) VALUES (new.id, new.note, new.tags, new.tokens);
                END;
                CREATE TRIGGER notes_ad AFTER DELETE ON notes BEGIN
                    INSERT INTO notes_fts(notes_fts, rowid, note, tags, tokens) VALUES ('delete', old.id, old.note, old.tags, old.tokens);
                END;
                CREATE TRIGGER notes_au AFTER UPDATE ON notes BEGIN
                    INSERT INTO notes_fts(notes_fts, rowid, note, tags, tokens) VALUES ('delete', old.id, old.note, old.tags, old.tokens);
                    INSERT INTO notes_fts(rowid, note, tags, tokens) VALUES (new.id, new.note, new.tags, new.tokens);
                END;
                CREATE TABLE feedback (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    repo_id TEXT NOT NULL,
                    query_tokens TEXT NOT NULL,
                    target TEXT NOT NULL,
                    created_at BIGINT NOT NULL
                );
                CREATE INDEX feedback_repo ON feedback(repo_id, target);
                CREATE TABLE related_cache (
                    repo_id TEXT NOT NULL,
                    head_sha TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    created_at BIGINT NOT NULL,
                    PRIMARY KEY (repo_id)
                );
                """,
            PostgresSql: """
                CREATE TABLE repositories (
                    repo_id TEXT PRIMARY KEY,
                    root TEXT NOT NULL,
                    name TEXT NOT NULL,
                    created_at BIGINT NOT NULL,
                    last_indexed_at BIGINT,
                    head_sha TEXT,
                    generation INTEGER NOT NULL DEFAULT 0,
                    file_count INTEGER NOT NULL DEFAULT 0,
                    symbol_count INTEGER NOT NULL DEFAULT 0,
                    chunk_count INTEGER NOT NULL DEFAULT 0,
                    last_duration_ms INTEGER,
                    last_error TEXT
                );
                CREATE TABLE files (
                    repo_id TEXT NOT NULL,
                    path TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    size BIGINT NOT NULL,
                    mtime_ms BIGINT NOT NULL,
                    language TEXT NOT NULL,
                    generation INTEGER NOT NULL,
                    indexed_at BIGINT NOT NULL,
                    PRIMARY KEY (repo_id, path)
                );
                CREATE INDEX files_hash ON files(content_hash);
                CREATE TABLE contents (
                    content_hash TEXT PRIMARY KEY,
                    language TEXT NOT NULL,
                    line_count INTEGER NOT NULL,
                    symbol_count INTEGER NOT NULL,
                    chunk_count INTEGER NOT NULL,
                    first_seen_at BIGINT NOT NULL
                );
                CREATE TABLE symbols (
                    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    content_hash TEXT NOT NULL,
                    name TEXT NOT NULL,
                    full_name TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    container TEXT,
                    signature TEXT NOT NULL,
                    doc TEXT,
                    start_line INTEGER NOT NULL,
                    end_line INTEGER NOT NULL,
                    tokens TEXT NOT NULL,
                    tsv tsvector GENERATED ALWAYS AS (
                        setweight(to_tsvector('english', name), 'A') ||
                        setweight(to_tsvector('english', full_name), 'B') ||
                        setweight(to_tsvector('english', tokens), 'B') ||
                        setweight(to_tsvector('english', signature), 'C') ||
                        setweight(to_tsvector('english', coalesce(doc, '')), 'D')) STORED
                );
                CREATE INDEX symbols_hash ON symbols(content_hash);
                CREATE INDEX symbols_name ON symbols(name);
                CREATE INDEX symbols_tsv ON symbols USING GIN (tsv);
                CREATE TABLE chunks (
                    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    content_hash TEXT NOT NULL,
                    ordinal INTEGER NOT NULL,
                    start_line INTEGER NOT NULL,
                    end_line INTEGER NOT NULL,
                    heading TEXT,
                    text TEXT NOT NULL,
                    tokens TEXT NOT NULL,
                    tsv tsvector GENERATED ALWAYS AS (
                        setweight(to_tsvector('english', coalesce(heading, '')), 'A') ||
                        setweight(to_tsvector('english', tokens), 'B') ||
                        setweight(to_tsvector('english', text), 'C')) STORED
                );
                CREATE INDEX chunks_hash ON chunks(content_hash, ordinal);
                CREATE INDEX chunks_tsv ON chunks USING GIN (tsv);
                CREATE TABLE embeddings (
                    content_hash TEXT NOT NULL,
                    ordinal INTEGER NOT NULL,
                    model TEXT NOT NULL,
                    vector BYTEA NOT NULL,
                    PRIMARY KEY (content_hash, ordinal, model)
                );
                CREATE TABLE notes (
                    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    repo_id TEXT NOT NULL,
                    note TEXT NOT NULL,
                    files TEXT NOT NULL DEFAULT '[]',
                    file_hashes TEXT NOT NULL DEFAULT '[]',
                    tags TEXT NOT NULL DEFAULT '[]',
                    tokens TEXT NOT NULL,
                    useful_count INTEGER NOT NULL DEFAULT 0,
                    created_at BIGINT NOT NULL,
                    updated_at BIGINT NOT NULL,
                    tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', note || ' ' || tags || ' ' || tokens)) STORED
                );
                CREATE INDEX notes_repo ON notes(repo_id);
                CREATE INDEX notes_tsv ON notes USING GIN (tsv);
                CREATE TABLE feedback (
                    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    repo_id TEXT NOT NULL,
                    query_tokens TEXT NOT NULL,
                    target TEXT NOT NULL,
                    created_at BIGINT NOT NULL
                );
                CREATE INDEX feedback_repo ON feedback(repo_id, target);
                CREATE TABLE related_cache (
                    repo_id TEXT NOT NULL,
                    head_sha TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    created_at BIGINT NOT NULL,
                    PRIMARY KEY (repo_id)
                );
                """),
    ];
}
