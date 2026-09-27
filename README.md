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
| ⛓️ | **Restraints** | Put gear on your Sub that comes with rules - forced pose, walk only, blocked actions, gagged chat, or arm/leg/full-body cuffs held in an animation. Or send rules on their own, with no gear: forced pose, walk only, blocked actions or gagged. |
| 🔗 | **Follow / Leash** | Make your Sub follow you, with their own movement held until you let go. |
| 🔒 | **Collar** | A collar piece that goes on and locks when you pair, as a visible sign of your bond. It can carry a Moodle too. |
| 📳 | **Toy Control** | Control your Sub's connected toys through Intiface Central, plus optional triggers (low health, taking damage, being restrained, spells or emotes used on them). |
| 💫 | **Reactions** | Make your own character react when someone uses an emote on you or says a phrase after your trigger word: play a gesture, put on an item, turn on a mod, apply a moodle or reply in chat. Works for both roles, and only for the people you're paired with unless you allow anyone. |
| ⚡ | **Custom Triggers** | Bundle several actions behind one word: a title, an outfit and an animation all at once. |
| 🧭 | **Teleport** | Bring your Sub to your location with one click. |
| ↩️ | **Revert all** | One button in the Owner's header puts everything back to nothing. The collar and your pairing stay. |
| ⭐ | **Favorites & Sub Control** | Star the commands you use most, open them from the server info bar, or see every command in one panel. |
| ☁️ | **Automatic sync** | The Sub's list of outfits, animations, moodles and restraints reaches the Owner by itself, end-to-end encrypted. |
| 👥 | **Multiple pairings** | Own several Subs, belong to an Owner, or both at the same time. |

---

## 🤝 How it works

### 1. Pair up
Open **Settings** (`/oathboundsettings`), type your partner's `Name Surname@World`, choose whether
you're inviting them as your Sub or as your Owner, and press **Send Invitation**. Your partner gets a
request naming you, and pairing only happens if they click **Accept**.

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
- **Extra steps for the heavier features.** Animations, leash and restraints need an extra
  acknowledgement before you can turn them on, and custom chat messages and toy control each need their
  own.
- **Your safeword.** `/oathboundpanic` instantly removes everything that's been applied to you:
  outfit, collar, title, moodles, restraints, leash, animations and toys. You can also bind it to a
  hotkey. If you set a safeword, type it after the command (`/oathboundpanic red`); if you don't,
  the plain command always works. Panic doesn't end your pairing. Unpairing is a separate action in Settings.
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
| **Lifestream** | Teleport | Owner and Sub · for teleport |
| **Intiface Central** *(desktop app)* | Toy control | Sub · for toys |
| **Snowcloak / Lightless / similar** | Letting other players see your changes | Sub · recommended |

> [!TIP]
> The Owner doesn't need any of these to send commands. Sending only needs Oathbound itself (plus
> Lifestream if you want to use Teleport).

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
- **Gagged** changes chat messages you type into muffled text before they're sent.
- **Custom chat messages** in a Custom Trigger send text you wrote yourself, on the channel you chose.
- **Reactions** act on their own when someone uses an emote on you or says your trigger phrase. A
  reaction with a chat message **replies automatically**, in whatever channel you wrote it for, at most
  once every 10 seconds per reaction. That's the kind of automation plugin rules frown on most, so only
  add a chat reply if you're comfortable with it.

Every command the Owner sends is one deliberate click that sends one tell. Apart from a chat reply you
set up yourself in Reactions, the plugin never auto-replies to chat. The only tells it sends on its own
are ones tied directly to something you just did, like confirming a pairing you accepted.

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
