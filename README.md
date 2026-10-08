<div align="center">

# ⛓️ Oathbound

**A consent-based Owner & Sub roleplay plugin for FINAL FANTASY XIV**

Titles · Outfits · Animations · Moodles · Restraints · Leash · Collar · Toys

[![Latest release](https://img.shields.io/github/v/release/RaylaPetal/Oathbound?label=release&color=8a5cf6)](https://github.com/RaylaPetal/Oathbound/releases/latest)
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
| 🏷️ | **Title** | Give your Sub a title (with prefix/suffix and color) through Honorific. It stays locked - their own titles can't replace it, and it comes back after a relog - until you clear it, or for a time you set. |
| 👗 | **Outfit** | Dress your Sub in one of their Glamourer designs, locked in place or free to change. Lock it for a set time and it comes off by itself when the time is up. |
| 🎭 | **Animation** | Play emotes and poses from your Sub's Penumbra animation mods. Your Sub is held in the pose until you press **Stop animation**. |
| 😊 | **Moodles** | Add or clear status icons from your Sub's Moodles, or write your own - title, description, icon and duration - and put it on them without them making it first. Your own moodles work in rulebooks and Custom Triggers too. A moodle you put on stays locked - put back if it's taken off - until you remove it, or for a time you set. |
| ⛓️ | **Restraints** | Put gear on your Sub that comes with rules - forced pose, walk only, blocked actions, gagged chat, or arm/leg/full-body cuffs held in an animation. Or send rules on their own, with no gear: forced pose, walk only, blocked actions, gagged, or arm/leg/full-body cuffs drawn on their wrists and ankles with chains between them (gear restraints can draw them too, rule by rule). Each restraint has its own lock: until you unlock it, or a timer after which it comes off by itself, and they stay on through a relog. Give a lock a **key** - a password your Sub can type to unlock it (you're told if they do). A gag can be Light, Medium or Heavy, and you can let your Sub **struggle** against a lock - Easy, Medium or Hard - for a chance to get free (you're told if they do). Mod restraints can have a picture - a file of your choosing, or a snapshot you frame on the game screen. |
| 🔗 | **Follow / Leash** | Put your Sub on a leash of the length you choose. They move freely inside it, can't walk past its end, and get pulled along when you walk farther away. It stays on until you unleash them: when you teleport or change area your Sub is brought along to you, and anywhere they can't follow (your house, an inn room, a duty) it just pauses until you're together again. When you mount, your Sub rides pillion behind you (in a party, on a multi-seat mount) or mounts up and rides or flies with you. A leash line runs from their neck to your hand, slack or taut. |
| 🏷️ | **Status icons** | Small gagged, restrained and leashed icons next to your Sub's name. |
| 🔒 | **Collar** | A collar piece, a left-hand ring, or both, that go on and lock together when you pair, as a visible sign of your bond. It can carry a Moodle too. If it ever comes off without you unlocking it, you see a red warning in your header - even if it happened while you were offline. |
| 📳 | **Toy Control** | Control your Sub's connected toys through Intiface Central, plus optional triggers (low health, taking damage, being restrained, spells or emotes used on them). |
| 💫 | **Reactions** | Make your own character react when someone uses an emote on you or says a phrase after your trigger word: play a gesture, put on an item, turn on a mod, apply a moodle or reply in chat. Works for both roles, and only for the people you're paired with unless you allow anyone. |
| 📖 | **Rulebook** | Rules that keep running while the Owner is away. **Oaths** the Sub swears to: daily rituals (greet the Owner with a gesture, message them, check in), manners (say goodnight before logging off, call the Owner by a title, a forbidden word, staying quiet in public), places and a curfew, or duty oaths (no deaths, no wipes, a time limit), with something for keeping and something for breaking each one. A **deck** of reward and punishment cards: bad things draw a punishment, a kept oath draws a reward, or the Owner draws one by hand. **Place** rules for entering or leaving a city, a house or a duty. **Presence** rules for when the Owner comes near. And a **ledger** score with thresholds. Daily rituals can score the ledger each time they're done or missed, and are judged as kept or broken when the oath ends. More oaths: complete a number of duties, play only certain jobs, keep on the outfit you put on them, stay at your side, and ask before logging off. An oath can allow a few strikes before it breaks, pay a streak bonus, and start again by itself or be offered again when it ends. A **schedule** runs something at a time of day or at login, and a **shop** lets the Sub spend ledger points on a reward card or something you set. Every reward or punishment that puts on a title, outfit, moodle or restraint lasts as long as you choose, locked until it comes off by itself or you end it. The Sub accepts every version and every oath before anything runs; after that only the Owner can change or remove a rule, and the Owner sees what happened. |
| ⚡ | **Custom Triggers** | Bundle several actions behind one word: a title, an outfit and an animation all at once. |
| 🧭 | **Teleport** | Bring your Sub to your side with one click, from anywhere outdoors: they change world, teleport (or travel to your housing ward), then walk, ride or fly right up to you. |
| ↩️ | **Revert all** | One button in the Owner's header puts everything back to nothing. The collar and your pairing stay. |
| ⭐ | **Favorites & Sub Control** | Star the commands you use most, open them from the server info bar, or see every command in one panel. |
| ☁️ | **Automatic sync** | The Sub's list of outfits, animations, moodles and restraints reaches the Owner by itself, end-to-end encrypted. |
| 👥 | **Multiple pairings** | Own several Subs, belong to an Owner, or both at the same time. |

---

## 🤝 How it works

### 1. Pair up
Open **Settings** (`/obsettings`) and press **Create pairing code**. Give the code to your partner
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
In the main window (`/ob`), the Sub picks their designs, animations, moodles, restraints and collar,
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
  Moodles your Owner writes themselves need one more switch under Moodles, **Allow moodles my Owner writes**,
  because the text is theirs and other players can see moodles through sync tools. Moodles itself must also let
  other plugins apply moodles and allow your Owner (friends, party members or everyone) in its own settings.
- **Extra steps for the heavier features.** Animations, leash, restraints and teleport need an extra
  acknowledgement before you can turn them on, and custom chat messages and toy control each need their
  own.
- **Your safeword.** `/obpanic` instantly removes everything that's been applied to you:
  outfit, title, moodles, restraints, leash, animations and toys. Your collar is the one thing it leaves
  on: a locked collar (and its moodle) comes off only when the Owner who collared you sends **Collar
  unlock**, or when that pairing ends. If you were leashed, your plugin tells your Owner's that the leash
  came off because you used your safeword. You can also bind it to a hotkey. If you set a safeword, type it after the command (`/obpanic red`); if you don't,
  the plain command always works. Panic doesn't end your pairing. Unpairing is a separate action in Settings, and it reaches your
  partner even if they're offline: their pairing ends the next time they log in, however long that takes.
- **Only the two of you see the icons, the leash line and drawn cuffs.** They're drawn by your own clients - nobody
  else sees them, and drawing them sends nothing. The Sub's own view is exact; the Owner's is an estimate
  built from the commands they sent. The leash is the exception: when it comes off on the Sub's side, the
  Sub's plugin tells the Owner's. So is the collar: while one is locked, the Sub's plugin tells the relay whether
  it's really on, and the Owner is warned if it comes off. For everything else the estimate can't see a panic or the Sub's own
  releases (Sub Control has a **Clear estimate** button for that). The leash line's look (or hiding it) is set in
  the Follow / Leash module, and the drawn cuffs' look in the Restraints module.
- **Rulebooks run only after you say yes.** A rulebook your Owner writes reaches you encrypted, and nothing in
  it runs until you accept that version; each new oath needs its own yes too. Accepting a version that changes
  or removes an oath you already swore switches that oath to the new terms or ends it, and the review tells you
  first. Your Owner can also send a version that starts over, which clears your oaths, ledger and history only
  once you accept it. Accepting is your consent to every rule in that version: after that you can't switch single rules
  off, only your Owner can change or remove them. It needs its own permission and acknowledgement,
  every consequence still needs the permission for its category, and a rulebook can never teleport you or
  touch your collar. A recurring oath starts again (or is offered again) after each run, and swearing to it is
  your consent to that too; you can stop it renewing at any time, and the current run still counts. Only you can
  buy from your Owner's shop - no command or rule ever spends your points. Only a presence rule can leash you, take the leash off, or have you /tell your Owner a
  message, and only with your Follow or Custom chat messages permission on. Your safeword pauses all rulebooks and voids open oaths until you
  resume.
- **Test before you pair.** Settings' **Test Commands** tab lets a Sub try any command
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
   https://raw.githubusercontent.com/RaylaPetal/Oathbound/master/repo.json
   ```
3. Tick the checkbox next to it, then press **Save**.
4. Type `/xlplugins`, search for **Oathbound**, and install it.

A guided tour walks you through the rest the first time you open the plugin, highlighting each part as it goes.
Every module also has its own tour behind the **?** in its title bar, and you can rerun the overview from Settings.

---

## ⌨️ Commands

| Command | What it does |
|---|---|
| `/ob` | Open the main window |
| `/obsettings` | Open Settings (pairing, role, safeword) |
| `/obpanic [safeword]` | Your safeword: remove everything applied to you, right now |

The long names (`/oathbound`, `/oathboundsettings`, `/oathboundpanic`) still work, so existing macros and keybinds
keep working. The old `/collar` commands are gone - update any macro that still uses them.

---

## ⚠️ Please read: automation & game rules

Most of what Oathbound does only changes how your own character looks on your screen. A few features go
further, and you should decide for yourself before turning them on:

- **Animations** make your character perform emotes and poses, and hold you in place while they play. A
  one-off emote (like a wave or a bow) lets you go as soon as it finishes. A looping emote or a pose holds you
  until your Owner stops the animation or sends revert all, or you use your safeword. Stopping a looping emote
  makes your character take one tiny step, because moving is the only way the game ends one. Your Owner can
  also send an animation with a set time: it holds you for exactly that long, then stops by itself. A greeting
  oath's **Perform** button plays its gesture or modded animation at your Owner and holds you the same way. To
  try one on yourself from Settings' Test Commands tab, type `gesture stop` there to release it again.
- **Leash and some restraint rules** hold your movement or block your actions while they're active.
  When your Owner walks farther away than the leash length, the leash steers your character after them
  (or uses the game's own follow if that gets stuck), with your own movement ignored until you're back in range.
  If you're stuck running in place for 5 seconds, the leash goes slack: you get your own movement back to
  walk around whatever caught you, and it tightens again once you're back within its length (or tries
  pulling again after 15 seconds).
  If your Owner teleports or changes area while you're leashed, their plugin automatically tells yours where
  they went, and (if you've allowed **Teleport**) your character travels to them the same way Teleport does.
  If you haven't, or they go somewhere you can't follow (a house, an inn room, a duty), the leash pauses
  instead: you get full control back, and it picks up again as soon as you're in the same place. A trip you
  couldn't take (in combat or a duty, for example) is tried again on your next area change or once combat
  ends; one you stopped yourself isn't. Only your Owner's unleash, your safeword, or unpairing ends the leash.
  When your Owner mounts, the leash also mounts you: it rides pillion behind them if you're in their party
  and their mount has a free seat, otherwise it summons a mount with Mount Roulette, takes off and lands with
  them, and dismounts when they do. That needs the automation acknowledgement. If you hop off yourself, it
  leaves you on foot until your Owner mounts again, and ending the leash never drops you out of the air.
- **Teleport** moves your character for you: it changes world, teleports, and then walks, rides or
  flies your character across the open world or a housing ward to your Owner, with your own movement
  locked until you arrive. Walking a character around automatically looks more like a bot than anything
  else here, so only turn it on if you're comfortable with that. If it gets stuck, press **Stop teleport**
  in the header (or use your safeword). It doesn't work from inside a house or apartment.
- **Gagged** changes chat messages you type into muffled text before they're sent - every word at Heavy,
  muffled words that keep their first letter and length at Medium, and about one longer word in three at Light.
- **Custom chat messages** in a Custom Trigger send text you wrote yourself, on the channel you chose.
- **Rulebook** consequences happen on their own when something in your game matches a rule you accepted:
  breaking or keeping an oath, a wipe, dying, entering or leaving a place, your Owner coming near, a card
  being drawn or the ledger crossing a threshold. Each one runs the same commands your Owner could send you
  (title, outfit, animation, moodle, restraint, toy, revert all), never teleport or the collar. A **presence**
  rule can also leash you to your Owner when they arrive (your client then sends them a short `collarleash on`
  tell so the leash follows them across areas), take it off when they leave, and **send your Owner a /tell**
  they wrote, by itself, when they arrive or leave. Leashing needs your Follow permission and the message needs
  Custom chat messages; with either off, that part is skipped.
  A **schedule** rule runs at the time of day your Owner set, or each time you log in. Anything other than toys
  and moodles waits until you're out of combat and cutscenes, and each rule has a cooldown of at least a minute.
- **Reactions** act on their own when someone uses an emote on you or says your trigger phrase. A
  reaction with a chat message **replies automatically**, in whatever channel you wrote it for, at most
  once every 10 seconds per reaction. That's the kind of automation plugin rules frown on most, so only
  add a chat reply if you're comfortable with it.

Every command the Owner sends is one deliberate click that sends one tell, with one exception: while a Sub
is leashed, the Owner's plugin sends that Sub one "leash travel" tell on its own each time the Owner changes
area, so the Sub can follow. The Sub's plugin has a matching one: when the Sub's leash comes off on their
side (their safeword, or a leash their plugin couldn't put on), it sends the Owner one short "leash off" tell, so the Owner's plugin stops treating them as
leashed. It also sends that tell once in reply to a "leash travel" that reaches a Sub who isn't leashed. Apart
from those and a chat reply you set up yourself in Reactions, the plugin never auto-replies to chat. The only
other tells it sends on its own are ones tied directly to something you just did, like confirming a pairing
you accepted, or the optional "collarrulebook" tell when an Owner publishes a rulebook with **Tell them now**
ticked, which only asks the Sub's plugin to pick it up right away. If you struggle free of a restraint lock, your
plugin sends the Owner who set it one short "collarstruggle free" tell naming that restraint, and if you unlock one with
a key your Owner gave you, one "collarstruggle key" tell naming it; failed tries and wrong keys send nothing. An Owner's
**Grant leave** button (for an "ask before logging off" oath) sends one tell with the word they chose, only when
they press it.

A Teleport command carries the Owner's world, zone, housing ward (if any) and position, so the Sub's
plugin knows where to go. It's sent only to your paired Sub, the same way as every other command.

Pairing, catalog sync, rulebooks and the collar warning go through a small Oathbound relay service. Your
catalog, the Owner's rulebook and the Sub's rulebook activity report are end-to-end encrypted, and the relay
never sees their contents or your character's name. While a Sub has a rulebook, their plugin uploads that
encrypted activity report by itself, at most every 10 minutes and only when something changed. The relay also counts how many installs
checked in during the last half hour, as one anonymous number shown in the main window's header. It does keep,
under an anonymous id per pairing, whether that pairing has ended and whether a locked collar is still on.

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
