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
- Microsoft.Graph + MSAL — Outlook.com and M365 accounts, first-class. Sign-in
  goes through the system browser on a loopback redirect, deliberately *not* the
  WAM broker: the broker attaches the account to Windows itself, which is a
  side effect a mail client has no business causing.
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

## App registration (Microsoft identity)

Registered in the personal tenant `example.onmicrosoft.com` (Entra ID Free;
admin accounts: admin@example.onmicrosoft.com + personal@example.net as
guest Global Admin). Client IDs are public by design — safe to commit.

- Application (client) ID: `84d7958a-9db1-4cde-b670-5330d2f8422e`
- Home tenant ID: `bb679c17-b553-48c6-a746-da68c1b22307`
- Display name: `EmailClientForDevs` (placeholder). Product name: **eeeMail**
  pinned as leading candidate (collision-checked clear; ASUS "Eee" dormant;
  eeemail.com looked unclaimed but verify at a registrar before buying).
  Rename the registration only when finalized.
- Supported accounts: any Entra tenant + personal Microsoft accounts
- Platform: public client (mobile & desktop); redirect URIs: `http://localhost`,
  `https://login.microsoftonline.com/common/oauth2/nativeclient`, LiveSDK,
  `msal{clientid}://auth`, and the WAM broker
  (`ms-appx-web://microsoft.aad.brokerplugin/{clientid}`); public client flows
  enabled (device code available).
- Delegated Graph permissions: Mail.ReadWrite, Mail.ReadWrite.Shared,
  Mail.Send, Mail.Send.Shared, offline_access, User.Read.
- Opt-in discovery scopes (People.Read, User.ReadBasic.All) are requested only
  for accounts the user explicitly enables — see Shared mailboxes below.
- Unverified publisher for now: personal MSAs can consent; org tenants may
  require admin consent (John is admin on the target work tenant). Publisher
  verification (MPN + example.com domain verification) is a future task.

## Shared mailboxes

A shared mailbox is a mailbox row under the account whose token opens it. That
is ownership, not merely grouping: lose the account and the shared mailbox
becomes unreachable, so auth is always performed as the owning account while the
request addresses the shared mailbox.

- The sync engine addresses `/users/{upn}` throughout rather than `/me`. For the
  signed-in user's own mailbox the two are the same resource, so there is no
  special case and no second code path. (The Graph SDK generates distinct
  response types per route, but both derive from `BaseDeltaFunctionResponse`,
  so only the `Value` property differs.)
- Each mailbox keeps its own database under its own key. Two accounts may both
  reach one shared mailbox; each holds a separate copy, so the file name carries
  the owning account (`{account}--{mailbox}.db`) to keep them distinct.
- The folder tree groups by account. An account with only its own mailbox stays
  flat — folders hang straight off it — and a mailbox level appears only when
  there is more than one to tell apart, so adding a shared mailbox somewhere does
  not add nesting everywhere.

### Discovery is opt-in, per account

Graph has no "list the mailboxes I can open" endpoint, so discovery triangulates:
the relevance graph (People.Read) surfaces mailboxes already in use, which is
what Outlook automapping produces, and a directory search (User.ReadBasic.All)
covers mailboxes the user holds rights on but has never mailed. Measured against
the real tenants: without these scopes every automatic route returns 403, and
consumer accounts have no directory at all (`memberOf` 404s), so shared mailboxes
are a work/school-tenant feature only.

Both scopes are therefore **off by default and enabled per account**, because
requesting them for every account would force a fresh consent prompt on personal
accounts that can never use the feature — and, more subtly, asking for scopes
that were not granted defeats the MSAL token cache, redeeming the refresh token
on every call until the service throttles. `accounts.discovery_enabled` records
what was actually *granted*, read back from the token rather than assumed from
the request, since a tenant can consent to part of one.

Discovery only ever proposes. Every candidate is confirmed by opening its inbox
before it can be added, and that call is the same permission check Exchange
applies to the user in OWA — so the app can never read mail its user could not
read themselves. Typed-address entry works with no discovery scopes at all.

## Live updates: a long poll, not polling

New mail arrives by holding a connection open, not by asking on a timer.

The routes were measured rather than assumed:

- **Graph change notifications** deliver to a public HTTPS endpoint or an Azure
  Event Hub. A locally installed client has neither, so this is unavailable —
  not merely inconvenient.
- **Graph delta** is explicitly a pull model with no wait semantics. Asking
  repeatedly is all it offers.
- **EWS streaming notifications** are exactly a long poll. `Subscribe` once, then
  `GetStreamingEvents` holds the request open for up to 30 minutes and writes
  events down it as they occur, returning when the window closes so the client
  re-issues it. It uses the Exchange token already held for Autodiscover, so it
  costs no additional permission.
- **IMAP IDLE** would also work — Microsoft deprecated Basic auth for IMAP/POP,
  not the protocols — but needs `IMAP.AccessAsUser.All` on the registration. It
  is the route for non-Microsoft accounts later; it is not needed for these.

Measured end to end: with a connection held open on the work mailbox, a message
sent from another account produced `CreatedEvent, NewMailEvent, ModifiedEvent`
**18.8 seconds later**, about thirteen seconds after the send was accepted.

### Read the stream, do not read the response

