# Vouch

**English** · [中文](docs/README.zh-CN.md) · [Русский](docs/README.ru.md) · [日本語](docs/README.ja.md)

A modern, cross-platform **Steam authenticator** for the desktop — a from-scratch
reimplementation of the unmaintained
[Steam Desktop Authenticator](https://github.com/Jessecar96/SteamDesktopAuthenticator),
built on Avalonia with a clean multi-account UI.

Manage many Steam accounts, generate login codes, approve trade/market
confirmations, review and answer trade offers, browse and transfer inventory,
approve sign-ins, manage an account's logged-in devices, and link or unlink
authenticators — all from one app.

- **Stack:** Avalonia 12 (Fluent) · .NET 10 · CommunityToolkit.Mvvm · SteamKit2 for login + direct HTTP to Steam
- **Platforms:** Windows (primary), Linux, macOS
- **Languages:** English · 简体中文 · Русский · 日本語 (live switch)

## Screenshots

<p align="center">
  <img src="docs/screenshot/s1.png" width="46%">
  <img src="docs/screenshot/s2.png" width="46%">
  <img src="docs/screenshot/s3.png" width="46%">
  <img src="docs/screenshot/s4.png" width="46%">
</p>

---

## ⚠️ Security — read this first

**Your `.maFile` is your account.** It holds the 2FA seed (`shared_secret`), the
mobile-confirmation key (`identity_secret`), the revocation code, live session
tokens, and — in this app — your Steam **password**. Anyone who obtains a
decrypted maFile can fully take over the account.

**Only download Vouch from the official repository:**

> ### https://github.com/Monodesu/Vouch

Any other build — a "mirror", a re-upload, or a fork's release — **could be
backdoored to steal your maFiles**. If you did not get it from the link above,
assume it is malicious. Vouch never sends your secrets or password anywhere
except to Steam's own servers.

**Encrypt your files at rest:** turn on **Settings → Encrypt maFiles on disk**
and set a passkey. Every maFile (your password included) is then encrypted on
disk with **Argon2id** key derivation and **AES-256-GCM** — a wrong passkey or
a tampered file is detected, not silently mis-decrypted. You enter the passkey
once per launch. Encryption protects the files at
rest — it **cannot** protect you from running a tampered build, which is why
downloading only from the official repo matters just as much.

---

## Features

### Accounts
- Multi-account sidebar with **search**, **multi-select**, and **drag-to-reorder** (across groups too)
- **Groups**: organize accounts into collapsible groups (right-click → move to /
  new group), with a default group for the rest — order and collapsed state persist
- Live **TOTP login code** with a countdown ring; one-click copy
- Copy username / password; edit a stored password inline; free-text **per-account note** (kept in the maFile)
- Avatar, persona name, and Steam **level / bans** (VAC · game · trade)
- At-a-glance **session status** — signed in / expired / never signed in — shown as a sidebar stripe, an avatar ring, and a label

### Sign in
- Sign in with the stored password (prompts only when missing/wrong)
- **Re-Sign in** shown when a valid session still exists
- Email Steam Guard codes handled in a dedicated dialog
- **Batch sign-in / update-info** across a selection
- **Approve sign-ins from Vouch** — a login started elsewhere (no code entered) can be **approved or denied** here, mobile-app style
- **QR sign-in** — approve a Steam login QR read from the clipboard or a full-screen capture

### Devices
- **Logged-in devices**: list an account's active login sessions and **sign out** any one of them
- **Sign out of all devices** for one account or a whole selection, optionally signing Vouch back in afterward

### Confirmations & offers
- Trade/market **confirmations**: accept / deny, individually or in batch
- Background sweep of every signed-in account with a **system notification** on a
  new confirmation or incoming trade offer (de-duplicated, toggleable in Settings)
- **Trade offers**: view active offers, open a detail dialog with item **images**,
  both parties' avatar/name/SteamID, and the partner's level, join date, and
  friendship — then **accept / decline / cancel**
- **Notifications** list with mark-read / mark-all-read

### Inventory
- **Inventory viewer**: browse an account's items in-app — only the games that actually hold items (like Steam's own dropdown), with icons and counts
- **Transfer**: send an account's tradable items to a configured trade link — **all items** or a **hand-picked** selection (with images), one offer per game; presets (CS2, TF2, Dota 2, Rust, Steam) plus a **custom appid**; confirms the offer on mobile automatically

