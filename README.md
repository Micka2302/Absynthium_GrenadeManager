# Absynthium Grenade Manager

- Logs grenade friendly fire to admins, server log, and a file
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
- `LogFileName`: file name for grenade TK log (stored under `addons/counterstrikesharp/logs`)
- `DebugShowOwnTkToAttacker`: show TK messages to the attacker (debug)
- `DebugShowOwnTkToAttackerEvenIfAdmin`: show debug TK message even if attacker is admin
- `DebugIncludeSelfDamage`: allow self-damage to trigger debug message (debug)
- `FriendlyFireMessageCooldownSeconds`: cooldown to avoid repeated TK spam (0 to disable)
- `GrenadeRadioCvar`: cvar to disable grenade radio messages (default `sv_ignoregrenaderadio`)
- `GrenadeThrowMessageEnabled`: enable custom grenade throw messages
- `GrenadeThrowMessageScope`: where to send throw messages (`team`, `all`, `admins`, `attacker`)
- `GrenadeThrowMessageTemplate`: default grenade throw message template
- `GrenadeThrowMessageByGrenade`: optional per-grenade throw message overrides
- `FriendlyFireMessageTemplate`: default message template
- `FriendlyFireMessageByGrenade`: optional per-grenade template overrides
- `GrenadeDisplayNames`: display names for grenade types

Notes

- Bots are always ignored.
- Default grenade radio is always disabled via `GrenadeRadioCvar`.

Template placeholders

- `{attacker}`: attacker player name
- `{victim}`: victim player name
- `{hp}`: victim HP after the hit
- `{tk_count}`: grenade TK count for attacker (session)
- `{thrown_count}`: grenades thrown by attacker (session)
- `{grenade}`: display name (from `GrenadeDisplayNames`)
- `{grenade_raw}`: raw weapon key (ex: `hegrenade`)
- `{team}`: team name
- `{team_short}`: short team name (T/CT)
- `{place}`: last known place name
- `{steamid}`: attacker SteamID64

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
