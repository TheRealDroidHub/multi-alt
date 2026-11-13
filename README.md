<p align="center"><img src="logo.png" width="128" alt="Multi Alt logo"></p>

<h1 align="center">Multi Alt</h1>
<p align="center">by Triple DDD: a free, open-source Roblox multi-instance launcher and alt account manager for Windows.</p>

## What it does

- **Run many Roblox accounts at once** on one PC.
- **Home:** live previews of every running client. Click one to play it, then click the sidebar to come back.
- **Accounts:** profile picture, display name, @username and ID for each account, with these buttons:
  - Play
  - Open in browser
  - Copy
  - Remove
- **Drag accounts** to reorder them. Home shows the clients in the same order.
- **Performance:**
  - RAM cap in GB
  - FPS cap
  - lower priority and efficiency mode for background clients
  - optional Anti-AFK, which is off by default
- **Kill all Roblox** in one click.
- Roblox windows stay off your taskbar, and Multi Alt controls them from its Home tab.

### New in v1.2

- **No more blue update screen closing your alts.** Multi Alt now starts the current Roblox client straight from its install folder, so an old client never runs Roblox's updater while your alts are in game. If another launcher (Bloxstrap, Fishstrap...) handles Roblox links, launches go through it like before.
- **Keep Roblox updated** (Settings, off by default): when Roblox has a new version and no Roblox program is open, Multi Alt runs Roblox's own installer before launching. It never installs the same version twice, and skips it when Multi Alt runs as admin.
- **Remove old Roblox versions** (Settings, off by default, needs Keep Roblox updated): deletes the old player folders Roblox leaves in `%LOCALAPPDATA%\Roblox\Versions`. It keeps the current version, Studio and the folders Roblox's links and uninstaller use.
- **Clean up leftover crash handlers** (Settings, off by default): closes RobloxCrashHandler programs whose client already closed, so they don't hold up a Roblox update.
- **FPS cap** is applied again before every launch and after a Roblox update, so a new Roblox version never runs uncapped.
- **RAM cap:** background clients over the cap get their memory trimmed at most every 30 seconds, never the client you're playing.
- **Tray icon** next to the clock: Open, Launch all, Mute Roblox, Kill all Roblox and Exit. **Close to tray** (Settings, off by default) keeps Multi Alt running when you close its window.
- **Show in Start Menu** (Settings, off by default), so Windows search finds Multi Alt.
- **Profile pictures** refresh every 8 hours. Removing an account also deletes its picture and its Open in browser login.
- **Runs at below-normal priority**, so your games come first. Roblox still starts at normal priority.
- **One Multi Alt at a time:** opening it again brings the running one to the front, so two copies can't overwrite each other's accounts.
- A warning when Multi Alt runs as admin, which can cause duplicate Roblox installs.
- **Updated Terms of Service.** Multi Alt shows them the first time it opens, and again whenever they change, and only opens after you press I agree.
- **Fixes:** burning all data can't be undone by a save that was still queued, and "Close this client" on Home can't close another program that got the same PID.

### New in v1.1

- **PID** next to each client's @username on Home and on running account cards.
- **CPU %** for all of Roblox at the top of Home, and per client on each tile above its RAM.
- **Private server links** work in the Game link box (links with `privateServerLinkCode`).
- **Launch-all checkbox** on each account card. Unticked accounts are skipped by Launch all and Follow/Join; Play still works.
- **Follow/Join:** type a username or user ID and every ticked account joins the game that player is in.
- **Auto-rejoin** (off by default): relaunches a client that closes by itself, max 2 times per account in 8 minutes, then it stops for that account.
- **Mute Roblox** (Settings → Audio): hard-mutes every client in the Windows volume mixer.
- **Update check:** a gold "Update available" button appears when a new release is out. It only opens the Releases page and never downloads anything. Can be turned off in Settings.
- **Remove Multi Alt** (bottom of Settings): delete the app and keep your data, or delete it and burn all saved data (type BURN to confirm).

## Download

Get `Multi Alt.exe` from the [Releases](../../releases) page. It's a single file and doesn't need to be installed.

> Windows Defender or SmartScreen may warn about it. That happens because the exe isn't code-signed, not because anything is wrong with it. You can read the whole source here, or build it yourself (see below).

## Build it yourself

You don't need Visual Studio. The C# compiler that comes with Windows is enough:

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:logo.ico /resource:logo.png,logo.png ^
  /r:System.Security.dll /r:System.Web.Extensions.dll /r:Microsoft.VisualBasic.dll /out:MultiAlt.exe MultiAlt.cs
```

To run the built-in checks, build with `/target:exe` instead and run `MultiAlt.exe --selftest`.

## Your data and safety

- Account logins (and saved passwords, if you add any) are encrypted with Windows DPAPI in `%APPDATA%\MultiAlt`. Only your Windows user on your PC can read them.
- Logins are only ever sent to roblox.com. Nothing is uploaded anywhere else. The optional update check only asks api.github.com for the latest version number. To start the current Roblox version, Multi Alt asks Roblox (clientsettingscdn.roblox.com) for the current version number, with no login attached.
- Copied combos and cookies are excluded from Windows clipboard history and cloud clipboard sync.
- Multi Alt doesn't inject code into Roblox and doesn't read or change its memory. It starts the official Roblox client on your PC with the same launch link the Roblox website uses.

## Disclaimer

By downloading, opening or using Multi Alt in any way, you agree to its Terms of Service. If you don't agree, don't use it.

- Use it at your own risk. It's provided as is, with no warranty.
- The developer isn't responsible for bans, flags, rate limits or any other action Roblox or a game takes on your accounts, for any reason.
- The developer isn't responsible for problems with your files, settings or Roblox install, including Roblox updates, deleted version folders or having to reinstall Roblox. There's no promise that the Roblox version Multi Alt starts or installs is the newest or best one, or that features keep working when Roblox changes.
- Multi Alt is not affiliated with Roblox Corporation, GitHub, Microsoft, Valve/Steam or any other company.

The full terms are in the app under **Terms of Service**.

## License

[MIT](LICENSE)