The first implementation called `ReadAsStringAsync`, which waits for the whole
body — so every event sat unread until the window closed, and a 30-minute window
was *worse* than a two-minute poll. The response is now read incrementally as
chunks arrive and events are reported the moment they appear. The same test that
showed 300s showed 18.8s afterwards.

Two details that matter:

- The buffer keeps only its tail. A 30-minute window otherwise accumulates
  unbounded keep-alive traffic.
- `ExchangeImpersonation` is sent only when the mailbox differs from the
  signed-in account. Exchange rejects a request impersonating the very user it
  is authenticated as — which is why subscribing to one's own mailbox failed
  until the header was made conditional.

Periodic checks remain as a fallback, configurable and defaulting to two
minutes, for accounts where streaming is unavailable or the permission is not
granted. Returning to the window also triggers a check.

## Theming and layout

Panels are AvalonDock anchorables (Dirkster.AvalonDock 5.0.0, the maintained
fork) — the agreed exception to the low-dependency rule. Folders, the message
list, the reading pane and the activity log each float, dock to any edge, or pin
away into a strip. AvalonDock's own chrome is light regardless of the app
palette, so its surfaces are pointed at the theme brushes, including floating
windows, which are separate top-level windows and would otherwise ignore the
theme entirely.

Two layout constraints worth remembering: `LayoutRoot` takes a single child, so
everything nests inside one panel; and `DockHeight` alone lets a pane collapse to
its title bar, so panes carry `DockMinHeight`.

### Colours are measured, not chosen

The dark palette is One Dark; light is a neutral grey-on-white. Every
foreground/background pair is checked against WCAG AA (4.5:1) by tests in
`PaletteTests`, and that has caught real defects three times:

- One Dark's own red measures 4.38:1 on its own background — under AA. A
  well-regarded palette still fails when measured.
- Secondary text on a selected row measured 3.16:1 in dark: readable until you
  click it. Selected rows now use the primary colour, since secondary cannot
  reach AA on a selection tint without becoming indistinguishable from primary.
- A "raised" surface set to pure white gave buttons no edge against a white
  background at all.

Dark body text avoids pure white deliberately — it haloes against a dark ground,
which is why the dark theme is not an inversion of the light one.

Brushes resolve through `DynamicResource`, and code-behind reads them per access
rather than caching, so a theme switch restyles open windows rather than
requiring a restart. Implicit control styles are what make this work at all:
most WPF panels never set a `Background`, so replacing individual colours left
the app entirely light until every control had a themed style.

### Density

Dense on purpose — a mail client is a data table. Spacing sits on a 4px grid,
rows are 22px, numeric columns use a monospace font so figures align
column-wise, and hierarchy comes from weight and colour rather than whitespace:
column headers recede to secondary, unread stays bold, and the selected row
carries a coloured left edge as well as a fill.

## UI testing

Screenshots show layout; they do not show what a control *is*. The accessibility
tree does, and it is the same tree screen readers use — so a defect found there
is a defect for real users, not just for automation.

Driven with the `uia` CLI from the sibling UIAutomation project, using its
**published** build (`releases\current`), never `bin\Debug`: that is another
session's working copy, rebuilt constantly, and holding its assemblies breaks
their build. A published binary reports a version stamp; a working copy says
`uia dev+local`.

    uia inputs Settings --depth 14 --json    every input element with its value
    uia windows --json                       owner / isOwned / bounds per window

`uia inputs` produces a diffable snapshot of every control and its live value,
which catches what a screenshot cannot: a control that lost its name, a default
that quietly changed, a setting missing from a generated list.

Three faults found this way, none visible in a screenshot:

- Generated settings rows presented as `(unnamed)`, because an `ItemsControl`
  gives its children no `AutomationProperties` unless told to. Unusable for
  automation and mute to a screen reader; they now carry the setting's name, its
  key as AutomationId, and its description as help text.
- Message rows exposed a record's generated `ToString()` as their accessible
  name, leaking the object graph including the mailbox handle.
- Setting a value produced `"ser6"` from `"6"` — `UpdateSourceTrigger=PropertyChanged`
  wrote back mid-edit and interleaved with the incoming text. A fast typist hits
  this too; automation merely found it first.

### Owned dialogs

Dialogs shown with `ShowDialog()` and an Owner are **not** children of the
desktop in the UIA tree, so `RootElement.FindAll(TreeScope.Children, …)` misses
them: a dialog plainly on screen looks like it never opened. Enumerate with
`EnumWindows` + `AutomationElement.FromHandle` instead, which also carries the
ownership relationship UIA does not expose.

Reading "not found" as "did not open" caused a button to be invoked twice here,
stacking two dialogs — worth knowing before trusting a negative result.

### Other traps

- `InvokePattern.Invoke()` on a button opening a modal dialog returns before the
  window is enumerable. Poll; do not assume.
- Coordinate clicking is unreliable across multiple monitors — coordinates read
  off a screenshot were out by a whole monitor origin. Use element invocation or
  the `bounds` field.
- `SetForegroundWindow` is blocked for background processes, so anything
  requiring focus first fails intermittently.
- Screenshots that must work regardless of occlusion use `PrintWindow` with
  `PW_RENDERFULLCONTENT` against a window handle: no focus stealing, and it
  captures a fully covered window correctly.

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