### Authenticator management
- **Add authenticator** wizard — works **without a phone number** (Steam emails the
  finalize code); revocation code is shown and re-confirmed in a separate dialog. Signs
  the new account in automatically once it's added
- **Deactivate authenticator** on Steam for a selection, using each account's stored
  revocation code (batch), then removes them from the app

### CS2 config sync
- Copy one account's **CS2 settings / key binds / video config** onto other accounts
  (all, a group, or hand-picked), with an automatic **backup** and one-click **restore**

### App
- Encrypted-at-rest maFiles (one passkey, entered once per launch)
- Light / dark theme, tray icon + minimize-to-tray, start-minimized
- Configurable check cadences, clipboard auto-clear, optional Web API key
- In-app **update check** against GitHub Releases

---

## Download

Grab the latest **`Vouch-vX.Y.Z-win-x64.exe`** from
[Releases](https://github.com/Monodesu/Vouch/releases). It is a single file
(framework-dependent) and needs the **[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)**
installed. Just run it — no installer.

## Build from source

Requires the **.NET 10 SDK**.

```bash
git clone https://github.com/Monodesu/Vouch.git
cd Vouch

dotnet run --project Vouch.App        # run
dotnet test Vouch.Core.Tests          # tests
```

Produce the single-file exe yourself:

```bash
dotnet publish Vouch.App/Vouch.App.csproj -c Release -r win-x64 \
  --self-contained false -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish
# (delete publish/*.pdb; use --self-contained true for a no-runtime-needed build)
```

## Project layout

| Path | What it is |
|------|-----------|
| `Vouch.App/` | Avalonia desktop app — views, view models, dialogs, assets |
| `Vouch.Core/` | Steam logic: TOTP, confirmations, offers, inventory, linking, storage — no UI |
| `Vouch.Core.Tests/` | xUnit tests for the pure/parsing logic |

## Where data lives

Everything is **portable** — it all lives next to the exe:

- `maFiles/` — your accounts (same on-disk layout as the original SDA; encrypted in place when you enable encryption)
- `settings.json` — app settings
- `cache/` — avatar cache

Override the location with the `VOUCH_DATA_DIR` environment variable.

## Updates

Vouch checks GitHub Releases for a newer tag on demand (Settings → Check for
updates) and links to the release page. Releases are produced automatically by CI
when a `v*` tag is pushed.

## License

Licensed under the [GNU Affero General Public License v3.0](LICENSE). Derivative
works — **including ones offered over a network** — must also be released as open
source under the AGPL.

Vouch is a derivative of the MIT-licensed original **Steam Desktop Authenticator**
by Jesse Cardone; that copyright notice is retained in [NOTICE](NOTICE).

## Acknowledgements

- [Steam Desktop Authenticator](https://github.com/Jessecar96/SteamDesktopAuthenticator) — the original, for the maFile format and the linking/confirmation flows it pioneered
- [Avalonia](https://avaloniaui.net/) — the cross-platform UI framework

## Dependencies

- [Avalonia](https://avaloniaui.net/) — cross-platform UI framework, Fluent theme + Inter font (MIT)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM source generators (MIT)
- [SteamKit2](https://github.com/SteamRE/SteamKit) — Steam login/auth handshake; a community library (SteamRE), **not** affiliated with Valve (LGPL-2.1)
- [Konscious.Security.Cryptography](https://github.com/kmaragon/Konscious.Security.Cryptography) — Argon2id key derivation for maFile encryption (MIT)
- [xUnit](https://xunit.net/) — unit tests (Apache-2.0)

The Steam authenticator core (TOTP, maFile format, authenticator linking, mobile confirmation) is a native C# port of [SteamAuth](https://github.com/geel9/SteamAuth) by Joshua Coffey / geel9 (MIT). Full third-party license texts are in [NOTICE](NOTICE).

---

> **Disclaimer:** Vouch is not affiliated with or endorsed by Valve. Steam is a
> trademark of Valve Corporation. Use at your own risk.
