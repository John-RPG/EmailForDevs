# Architecture

A Windows email client for developers: nothing hidden (real addresses, raw
headers, MIME tree, HTML source always one click away), dense keyboard-driven
UI, honest local storage, fast search.

## Stack

- .NET 10 (LTS), WPF, Windows-only.
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
- `bodies` — raw RFC 5322 message, Brotli-compressed. Source of truth; also
  powers view-source/headers/MIME-tree UI.
- `addresses` — deduplicated per mailbox
- `attachments` — content-addressed by hash (dedup of repeated attachments)
- FTS5 tables over decoded text + subject

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

## Non-negotiable UX principles

- Always show the real From/To addresses, never just a contact display name.
- Headers, MIME structure, and raw source viewable full-window, not a dialog.
- Compact, consistent density; no wasted whitespace between accounts.
- Any mailbox (incl. shared) can be hidden/shown from the tree trivially.
