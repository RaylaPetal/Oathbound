<div align="center">

# ⛓️ Oathbound

**A consent-based Owner & Sub roleplay plugin for FINAL FANTASY XIV**

Titles · Outfits · Animations · Moodles · Restraints · Leash · Collar · Toys

[![Latest release](https://img.shields.io/github/v/release/RaylaPetal/xiv-collar?label=release&color=8a5cf6)](https://github.com/RaylaPetal/xiv-collar/releases/latest)
![Dalamud API](https://img.shields.io/badge/Dalamud%20API-15-6c63ff)
![Consent first](https://img.shields.io/badge/consent-first-e05d8c)

</div>

---

## 💜 What is Oathbound?

Oathbound lets two players share an Owner/Sub dynamic in game. The **Owner** sends commands to their
**Sub**: put on this outfit, wear this title, play this animation, follow me. The Sub's own plugin
applies them to the Sub's own character.

Everything is built around **consent**. Nothing can happen to a Sub unless their own plugin is running,
they've paired with that Owner, and they've turned on that kind of command. The Sub can always stop
everything instantly with their safeword.

> [!NOTE]
> Commands travel as ordinary in-game `/tell`s between the two of you. Changes to your look are applied
> through your own Glamourer and Penumbra, so anyone who sees your synced appearance (Snowcloak,
> Lightless, or a similar sync tool) sees them too.

---

## ✨ Features

| | Feature | What it does |
|:-:|---|---|
| 🏷️ | **Title** | Give your Sub a title (with prefix/suffix and color) through Honorific. |
| 👗 | **Outfit** | Dress your Sub in one of their Glamourer designs, locked in place or free to change. |
| 🎭 | **Animation** | Play emotes and poses from your Sub's Penumbra animation mods. |
| 😊 | **Moodles** | Add or clear status icons from your Sub's Moodles. |
| ⛓️ | **Restraints** | Put gear on your Sub that comes with rules - forced pose, walk only, blocked actions, gagged chat, or arm/leg/full-body cuffs held in an animation. Or send rules on their own, with no gear: forced pose, walk only, blocked actions or gagged. Lock them until you unlock them, or set a timer and they come off by themselves when it runs out (both of you need a version with timed locks). |
| 🔗 | **Follow / Leash** | Put your Sub on a leash of the length you choose. They move freely inside it, can't walk past its end, and get pulled along when you walk farther away. It stays on when you teleport or change area: your Sub is brought along to you. When you mount, your Sub rides pillion behind you (in a party, on a multi-seat mount) or mounts up and rides or flies with you. A leash line runs from their neck to your hand, slack or taut. |
| 🏷️ | **Status icons** | Small gagged, restrained and leashed icons next to your Sub's name. |
| 🔒 | **Collar** | A collar piece that goes on and locks when you pair, as a visible sign of your bond. It can carry a Moodle too. |
| 📳 | **Toy Control** | Control your Sub's connected toys through Intiface Central, plus optional triggers (low health, taking damage, being restrained, spells or emotes used on them). |
| 💫 | **Reactions** | Make your own character react when someone uses an emote on you or says a phrase after your trigger word: play a gesture, put on an item, turn on a mod, apply a moodle or reply in chat. Works for both roles, and only for the people you're paired with unless you allow anyone. |
| ⚡ | **Custom Triggers** | Bundle several actions behind one word: a title, an outfit and an animation all at once. |
| 🧭 | **Teleport** | Bring your Sub to your side with one click, from anywhere outdoors: they change world, teleport (or travel to your housing ward), then walk, ride or fly right up to you. |
| ↩️ | **Revert all** | One button in the Owner's header puts everything back to nothing. The collar and your pairing stay. |
| ⭐ | **Favorites & Sub Control** | Star the commands you use most, open them from the server info bar, or see every command in one panel. |
| ☁️ | **Automatic sync** | The Sub's list of outfits, animations, moodles and restraints reaches the Owner by itself, end-to-end encrypted. |
| 👥 | **Multiple pairings** | Own several Subs, belong to an Owner, or both at the same time. |

---

## 🤝 How it works

### 1. Pair up
Open **Settings** (`/oathboundsettings`) and press **Create pairing code**. Give the code to your partner
any way you like: Discord, `/say`, in person. They enter it in their own Settings, see who it's from, and
press **Accept**. The next time you're online you'll be asked to **Confirm** that it's really them, and
you're paired. You don't have to be online at the same time, and no tells are sent. A code works once and
lasts 7 days.

Pairing with someone on an older version? Open **Older version? Pair by tell** in the same place and use
the old invitation, where both of you need to be online.

**Your recovery code.** After your first pairing, Oathbound shows you a recovery code. Keep it somewhere
safe. If you reinstall the plugin or move to a new PC, enter it in **Settings > Recovery code > Restore** and
your pairings come back. Your partners don't need to do anything. Without the code, a lost install can't
be restored and you'd pair again.

### 2. The Sub sets up what they'll allow
In the main window (`/oathbound`), the Sub picks their designs, animations, moodles, restraints and collar,
and gives them short words (aliases). In **Settings > Permissions**, the Sub turns each kind of command on or off.
A command in a category that's switched off is simply ignored.

### 3. The Owner commands
The Owner's view shows ready-made **Quick Commands** built from the Sub's shared setup. Each one has a
**Send** button, and one click sends one tell. The Sub's list reaches the Owner automatically, and the
Sync tab shows whether it's up to date.

---

## 🛡️ Consent & safety

- **Nothing without the Sub's plugin.** No one can change your character from outside your own game. If
  you uninstall or disable the plugin, all of it stops.
- **Permissions per category.** Titles, outfits, animations, moodles, restraints, leash, collar, toys,
  teleport and custom chat each have their own switch, and you can change them at any time.
- **Extra steps for the heavier features.** Animations, leash, restraints and teleport need an extra
  acknowledgement before you can turn them on, and custom chat messages and toy control each need their
  own.
- **Your safeword.** `/oathboundpanic` instantly removes everything that's been applied to you:
  outfit, title, moodles, restraints, leash, animations and toys. Your collar is the one thing it leaves
  on: a locked collar (and its moodle) comes off only when the Owner who collared you sends **Collar
  unlock**, or when that pairing ends. If you were leashed, your plugin tells your Owner's that the leash
  came off because you used your safeword. You can also bind it to a hotkey. If you set a safeword, type it after the command (`/oathboundpanic red`); if you don't,
  the plain command always works. Panic doesn't end your pairing. Unpairing is a separate action in Settings, and it reaches your
  partner even if they're offline: their pairing ends the next time they log in, however long that takes.
- **Only the two of you see the icons and the leash line.** They're drawn by your own clients - nobody
  else sees them, and drawing them sends nothing. The Sub's own view is exact; the Owner's is an estimate
  built from the commands they sent. The leash is the exception: when it comes off on the Sub's side, the
  Sub's plugin tells the Owner's. For everything else the estimate can't see a panic or the Sub's own
  releases (Sub Control has a **Clear estimate** button for that). Either of you can turn them off in Settings -> In-world visuals.
- **Test before you pair.** Settings has a **Test an Owner command** box that lets a Sub try any command
  on themselves without sending anything.

---

## 📦 Requirements

**Oathbound itself:** [XIVLauncher](https://goatcorp.github.io/) with Dalamud.

**Other plugins it works with:**

| Plugin | Needed for | Who needs it |
|---|---|---|
| **Glamourer** | Outfits, restraint gear, collar | Sub · **required** |
| **Penumbra** | Animations, mod-based restraints | Sub · **required** |
| **Honorific** | Titles | Sub · for titles |
| **Moodles** | Status icons | Sub · optional |
| **Customize+** | Profile changes while gagged | Sub · optional |
| **Lifestream** | Teleport (world change, aetheryte and housing-ward travel) | Sub · for teleport |
| **vnavmesh** | Teleport (walking, riding or flying to the Owner) | Sub · for teleport |
| **Intiface Central** *(desktop app)* | Toy control | Sub · for toys |
| **A sync plugin** (Lightless, PlayerSync or Snowcloak - any one) | Letting other players see your changes | Sub · recommended |

> [!TIP]
> The Owner doesn't need any of these to send commands, Teleport included. Sending only needs Oathbound
> itself. **Settings** shows which of these Oathbound can see (green) or not (red) at the top of every
> tab (except the sync plugin, which Oathbound doesn't need to work), and the main window disables a
> Sub's module when the plugin it needs isn't detected.

---

## 🚀 Installation

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, add:
   ```
   https://raw.githubusercontent.com/RaylaPetal/xiv-collar/master/repo.json
   ```
3. Tick the checkbox next to it, then press **Save**.
4. Type `/xlplugins`, search for **Oathbound**, and install it.

A short tutorial walks you through the rest the first time you open the plugin.

---

## ⌨️ Commands

| Command | What it does |
|---|---|
| `/oathbound` or `/ob` | Open the main window |
| `/oathboundsettings` | Open Settings (pairing, role, safeword) |
| `/oathboundpanic [safeword]` | Your safeword: remove everything applied to you, right now |

The older `/collar`, `/collarsettings` and `/collarpanic` still work, so existing macros keep working.

---

## ⚠️ Please read: automation & game rules

Most of what Oathbound does only changes how your own character looks on your screen. A few features go
further, and you should decide for yourself before turning them on:

- **Animations** make your character perform emotes and poses.
- **Leash and some restraint rules** hold your movement or block your actions while they're active.
  When your Owner walks farther away than the leash length, the leash steers your character after them
  (or uses the game's own follow if that gets stuck), with your own movement ignored until you're back in range.
  If you're stuck running in place for 5 seconds, the leash goes slack: you get your own movement back to
  walk around whatever caught you, and it tightens again once you're back within its length (or tries
  pulling again after 15 seconds).
  If your Owner teleports or changes area while you're leashed, their plugin automatically tells yours where
  they went, and (if you've allowed **Teleport**) your character travels to them the same way Teleport does.
  If you haven't, or they go somewhere you can't follow (a house, an inn room, a duty), the leash comes off.
  When your Owner mounts, the leash also mounts you: it rides pillion behind them if you're in their party
  and their mount has a free seat, otherwise it summons a mount with Mount Roulette, takes off and lands with
  them, and dismounts when they do. That needs the automation acknowledgement. If you hop off yourself, it
  leaves you on foot until your Owner mounts again, and ending the leash never drops you out of the air.
- **Teleport** moves your character for you: it changes world, teleports, and then walks, rides or
  flies your character across the open world or a housing ward to your Owner, with your own movement
  locked until you arrive. Walking a character around automatically looks more like a bot than anything
  else here, so only turn it on if you're comfortable with that. If it gets stuck, press **Stop teleport**
  in the header (or use your safeword). It doesn't work from inside a house or apartment.
- **Gagged** changes chat messages you type into muffled text before they're sent.
- **Custom chat messages** in a Custom Trigger send text you wrote yourself, on the channel you chose.
- **Reactions** act on their own when someone uses an emote on you or says your trigger phrase. A
  reaction with a chat message **replies automatically**, in whatever channel you wrote it for, at most
  once every 10 seconds per reaction. That's the kind of automation plugin rules frown on most, so only
  add a chat reply if you're comfortable with it.

Every command the Owner sends is one deliberate click that sends one tell, with one exception: while a Sub
is leashed, the Owner's plugin sends that Sub one "leash travel" tell on its own each time the Owner changes
area, so the Sub can follow. The Sub's plugin has a matching one: when the Sub's leash comes off on their
side (their safeword, a trip that couldn't happen, the 2-minute wait running out, or a leash their plugin
couldn't put on), it sends the Owner one short "leash off" tell, so the Owner's plugin stops treating them as
leashed. It also sends that tell once in reply to a "leash travel" that reaches a Sub who isn't leashed. Apart
from those and a chat reply you set up yourself in Reactions, the plugin never auto-replies to chat. The only
other tells it sends on its own are ones tied directly to something you just did, like confirming a pairing
you accepted.

A Teleport command carries the Owner's world, zone, housing ward (if any) and position, so the Sub's
plugin knows where to go. It's sent only to your paired Sub, the same way as every other command.

Pairing and catalog sync go through a small Oathbound relay service. It only ever handles encrypted data
and never sees your catalog contents or your character's name.

**Using third-party plugins is against FINAL FANTASY XIV's Terms of Service. Use Oathbound at your own
risk.**

---

<details>
<summary><b>🛠️ For developers</b></summary>

<br>

The repository contains two projects:

- `Oathbound.Plugin/`: the Dalamud plugin (C#, .NET 10), shared by both roles.
- `worker/`: the Cloudflare Worker relay that handles pairing and encrypted catalog sync (TypeScript).

The wire format both sides agree on lives in `protocol/`.

```bash
dotnet build Oathbound.slnx        # plugin; output lands in bin/x64/Debug/
cd worker && npm test              # relay tests
```

To load a dev build, add the built `Oathbound.Plugin.dll` under `/xlsettings` → **Experimental** → Dev
Plugin Locations, then enable it in `/xlplugins`. See `CLAUDE.md` for the architecture and the release
process.

</details>

<div align="center">

<sub>All participation in this repository is governed by the [Dalamud Code of Conduct](https://dalamud.dev/code-of-conduct).
Contributors using AI tooling should review the [AI Usage Policy](https://dalamud.dev/plugin-publishing/ai-policy) and disclose it.</sub>

</div>
