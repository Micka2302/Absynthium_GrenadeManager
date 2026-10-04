# Absynthium Grenade Manager

- Logs grenade friendly fire to admins and server log
- Tracks grenades thrown per player during the current session

Build

```
dotnet build -c Release
```

Install

Copy the built DLL to:
`addons/counterstrikesharp/plugins/Absynthium_GrenadeManager/`

Config is generated at:
`addons/counterstrikesharp/configs/plugins/Absynthium_GrenadeManager.json`

Config fields

- `AdminPermission`: CSS permission string required to see admin chat logs
- `DebugShowOwnTkToAttacker`: show TK messages to the attacker (debug)
- `DebugShowOwnTkToAttackerEvenIfAdmin`: show debug TK message even if attacker is admin
- `DebugIncludeSelfDamage`: allow self-damage to trigger debug message (debug)
- `DebugTKReverse`: allow TK reverse logic to include bots (DEV only)
- `DebugDamageTicks`: log per-tick reverse-damage diagnostics for fire grenades (`molotov`/`incgrenade`)
- `ReverseGrenadeFriendlyFireEnabled`: reverse grenade team damage to the thrower instead of teammates
- `MinTkToTkReserve`: number of previous grenade TKs allowed before reverse starts applying (`0` = reverse on first TK, `1` = reverse from second TK, etc.)
- `Language`: language code (English-only mode; defaults to `en`, any value falls back to `en`)
- `LanguageDirectory`: directory containing language files (default `lang`, relative to plugin folder)
- `GrenadeRadioCvar`: cvar to disable grenade radio messages (default `sv_ignoregrenaderadio`)
- `GrenadeThrowMessageEnabled`: enable custom grenade throw messages
- `GrenadeThrowMessageScope`: where to send throw messages (`team`, `all`, `admins`, `attacker`)

Language files

- On startup, the plugin auto-creates:
  - `<LanguageDirectory>/en.json`
- The language file is `<LanguageDirectory>/en.json`.
- If `LanguageDirectory` is relative, the plugin searches in this order:
  - configs plugin directory (`.../configs/plugins/Absynthium_GrenadeManager/<LanguageDirectory>`)
  - plugin directory (`.../plugins/Absynthium_GrenadeManager/<LanguageDirectory>`)
  - host base directory
- Message-related keys were removed from plugin config and are now only managed in language files.
- JSON keys:
  - `FriendlyFireMessageTemplate`
  - `FriendlyFireMessageByGrenade`
  - `FriendlyFireRepeatMessageTemplate`
  - `FriendlyFireRepeatMessageByGrenade`
  - `FriendlyFireVictimMessageTemplate`
  - `ReversePendingWarningTemplate`
  - `ReverseNextWarningTemplate`
  - `GrenadeThrowMessageTemplate`
  - `GrenadeThrowMessageByGrenade`
  - `ReverseDamageWarningTemplate`
  - `GrenadeDisplayNames`
- `GrenadeDisplayNames` can also be used as a direct message template per grenade if the value contains placeholders (for example `{attacker}`, `{victim}`, `{hp}`, `{damage}`, `{color:Red}`).
- For grenade throw chat specifically, `GrenadeDisplayNames[grenade]` is used first as the throw template when present.

Notes

- Bots are ignored by default. Set `DebugTKReverse=true` to include bots in TK reverse logic for DEV.
- Admin TK messages are deduplicated internally and sent once per incident (not configurable).
- Default grenade radio is always disabled via `GrenadeRadioCvar`.

Template placeholders

- `{attacker}`: attacker player name
- `{attacker_hp}`: attacker HP after reverse damage application
- `{victim}`: victim player name
- `{hp}`: victim HP after the hit
- `{tk_count}`: grenade TK count for attacker (session)
- `{thrown_count}`: grenades thrown by attacker (session)
- `{grenade}`: display name (from language file `GrenadeDisplayNames`)
- `{grenade_raw}`: raw weapon key (ex: `hegrenade`)
- `{team}`: team name
- `{team_short}`: short team name (T/CT)
- `{place}`: last known place name
- `{steamid}`: attacker SteamID64
- `{damage}`: reverse damage amount applied to attacker
- `{reverse_count}`: number of reverse TK incidents applied to attacker (`max(0, tk_count - MinTkToTkReserve)` when reverse is enabled)
- `{remaining_tk}`: remaining grenade TK incidents before TK reverse activation

Color tokens

Use `{color:NAME}` in any template. You can also use the legacy shorthand `{red}`, `{green}`, etc.

Examples:

- `{color:Team}`: team color of attacker
- `{color:Attacker}`: attacker team color
- `{color:Victim}`: victim team color
- `{color:Default}`: reset to default color
- `{color:Green}`, `{color:Red}`, `{color:Blue}`, etc. (all names from `ChatColors`)
- `{red}`, `{green}`, `{blue}`, `{default}`, etc. (legacy shorthand)
- `{color:FF0000}` or `{#FF0000}`: custom hex colors

Example:

`{color:Team}[{team_short}] {color:Default}{attacker} @{place} : {color:Green}{grenade}{color:Default} !`
