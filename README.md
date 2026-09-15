# eeeMail

A mail client for people who read headers.

Most mail clients work hard to hide the mail. They show you a display name
instead of the address it actually came from, bury the headers, hide the MIME
tree, and quietly re-render the HTML so you cannot see what the sender wrote.
eeeMail does the opposite: real addresses, full headers, raw MIME, every
attachment including the inline parts, and the HTML source always one click
away.

**Status: early developer preview.** It syncs and reads real mail, but it has
had roughly one user. Expect rough edges.

## Install

```powershell
irm https://raw.githubusercontent.com/John-RPG/EmailForDevs/master/install.ps1 | iex
```

Installs to `%LOCALAPPDATA%\Programs\eeeMail`, no administrator rights needed.
The script checks the download against the SHA-256 digest GitHub publishes for
the release and refuses to install anything that does not match.

Or [download the zip](https://github.com/John-RPG/EmailForDevs/releases/latest)
and unpack it yourself — it is a single self-contained executable, so there is
nothing else to install.

### Uninstall

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/John-RPG/EmailForDevs/master/install.ps1))) -Uninstall
```

Piping to `iex` cannot take arguments — `| iex -Uninstall` binds the switch to
`iex`, which ignores it and installs instead. Hence the scriptblock.

Your mail databases and settings are left alone; only the program is removed.

## What it does

- **Microsoft 365 and Outlook.com** through Graph, with delegated permissions
  only. The app can never read mail you could not read yourself.
- **Shared mailboxes**, found through Autodiscover the same way Outlook finds
  them rather than by probing the directory.
- **Live updates** over EWS streaming notifications, so new mail arrives without
  waiting for a poll.
- **The whole message** — real addresses, full headers, raw MIME, and every
  attachment including inline parts.
- **Local encrypted storage** (SQLCipher), one database per mailbox, with
  full-text search: `from:`, `to:`, `subject:`, `has:attachment` and friends.
- **Settings at four scopes** — folder, mailbox, account, application — with
  inheritance, so you can change one folder without touching anything else.
- **Light and dark themes**, with contrast verified by test rather than by eye.
- **Updates itself**, after showing you the version, size and hash and asking.

## Limitations

- **Windows x64 only.**
- **Microsoft accounts only.** No IMAP yet, so no Gmail or Fastmail.
- **The binary is unsigned**, so SmartScreen warns on first run. This also means
  auto-update replaces an unsigned executable: the digest check proves the bytes
  came from the GitHub release intact, but it cannot prove the release itself is
  trustworthy. Code signing is the real fix and is not done. If that trade-off
  does not suit you, turn the check off under Settings → Updates and install by
  hand.
- The Entra app registration is unverified, so consent shows an "unverified
  publisher" notice.

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build -c Release
dotnet test -c Release
```

To produce a release build — one self-contained executable that runs on a
machine with no .NET installed:

```powershell
dotnet publish src/Mail.App/Mail.App.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -o publish
```

## Layout

| Project | Purpose |
| --- | --- |
| `Mail.Core` | Domain logic with no I/O: MIME, compose, search parsing, themes, updates. |
| `Mail.Storage` | Encrypted SQLite storage and the settings system. |
| `Mail.Sync` | Graph, EWS, and authentication. |
| `Mail.App` | The WPF shell. |
| `tools/SyncSmoke` | A command-line harness for exercising sync against real accounts. |

[ARCHITECTURE.md](ARCHITECTURE.md) covers the design and the reasoning behind it.
