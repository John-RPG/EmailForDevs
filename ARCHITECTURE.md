# Architecture

A Windows email client for developers: nothing hidden (real addresses, raw
headers, MIME tree, HTML source always one click away), dense keyboard-driven
UI, honest local storage, fast search.

## Stack

- .NET 10 (LTS), WPF, Windows-only.
- **Low dependency count is a hard rule.** No third-party UI suites (DevExpress/
  Telerik/Syncfusion serve as interaction-design references only), no ORM, no DI
  framework. New packages must earn their place; prefer BCL and hand-rolled.
- MailKit/MimeKit — IMAP/SMTP/POP3 + MIME parsing.
- Microsoft.Graph + MSAL (WAM broker) — Outlook.com and M365 accounts, first-class.
- Microsoft.Data.Sqlite — storage, FTS5 for search.
- Brotli (built-in) — compression of raw message blobs.

## Projects

| Project | Purpose | Rules |
|---|---|---|
| `Mail.Core` | Domain models, MIME handling, threading | No UI, no storage, no network deps (MimeKit only) |
| `Mail.Storage` | SQLite schemas, repositories, FTS, compression | Depends on Core only |
| `Mail.Sync` | Graph + IMAP/POP sync engines, auth, sync state machines | Depends on Core + Storage |
| `Mail.App` | WPF shell, MVVM | Thin; all logic lives below |
| `Mail.Tests` | xUnit | Core + Storage covered first |

## Concepts

- **Identity** — a person/persona grouping (John-personal, John-work). Display
  concept only; a row, not a database.
- **Account** — an auth realm (Hotmail account, work M365 account). Owns
  credentials/tokens. One account can expose many mailboxes.
- **Mailbox** — a final mail store: a primary mailbox or a shared/delegated
  mailbox. The unit of sync, storage, search, and backup.

Tree view: identity → account → mailbox → folders.

## Encryption

All databases are SQLCipher-encrypted (`SQLitePCLRaw.bundle_e_sqlcipher` +
`Microsoft.Data.Sqlite.Core`), keyed with **raw 32-byte keys** (no passphrase
KDF on connection open). Key hierarchy:

```
DPAPI (CurrentUser) → master.key → app.db → per-mailbox DEKs → {upn}.db
```

- Each mailbox DB has its own random 32-byte DEK, stored in the `mailboxes`
  registry inside app.db (which is itself encrypted — envelope closed).
- The profile master key is wrapped **twice** on disk (`ProfileKeyStore`):
  - `master.key` — DPAPI CurrentUser; silent day-to-day unlock, machine+user bound.
  - `recovery.blob` — AES-256-GCM under PBKDF2-SHA256 (600k iterations) of a
    **recovery code** (8×4 Crockford base32, shown once at profile creation).
    Machine-independent: survives Windows reinstalls and travels with backups.
    Recovering also rewrites `master.key` for the current Windows user.
- Threat model: protects against other local users, casual file copying, and
  offline disk access. Does not protect against malware running as the user
  while the app holds keys in memory (nothing local can).

## Profiles and multi-user

Multiple Windows users are isolated for free (`%LOCALAPPDATA%` + DPAPI
CurrentUser). Multiple profiles per Windows user (Outlook-style) are folder-
scoped, each fully self-contained:

```
%LOCALAPPDATA%\<AppName>\
  profiles.json                 ← names + paths only, nothing secret
  profiles\<Name>\
    master.key
    recovery.blob
    app.db
    mailboxes\{upn}.db
```

## Storage: two tiers

### `app.db` (one per install)

- `identities`, `accounts` (auth config only — **no secrets**; MSAL token cache
  is a separate DPAPI-protected file, IMAP/POP passwords go to Windows
  Credential Manager)
- `mailboxes` registry: enabled/visible, DB file path, sync policy
- global address/autocomplete ranking (fed from all mailboxes)
- UI state (tree expansion, column layouts)

### `{upn}.db` (one per mailbox, shared mailboxes included)

Named by the mailbox's UPN/SMTP address (sanitized for the filesystem) so files
are human-identifiable; the `mailboxes` registry stores the actual path, so the
name is cosmetic and survives UPN changes. Contains:

- `folders`, `messages` (envelope, flags, threading), `sync_state` (Graph delta
  tokens; IMAP UIDVALIDITY/HIGHESTMODSEQ per folder)
- `blobs` — **content-addressed store** (SHA-256, Brotli-compressed), shared by
  everything below. Attachments live here (inside the DB, not loose files, so
  encryption covers them).
- `bodies` + `body_segments` — a message's raw RFC 5322 source is an *ordered
  list of blob references*. Large repeated MIME parts (signature images, logos,
  re-forwarded attachments) are stored once mailbox-wide; concatenating the
  segments reconstructs the original **byte-for-byte** (preserves DKIM
  verifiability and honest view-source). Orphaned blobs are swept by GC after
  deletes.
- `addresses` — deduplicated per mailbox
- `attachments` — metadata rows pointing into `blobs` (NULL until fetched)
- `messages_fts` — FTS5 over subject / body text / participants

Rationale: backup = copy `app.db` + chosen mailbox files; detach a shared
mailbox = delete one file. No per-identity DB — nothing lives at that level.

## Sync policies (per mailbox)

- `MirrorServer` — full replica.
- `WindowedCache(months)` — only recent mail cached locally.
- `LocalArchive` — POP-style: download then delete from server; local DB is the
  only copy (registry flags this for backup warnings).

## Search

Search orchestrator fans out one FTS5 query per enabled mailbox DB in parallel
(no `ATTACH` — low compile-time cap) and merges results. Mailboxes with
`WindowedCache` also get a server-side query (Graph `$search`; IMAP `SEARCH`
later), merged and deduped by Internet-Message-ID; server-only hits are flagged
in the UI.

Queries are a typed **AST in Mail.Core** (and/or/not groups, property
comparisons — from/to/subject/date/size/flags/has-attachment — ranges, and
full-text terms). The AST compiles to local SQL+FTS5 and, where expressible, to
Graph `$filter`/`$search`; unsupported fragments fall back to local-only with
the limitation surfaced. The planned filter-editor UI (token/pill style with
completion, DevExpress-FilterEditor-like in *behavior* — built in-house in pure
WPF, no third-party control) is just a front-end that produces this AST; plain
text search is the degenerate case.

## Threading

Conversation keys are computed at ingest from References/In-Reply-To
(JWZ-style): adopt the key of any referenced message; merge keys when a message
bridges several; handle out-of-order arrival via a `message_references` table
(a late-arriving parent finds children that referenced it and merges). Server
conversation ids (Graph conversationId) will be stored alongside when sync
lands, but our own keys are authoritative so behavior is identical across
Graph/IMAP/POP.

## HTML rendering (WebView2, locked down)

Defense in depth: (1) WebResourceRequested filter on `*` — the engine gets all
content from us (virtual host + cid: from blob store) and every external
request is cancelled; opt-in remote images are fetched by our own sterile
HttpClient and cached, never by the engine. (2) Scripts/web-messages/host
objects/DevTools/autofill disabled in settings. (3) DOM sanitization before
serving (strip script/iframe/object/forms/on*/javascript:/meta-refresh).
(4) All navigation cancelled; links show their real target and open in the
system browser on confirm. (5) Throwaway in-memory profile; Chromium sandbox.
Plain-text / simplified / raw-source views always available.

## UI layout

Three-pane: tree | message list | reading pane (right, switchable to bottom or
off), message popouts for multi-monitor. Shell starts with fixed GridSplitter
panes; AvalonDock is the approved candidate exception for real docking once
panel count justifies it. Message list rows configurable 1–3 lines per folder
(1 = dense grid columns; 2–3 add subject/preview lines; custom tiles maybe
later).

## Non-negotiable UX principles

- Always show the real From/To addresses, never just a contact display name.
- Headers, MIME structure, and raw source viewable full-window, not a dialog.
- Compact, consistent density; no wasted whitespace between accounts.
- Any mailbox (incl. shared) can be hidden/shown from the tree trivially.
