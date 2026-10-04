using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace AbsynthiumGrenadeManager;

public sealed class GrenadeManagerConfig : BasePluginConfig
{
    public override int Version { get; set; } = 9;

    public string AdminPermission { get; set; } = "@css/ban";

    public bool DebugShowOwnTkToAttacker { get; set; } = false;
    public bool DebugShowOwnTkToAttackerEvenIfAdmin { get; set; } = false;
    public bool DebugIncludeSelfDamage { get; set; } = false;
    public bool DebugTKReverse { get; set; } = false;
    public bool DebugDamageTicks { get; set; } = false;

    public bool ReverseGrenadeFriendlyFireEnabled { get; set; } = false;
    public int MinTkToTkReserve { get; set; } = 0;
    public string Language { get; set; } = "en";
    public string LanguageDirectory { get; set; } = "lang";

    public string GrenadeRadioCvar { get; set; } = "sv_ignoregrenaderadio";

    public bool GrenadeThrowMessageEnabled { get; set; } = false;
    public string GrenadeThrowMessageScope { get; set; } = "team";
}

public sealed class GrenadeManagerLanguage
{
    public string FriendlyFireMessageTemplate { get; set; } = string.Empty;
    public Dictionary<string, string> FriendlyFireMessageByGrenade { get; set; } = new();
    public string FriendlyFireRepeatMessageTemplate { get; set; } = string.Empty;
    public Dictionary<string, string> FriendlyFireRepeatMessageByGrenade { get; set; } = new();
    public string FriendlyFireVictimMessageTemplate { get; set; } = string.Empty;
    public string ReversePendingWarningTemplate { get; set; } = string.Empty;
    public string ReverseNextWarningTemplate { get; set; } = string.Empty;

    public string GrenadeThrowMessageTemplate { get; set; } = string.Empty;
    public Dictionary<string, string> GrenadeThrowMessageByGrenade { get; set; } = new();

    public string ReverseDamageWarningTemplate { get; set; } = string.Empty;

    public Dictionary<string, string> GrenadeDisplayNames { get; set; } = new();
}

public sealed class AbsynthiumGrenadeManager : BasePlugin, IPluginConfig<GrenadeManagerConfig>
{
    public override string ModuleName => "Absynthium_GrenadeManager";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Micka";
    public override string ModuleDescription => "Admin log for grenade friendly fire and per-player grenade counters.";

    public GrenadeManagerConfig Config { get; set; } = new();

    private readonly Dictionary<ulong, int> _grenadesThrown = new();
    private readonly Dictionary<ulong, int> _grenadeTkCount = new();
    private readonly Dictionary<string, DateTime> _lastFriendlyFireMessage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VictimStateSnapshot> _pendingVictimStateRestore = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FriendlyFireAttributionSnapshot> _recentFriendlyFireAttribution = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FriendlyFireNoLossBaselineSnapshot> _friendlyFireNoLossBaselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ulong> _infernoOwnerByEntityIndex = new();
    private GrenadeManagerLanguage _language = new();
    private const double ContinuousGrenadeIncidentWindowSeconds = 8d;
    private const double FriendlyFireMessageDedupSeconds = 1.25d;
    private const double ReverseWarningDedupSeconds = 5d;
    private const double VictimStateSnapshotWindowSeconds = 3d;
    private const double FriendlyFireAttributionWindowSeconds = 8d;
    private const double FriendlyFireNoLossBaselineWindowSeconds = 10d;
    private const int FriendlyFireNoLossMaxHpCompensation = 6;
    private const int FriendlyFireNoLossMaxArmorCompensation = 12;

    private static readonly string[] ControllerOwnerPropertyCandidates =
    {
        "Thrower",
        "OriginalThrower",
        "OwnerEntity",
        "Owner",
        "OwnerPawn",
        "OwnerController",
        "ThrowerEntity",
        "ThrowerPawn",
        "ThrowerController",
        "Creator",
        "Attacker",
        "WeaponOwner",
        "Parent"
    };

    private static readonly string[] ControllerOwnerPropertyNameHints =
    {
        "owner",
        "thrower",
        "attacker",
        "creator",
        "instigator"
    };

    private static readonly Regex ColorTagRegex = new(@"\{/?[#a-z0-9_]+\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string ColorChar(char value) => value.ToString();

    private static readonly Func<CCSPlayerController?, string> DefaultColorResolver = _ => ColorChar(ChatColors.Default);

    private static readonly IReadOnlyDictionary<string, Func<CCSPlayerController?, string>> ColorTagResolvers =
        new Dictionary<string, Func<CCSPlayerController?, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = DefaultColorResolver,
            ["reset"] = DefaultColorResolver,
            ["white"] = _ => ColorChar(ChatColors.White),
            ["green"] = _ => ColorChar(ChatColors.Green),
            ["lightgreen"] = _ => ColorChar(ChatColors.Lime),
            ["lightyellow"] = _ => ColorChar(ChatColors.LightYellow),
            ["yellow"] = _ => ColorChar(ChatColors.Yellow),
            ["lightblue"] = _ => ColorChar(ChatColors.LightBlue),
            ["blue"] = _ => ColorChar(ChatColors.Blue),
            ["darkblue"] = _ => ColorChar(ChatColors.DarkBlue),
            ["olive"] = _ => ColorChar(ChatColors.Olive),
            ["lime"] = _ => ColorChar(ChatColors.Lime),
            ["red"] = _ => ColorChar(ChatColors.Red),
            ["lightred"] = _ => ColorChar(ChatColors.LightRed),
            ["darkred"] = _ => ColorChar(ChatColors.DarkRed),
            ["lightpurple"] = _ => ColorChar(ChatColors.LightPurple),
            ["purple"] = _ => ColorChar(ChatColors.Purple),
            ["magenta"] = _ => ColorChar(ChatColors.Magenta),
            ["grey"] = _ => ColorChar(ChatColors.Grey),
            ["gray"] = _ => ColorChar(ChatColors.Grey),
            ["gold"] = _ => ColorChar(ChatColors.Gold),
            ["silver"] = _ => ColorChar(ChatColors.Silver),
            ["orange"] = _ => ColorChar(ChatColors.Orange),
            ["team"] = player => ColorChar(player is not null ? ChatColors.ForPlayer(player) : ChatColors.Default),
            ["attacker"] = player => ColorChar(player is not null ? ChatColors.ForPlayer(player) : ChatColors.Default),
            ["ct"] = _ => ColorChar(ChatColors.Blue),
            ["counterterrorist"] = _ => ColorChar(ChatColors.Blue),
            ["t"] = _ => ColorChar(ChatColors.Yellow),
            ["terrorist"] = _ => ColorChar(ChatColors.Yellow),
            ["spec"] = _ => ColorChar(ChatColors.LightPurple),
            ["spectator"] = _ => ColorChar(ChatColors.LightPurple),
        };

    private static readonly HashSet<string> GrenadeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "hegrenade",
        "flashbang",
        "smokegrenade",
        "molotov",
        "incgrenade",
        "decoy"
    };

    private static readonly HashSet<string> DamageGrenadeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "hegrenade",
        "molotov",
        "incgrenade",
        "decoy"
    };

    private static readonly string[] TemplateMarkers =
    {
        "{attacker}",
        "{attacker_hp}",
        "{victim}",
        "{hp}",
        "{tk_count}",
        "{thrown_count}",
        "{grenade}",
        "{grenade_raw}",
        "{team}",
        "{team_short}",
        "{place}",
        "{steamid}",
        "{damage}",
        "{reverse_count}",
        "{remaining_tk}",
        "{color:",
        "{#"
    };

    private sealed class VictimStateSnapshot
    {
        public int Health { get; init; }
        public int Armor { get; init; }
        public DateTime TimestampUtc { get; init; }
    }

    private sealed class FriendlyFireAttributionSnapshot
    {
        public ulong AttackerSteamId { get; init; }
        public DateTime TimestampUtc { get; init; }
    }

    private sealed class FriendlyFireNoLossBaselineSnapshot
    {
        public int Health { get; init; }
        public int Armor { get; init; }
        public DateTime TimestampUtc { get; init; }
    }

    private static readonly string[] DamageInfoZeroPropertyCandidates =
    {
        "OriginalDamage",
        "DamageArmor",
        "DmgArmor",
        "ArmorDamage",
        "OriginalDamageArmor",
        "TotalledDamageArmor"
    };

    private static readonly string[] ReverseDamagePropertyCandidates =
    {
        "Damage",
        "TotalledDamage",
        "OriginalDamage",
        "BaseDamage",
        "DamageNoReduction",
        "UnreducedDamage"
    };

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventInfernoStartburn>(OnInfernoStartBurn);
        RegisterEventHandler<EventInfernoExpire>(OnInfernoExpire);
        RegisterEventHandler<EventInfernoExtinguish>(OnInfernoExtinguish);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnPlayerTakeDamagePre);
        RegisterListener<Listeners.OnPlayerTakeDamagePost>(OnPlayerTakeDamagePost);
        ScheduleGrenadeRadioDisable();
    }

    public void OnConfigParsed(GrenadeManagerConfig config)
    {
        Config = config ?? new GrenadeManagerConfig();
        LoadLanguageFile();
        ScheduleGrenadeRadioDisable();
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !IsValidPlayer(player))
        {
            return HookResult.Continue;
        }

        // Inferno ticks can trigger weapon_fire repeatedly; they are not real throws.
        var rawWeaponKey = NormalizeWeaponKeyRaw(@event.Weapon);
        if (rawWeaponKey == "inferno")
        {
            return HookResult.Continue;
        }

        var grenadeKey = NormalizeGrenadeKey(@event.Weapon);
        if (string.IsNullOrWhiteSpace(grenadeKey) || !GrenadeKeys.Contains(grenadeKey))
        {
            return HookResult.Continue;
        }

        if (!ShouldRegisterGrenadeThrow(player.SteamID, grenadeKey))
        {
            return HookResult.Continue;
        }

        var thrownCount = IncrementCount(_grenadesThrown, player.SteamID);

        if (Config.GrenadeThrowMessageEnabled)
        {
            var grenadeDisplay = GetGrenadeDisplay(grenadeKey);
            var template = GetGrenadeThrowTemplate(grenadeKey);
            var message = ApplyTemplate(
                template,
                player,
                null,
                grenadeDisplay,
                grenadeKey,
                GetPlayerHealth(player),
                GetCount(_grenadeTkCount, player.SteamID),
                thrownCount);

            SendGrenadeThrowMessage(player, message);
        }

        return HookResult.Continue;
    }

    private HookResult OnInfernoStartBurn(EventInfernoStartburn @event, GameEventInfo info)
    {
        if (@event.Entityid <= 0)
        {
            return HookResult.Continue;
        }

        var inferno = Utilities.GetEntityFromIndex<CInferno>(@event.Entityid);
        if (inferno == null || !inferno.IsValid)
        {
            return HookResult.Continue;
        }

        var owner = TryResolveController(inferno.OwnerEntity.Value);
        if (owner == null || !owner.IsValid)
        {
            owner = TryResolveController(inferno);
        }

        if (owner == null || !owner.IsValid)
        {
            return HookResult.Continue;
        }

        _infernoOwnerByEntityIndex[@event.Entityid] = owner.SteamID;
        if (Config.DebugDamageTicks)
        {
            Logger.LogInformation(
                "Inferno owner mapped: entity={Entity} owner={Owner} steamid={SteamId}",
                @event.Entityid,
                owner.PlayerName,
                owner.SteamID);
        }

        return HookResult.Continue;
    }

    private HookResult OnInfernoExpire(EventInfernoExpire @event, GameEventInfo info)
    {
        if (@event.Entityid > 0)
        {
            _infernoOwnerByEntityIndex.Remove(@event.Entityid);
        }

        return HookResult.Continue;
    }

    private HookResult OnInfernoExtinguish(EventInfernoExtinguish @event, GameEventInfo info)
    {
        if (@event.Entityid > 0)
        {
            _infernoOwnerByEntityIndex.Remove(@event.Entityid);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;

        if (victim == null)
        {
            return HookResult.Continue;
        }

        var allowBotsForReverseDebug = Config.ReverseGrenadeFriendlyFireEnabled && Config.DebugTKReverse;
        if (!IsValidPlayer(victim, allowBotsForReverseDebug))
        {
            return HookResult.Continue;
        }

        var hasValidAttacker = attacker != null && IsValidPlayer(attacker, allowBotsForReverseDebug);
        var isSelf = hasValidAttacker && attacker == victim;
        if (isSelf && !Config.DebugIncludeSelfDamage)
        {
            return HookResult.Continue;
        }

        if (hasValidAttacker && !isSelf && attacker!.TeamNum != victim.TeamNum)
        {
            return HookResult.Continue;
        }

        var healthDamage = Math.Max(0, @event.DmgHealth);
        var armorDamage = GetEventArmorDamage(@event);
        if (healthDamage <= 0 && armorDamage <= 0)
        {
            return HookResult.Continue;
        }

        var expectedHealthBeforeHit = Math.Max(0, @event.Health + healthDamage);
        var expectedArmorBeforeHit = Math.Max(0, @event.Armor + armorDamage);

        var grenadeKey = NormalizeGrenadeKey(@event.Weapon);
        if (string.IsNullOrWhiteSpace(grenadeKey) || !DamageGrenadeKeys.Contains(grenadeKey))
        {
            return HookResult.Continue;
        }

        if (!hasValidAttacker)
        {
            var recentAttacker = TryResolveRecentFriendlyFireAttacker(victim, grenadeKey);
            if (recentAttacker != null && IsValidPlayer(recentAttacker, allowBotsForReverseDebug))
            {
                attacker = recentAttacker;
                hasValidAttacker = true;
                isSelf = attacker == victim;

                if (Config.DebugDamageTicks)
                {
                    Logger.LogInformation(
                        "FF attacker resolved from recent cache via player_hurt: tick={Tick} attacker={Attacker} victim={Victim} grenade={Grenade}",
                        Server.TickCount,
                        attacker.PlayerName,
                        victim.PlayerName,
                        grenadeKey);
                }
            }
        }

        if (isSelf && !Config.DebugIncludeSelfDamage)
        {
            return HookResult.Continue;
        }

        if (hasValidAttacker && !isSelf && attacker!.TeamNum != victim.TeamNum)
        {
            return HookResult.Continue;
        }

        var attackerNameForLog = hasValidAttacker ? attacker!.PlayerName : "<unknown>";
        var remainingHp = @event.Health;
        if (Config.DebugDamageTicks && IsContinuousFireGrenade(grenadeKey))
        {
            Logger.LogInformation(
                "FF tick hurt: tick={Tick} attacker={Attacker} victim={Victim} grenade={Grenade} dmgHealth={DmgHealth} dmgArmor={DmgArmor} victimHpEvent={VictimHp}",
                Server.TickCount,
                attackerNameForLog,
                victim.PlayerName,
                grenadeKey,
                healthDamage,
                armorDamage,
                remainingHp);
        }

        // Fallback path: if pre-damage reverse hook misses grenade ownership resolution,
        // we still reverse team damage based on the reliable player_hurt event payload.
        if (Config.ReverseGrenadeFriendlyFireEnabled && !isSelf)
        {
            if (hasValidAttacker)
            {
                CacheFriendlyFireAttribution(attacker!, victim, grenadeKey);

                if (ShouldApplyTkReverse(attacker!, out var previousTkCount))
                {
                    var targetHealthBeforeHit = expectedHealthBeforeHit;
                    var targetArmorBeforeHit = expectedArmorBeforeHit;
                    ApplyFriendlyFireNoLossBaselineCompensation(
                        attacker!,
                        victim,
                        grenadeKey,
                        ref targetHealthBeforeHit,
                        ref targetArmorBeforeHit);

                    var restoredFromSnapshot = TryRestoreVictimStateSnapshot(attacker!, victim, grenadeKey);
                    if (!restoredFromSnapshot)
                    {
                        RestoreFriendlyFireVictimState(victim, targetHealthBeforeHit, targetArmorBeforeHit);
                    }

                    remainingHp = GetPlayerHealth(victim);
                    var remainingArmor = GetPlayerArmor(victim);
                    UpdateFriendlyFireNoLossBaseline(attacker!, victim, remainingHp, remainingArmor);
                    _ = TryApplyReverseDamage(
                        attacker!,
                        healthDamage + armorDamage,
                        out var appliedDamage,
                        preserveOneHp: IsContinuousFireGrenade(grenadeKey));
                    if (appliedDamage > 0)
                    {
                        SendReverseDamageWarning(attacker!, victim, grenadeKey, appliedDamage);
                    }

                    Logger.LogInformation(
                        "Reverse FF applied via player_hurt fallback: attacker={Attacker} victim={Victim} grenade={Grenade} restoredHealth={RestoredHealth} restoredArmor={RestoredArmor} reversedDamage={ReversedDamage}",
                        attacker!.PlayerName,
                        victim.PlayerName,
                        grenadeKey,
                        healthDamage,
                        armorDamage,
                        appliedDamage);

                    if (Config.DebugDamageTicks && IsContinuousFireGrenade(grenadeKey))
                    {
                        Logger.LogInformation(
                            "FF tick hurt restore verify: tick={Tick} victim={Victim} grenade={Grenade} expectedHealth={ExpectedHealth} actualHealth={ActualHealth} expectedArmor={ExpectedArmor} actualArmor={ActualArmor}",
                            Server.TickCount,
                            victim.PlayerName,
                            grenadeKey,
                            targetHealthBeforeHit,
                            GetPlayerHealth(victim),
                            targetArmorBeforeHit,
                            GetPlayerArmor(victim));
                    }

                    EnforceVictimStateSoftNextFrames(
                        victim,
                        grenadeKey,
                        targetHealthBeforeHit,
                        targetArmorBeforeHit,
                        hasKnownAttacker: true);
                }
                else
                {
                    Logger.LogInformation(
                        "Reverse FF skipped by MinTkToTkReserve via player_hurt fallback: attacker={Attacker} victim={Victim} grenade={Grenade} previousTk={PreviousTk} reserve={Reserve}",
                        attacker!.PlayerName,
                        victim.PlayerName,
                        grenadeKey,
                        previousTkCount,
                        Math.Max(0, Config.MinTkToTkReserve));
                }
            }
            else if (IsContinuousFireGrenade(grenadeKey) && Math.Max(0, Config.MinTkToTkReserve) == 0)
            {
                RestoreFriendlyFireVictimState(victim, expectedHealthBeforeHit, expectedArmorBeforeHit);

                remainingHp = GetPlayerHealth(victim);
                if (Config.DebugDamageTicks)
                {
                    Logger.LogInformation(
                        "FF tick hurt unknown-attacker restored: tick={Tick} victim={Victim} grenade={Grenade} expectedHealth={ExpectedHealth} actualHealth={ActualHealth} expectedArmor={ExpectedArmor} actualArmor={ActualArmor}",
                        Server.TickCount,
                        victim.PlayerName,
                        grenadeKey,
                        expectedHealthBeforeHit,
                        remainingHp,
                        expectedArmorBeforeHit,
                        GetPlayerArmor(victim));
                }

                EnforceVictimStateSoftNextFrames(
                    victim,
                    grenadeKey,
                    expectedHealthBeforeHit,
                    expectedArmorBeforeHit,
                    hasKnownAttacker: false);
            }
        }

        if (hasValidAttacker)
        {
            PublishFriendlyFireMessage(attacker!, victim, grenadeKey, remainingHp, healthDamage + armorDamage, isSelf);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerTakeDamagePre(CCSPlayerPawn victimPawn, CTakeDamageInfo info)
    {
        if (!Config.ReverseGrenadeFriendlyFireEnabled)
        {
            return HookResult.Continue;
        }

        var victim = victimPawn.Controller.Value as CCSPlayerController;
        var allowBotsForReverseDebug = Config.DebugTKReverse;
        if (victim == null || !IsValidPlayer(victim, allowBotsForReverseDebug))
        {
            return HookResult.Continue;
        }

        var grenadeKey = GetDamageGrenadeKeyForTakeDamageInfo(info);
        if (string.IsNullOrWhiteSpace(grenadeKey) || !DamageGrenadeKeys.Contains(grenadeKey))
        {
            return HookResult.Continue;
        }

        var attacker = GetAttackerController(info);
        if (attacker == null || !IsValidPlayer(attacker, allowBotsForReverseDebug))
        {
            var mappedInfernoAttacker = TryResolveMappedInfernoAttacker(info);
            if (mappedInfernoAttacker != null && IsValidPlayer(mappedInfernoAttacker, allowBotsForReverseDebug))
            {
                attacker = mappedInfernoAttacker;
            }
        }

        if (attacker == null || !IsValidPlayer(attacker, allowBotsForReverseDebug))
        {
            var recentAttacker = TryResolveRecentFriendlyFireAttacker(victim, grenadeKey);
            if (recentAttacker != null && IsValidPlayer(recentAttacker, allowBotsForReverseDebug))
            {
                attacker = recentAttacker;

                if (Config.DebugDamageTicks)
                {
                    Logger.LogInformation(
                        "FF attacker resolved from recent cache via pre-hook: tick={Tick} attacker={Attacker} victim={Victim} grenade={Grenade}",
                        Server.TickCount,
                        attacker.PlayerName,
                        victim.PlayerName,
                        grenadeKey);
                }
            }
        }

        if (attacker == null || !IsValidPlayer(attacker, allowBotsForReverseDebug))
        {
            var attackerTeamNum = TryResolveAttackerTeamNum(info);
            var isFriendlyByTeam = attackerTeamNum.HasValue && attackerTeamNum.Value == victim.TeamNum;
            var isFriendlyByRatio = IsFriendlyFireByRatio(info);
            var reserve = Math.Max(0, Config.MinTkToTkReserve);
            if ((!isFriendlyByTeam && !isFriendlyByRatio) || reserve > 0)
            {
                return HookResult.Continue;
            }

            if (Config.DebugDamageTicks && IsContinuousFireGrenade(grenadeKey))
            {
                Logger.LogInformation(
                    "FF tick pre (unknown attacker): tick={Tick} victim={Victim} grenade={Grenade} damage={Damage} total={Total} original={Original}",
                    Server.TickCount,
                    victim.PlayerName,
                    grenadeKey,
                    info.Damage,
                    info.TotalledDamage,
                    ReadDamageInfoFloat(info, "OriginalDamage"));
            }

            var unknownVictimHealthBeforeSuppress = GetPlayerHealth(victim);
            var unknownVictimArmorBeforeSuppress = GetPlayerArmor(victim);
            SuppressFriendlyFireDamage(info);
            EnforceVictimNoLossNextFrame(
                victim,
                grenadeKey,
                unknownVictimHealthBeforeSuppress,
                unknownVictimArmorBeforeSuppress);

            return HookResult.Handled;
        }

        if (attacker == null)
        {
            return HookResult.Continue;
        }

        if (attacker == victim || attacker.TeamNum != victim.TeamNum)
        {
            return HookResult.Continue;
        }

        CacheFriendlyFireAttribution(attacker, victim, grenadeKey);

        if (!ShouldApplyTkReverse(attacker, out _))
        {
            // Reserve window: allow damage to teammate until TK reverse activates.
            return HookResult.Continue;
        }

        var damage = GetReverseDamageAmount(info);
        if (Config.DebugDamageTicks && IsContinuousFireGrenade(grenadeKey))
        {
            Logger.LogInformation(
                "FF tick pre: tick={Tick} attacker={Attacker} victim={Victim} grenade={Grenade} damage={Damage} total={Total} original={Original} base={Base}",
                Server.TickCount,
                attacker.PlayerName,
                victim.PlayerName,
                grenadeKey,
                info.Damage,
                info.TotalledDamage,
                ReadDamageInfoFloat(info, "OriginalDamage"),
                ReadDamageInfoFloat(info, "BaseDamage"));
        }

        var victimHealthBeforeSuppress = GetPlayerHealth(victim);
        var victimArmorBeforeSuppress = GetPlayerArmor(victim);
        UpdateFriendlyFireNoLossBaseline(attacker, victim, victimHealthBeforeSuppress, victimArmorBeforeSuppress);
        CacheVictimStateSnapshot(attacker, victim, grenadeKey);
        SuppressFriendlyFireDamage(info);
        EnforceVictimNoLossNextFrame(
            attacker,
            victim,
            grenadeKey,
            victimHealthBeforeSuppress,
            victimArmorBeforeSuppress);

        var appliedDamage = 0;
        if (damage > 0)
        {
            _ = TryApplyReverseDamage(
                attacker,
                damage,
                out appliedDamage,
                preserveOneHp: IsContinuousFireGrenade(grenadeKey));
        }

        if (appliedDamage > 0)
        {
            SendReverseDamageWarning(attacker, victim, grenadeKey, appliedDamage);
        }

        var reportedDamage = appliedDamage > 0 ? appliedDamage : damage;
        PublishFriendlyFireMessage(
            attacker,
            victim,
            grenadeKey,
            GetPlayerHealth(victim),
            reportedDamage,
            isSelfDamage: false);
        Logger.LogInformation(
            "Reverse FF applied via pre-damage hook: attacker={Attacker} victim={Victim} grenade={Grenade} preventedDamage={PreventedDamage} reversedDamage={ReversedDamage}",
            attacker.PlayerName,
            victim.PlayerName,
            grenadeKey,
            damage,
            appliedDamage);
        return HookResult.Handled;
    }

    private void OnPlayerTakeDamagePost(CCSPlayerPawn victimPawn, CTakeDamageInfo info, CTakeDamageResult result)
    {
        if (!Config.ReverseGrenadeFriendlyFireEnabled)
        {
            return;
        }

        var victim = victimPawn.Controller.Value as CCSPlayerController;
        var allowBotsForReverseDebug = Config.DebugTKReverse;
        if (victim == null || !IsValidPlayer(victim, allowBotsForReverseDebug))
        {
            return;
        }

        var healthLost = Math.Max(0, result.HealthLost);
        if (healthLost <= 0)
        {
            return;
        }

        var grenadeKey = GetDamageGrenadeKeyForTakeDamageInfo(info);
        if (string.IsNullOrWhiteSpace(grenadeKey) || !DamageGrenadeKeys.Contains(grenadeKey))
        {
            return;
        }

        var attacker = GetAttackerController(info);
        if (attacker == null || !IsValidPlayer(attacker, allowBotsForReverseDebug))
        {
            var mappedInfernoAttacker = TryResolveMappedInfernoAttacker(info);
            if (mappedInfernoAttacker != null && IsValidPlayer(mappedInfernoAttacker, allowBotsForReverseDebug))
            {
                attacker = mappedInfernoAttacker;
            }
        }

        if (attacker == null || !IsValidPlayer(attacker, allowBotsForReverseDebug))
        {
            var recentAttacker = TryResolveRecentFriendlyFireAttacker(victim, grenadeKey);
            if (recentAttacker != null && IsValidPlayer(recentAttacker, allowBotsForReverseDebug))
            {
                attacker = recentAttacker;

                if (Config.DebugDamageTicks)
                {
                    Logger.LogInformation(
                        "FF attacker resolved from recent cache via post-hook: tick={Tick} attacker={Attacker} victim={Victim} grenade={Grenade}",
                        Server.TickCount,
                        attacker.PlayerName,
                        victim.PlayerName,
                        grenadeKey);
                }
            }
        }

        var hasValidAttacker = attacker != null && IsValidPlayer(attacker, allowBotsForReverseDebug);
        if (hasValidAttacker)
        {
            if (attacker == victim || attacker!.TeamNum != victim.TeamNum)
            {
                return;
            }

            CacheFriendlyFireAttribution(attacker!, victim, grenadeKey);

            if (!ShouldApplyTkReverse(attacker!, out _))
            {
                return;
            }
        }
        else
        {
            var attackerTeamNum = TryResolveAttackerTeamNum(info);
            var isFriendlyByTeam = attackerTeamNum.HasValue && attackerTeamNum.Value == victim.TeamNum;
            var isFriendlyByRatio = IsFriendlyFireByRatio(info);
            if ((!isFriendlyByTeam && !isFriendlyByRatio) || Math.Max(0, Config.MinTkToTkReserve) > 0)
            {
                return;
            }
        }

        var healthBefore = Math.Max(0, result.HealthBefore);
        if (healthBefore <= 0)
        {
            return;
        }

        var currentHealth = GetPlayerHealth(victim);
        if (currentHealth <= 0 || currentHealth >= healthBefore)
        {
            return;
        }

        var pawn = victim.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        var maxHealth = GetPlayerMaxHealth(victim);
        var targetHealth = maxHealth > 0 ? Math.Min(healthBefore, maxHealth) : healthBefore;
        if (targetHealth <= currentHealth)
        {
            return;
        }

        pawn.Health = targetHealth;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        if (hasValidAttacker)
        {
            UpdateFriendlyFireNoLossBaseline(attacker!, victim, targetHealth, GetPlayerArmor(victim));
        }

        if (Config.DebugDamageTicks)
        {
            Logger.LogInformation(
                "FF tick post restore: tick={Tick} victim={Victim} grenade={Grenade} health={Current}->{Target} lost={Lost} friendlyRatio={Ratio}",
                Server.TickCount,
                victim.PlayerName,
                grenadeKey,
                currentHealth,
                targetHealth,
                healthLost,
                info.FriendlyFireDamageReductionRatio);
        }
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null)
        {
            return HookResult.Continue;
        }

        _grenadesThrown.Remove(player.SteamID);
        _grenadeTkCount.Remove(player.SteamID);
        ClearCooldownsForPlayer(player.SteamID);
        ClearVictimStateSnapshotsForPlayer(player.SteamID);
        ClearFriendlyFireAttributionsForPlayer(player.SteamID);
        ClearFriendlyFireNoLossBaselinesForPlayer(player.SteamID);

        return HookResult.Continue;
    }

    private static Dictionary<string, string> NormalizeDictionary(Dictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
        {
            return result;
        }

        foreach (var kvp in source)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key))
            {
                continue;
            }

            result[kvp.Key.Trim()] = kvp.Value ?? string.Empty;
        }

        return result;
    }

    private static Dictionary<string, string> MergeDictionary(
        Dictionary<string, string>? defaults,
        Dictionary<string, string>? overrides)
    {
        var result = NormalizeDictionary(defaults);
        foreach (var kvp in NormalizeDictionary(overrides))
        {
            result[kvp.Key] = kvp.Value;
        }

        return result;
    }

    private void LoadLanguageFile()
    {
        var languageCode = NormalizeLanguageCode(Config.Language);
        var defaultLanguage = CreateDefaultEnglishLanguage();
        _language = defaultLanguage;

        try
        {
            var languageDirectory = ResolveLanguageDirectory(languageCode);
            Directory.CreateDirectory(languageDirectory);
            EnsureDefaultLanguageFiles(languageDirectory);

            var languageFilePath = Path.Combine(languageDirectory, $"{languageCode}.json");
            if (File.Exists(languageFilePath))
            {
                var languageJson = File.ReadAllText(languageFilePath);
                _language = MergeLanguage(defaultLanguage, DeserializeLanguage(languageJson));
                Logger.LogInformation("Loaded language file: {Path}", languageFilePath);
            }
            else
            {
                Logger.LogWarning("Language file not found: {Path}. Falling back to built-in defaults for {Language}.", languageFilePath, languageCode);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load language files. Falling back to built-in defaults for {Language}.", languageCode);
        }
    }

    private string ResolveLanguageDirectory(string languageCode)
    {
        var candidates = GetLanguageDirectoryCandidates();
        Logger.LogInformation("Language search candidates: {Candidates}", string.Join(" | ", candidates));

        foreach (var directory in candidates)
        {
            var languagePath = Path.Combine(directory, $"{languageCode}.json");
            if (File.Exists(languagePath))
            {
                Logger.LogInformation("Using language directory (exact match): {Directory}", directory);
                return directory;
            }
        }

        foreach (var directory in candidates)
        {
            if (File.Exists(Path.Combine(directory, "en.json")))
            {
                Logger.LogInformation("Using language directory (fallback file match): {Directory}", directory);
                return directory;
            }
        }

        var fallback = candidates.Count > 0 ? candidates[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "lang"));
        Logger.LogInformation("Using language directory (default): {Directory}", fallback);
        return fallback;
    }

    private List<string> GetLanguageDirectoryCandidates()
    {
        var configuredPath = (Config.LanguageDirectory ?? string.Empty).Trim();
        if (configuredPath.Length == 0)
        {
            configuredPath = "lang";
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(ModuleDirectory))
        {
            var configPluginBase = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", ModuleName));
            AddLanguageDirectoryCandidate(candidates, configuredPath, configPluginBase);
        }

        AddLanguageDirectoryCandidate(candidates, configuredPath, null);

        if (Path.IsPathRooted(configuredPath))
        {
            return candidates;
        }

        AddLanguageDirectoryCandidate(candidates, configuredPath, ModuleDirectory);
        AddLanguageDirectoryCandidate(candidates, configuredPath, Path.GetDirectoryName(ModulePath));
        AddLanguageDirectoryCandidate(candidates, configuredPath, AppContext.BaseDirectory);

        return candidates;
    }

    private static void AddLanguageDirectoryCandidate(List<string> candidates, string configuredPath, string? baseDirectory)
    {
        string fullPath;
        if (Path.IsPathRooted(configuredPath))
        {
            fullPath = Path.GetFullPath(configuredPath);
        }
        else if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            fullPath = Path.GetFullPath(Path.Combine(baseDirectory, configuredPath));
        }
        else
        {
            return;
        }

        foreach (var existing in candidates)
        {
            if (string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        candidates.Add(fullPath);
    }

    private static string NormalizeLanguageCode(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "en";
        }

        var normalized = language.Trim().ToLowerInvariant();
        if (normalized.StartsWith("en", StringComparison.Ordinal))
        {
            return "en";
        }

        return "en";
    }

    private static GrenadeManagerLanguage DeserializeLanguage(string languageJson)
    {
        return JsonSerializer.Deserialize<GrenadeManagerLanguage>(
                   languageJson,
                   new JsonSerializerOptions
                   {
                       PropertyNameCaseInsensitive = true,
                       ReadCommentHandling = JsonCommentHandling.Skip,
                       AllowTrailingCommas = true
                   })
               ?? new GrenadeManagerLanguage();
    }

    private static GrenadeManagerLanguage MergeLanguage(GrenadeManagerLanguage defaults, GrenadeManagerLanguage overrides)
    {
        return new GrenadeManagerLanguage
        {
            FriendlyFireMessageTemplate =
                !string.IsNullOrWhiteSpace(overrides.FriendlyFireMessageTemplate)
                    ? overrides.FriendlyFireMessageTemplate
                    : defaults.FriendlyFireMessageTemplate,
            FriendlyFireMessageByGrenade = MergeDictionary(defaults.FriendlyFireMessageByGrenade, overrides.FriendlyFireMessageByGrenade),
            FriendlyFireRepeatMessageTemplate =
                !string.IsNullOrWhiteSpace(overrides.FriendlyFireRepeatMessageTemplate)
                    ? overrides.FriendlyFireRepeatMessageTemplate
                    : defaults.FriendlyFireRepeatMessageTemplate,
            FriendlyFireRepeatMessageByGrenade =
                MergeDictionary(defaults.FriendlyFireRepeatMessageByGrenade, overrides.FriendlyFireRepeatMessageByGrenade),
            FriendlyFireVictimMessageTemplate =
                !string.IsNullOrWhiteSpace(overrides.FriendlyFireVictimMessageTemplate)
                    ? overrides.FriendlyFireVictimMessageTemplate
                    : defaults.FriendlyFireVictimMessageTemplate,
            ReversePendingWarningTemplate =
                !string.IsNullOrWhiteSpace(overrides.ReversePendingWarningTemplate)
                    ? overrides.ReversePendingWarningTemplate
                    : defaults.ReversePendingWarningTemplate,
            ReverseNextWarningTemplate =
                !string.IsNullOrWhiteSpace(overrides.ReverseNextWarningTemplate)
                    ? overrides.ReverseNextWarningTemplate
                    : defaults.ReverseNextWarningTemplate,
            GrenadeThrowMessageTemplate =
                !string.IsNullOrWhiteSpace(overrides.GrenadeThrowMessageTemplate)
                    ? overrides.GrenadeThrowMessageTemplate
                    : defaults.GrenadeThrowMessageTemplate,
            GrenadeThrowMessageByGrenade = MergeDictionary(defaults.GrenadeThrowMessageByGrenade, overrides.GrenadeThrowMessageByGrenade),
            ReverseDamageWarningTemplate =
                !string.IsNullOrWhiteSpace(overrides.ReverseDamageWarningTemplate)
                    ? overrides.ReverseDamageWarningTemplate
                    : defaults.ReverseDamageWarningTemplate,
            GrenadeDisplayNames = MergeDictionary(defaults.GrenadeDisplayNames, overrides.GrenadeDisplayNames)
        };
    }

    private void EnsureDefaultLanguageFiles(string languageDirectory)
    {
        EnsureLanguageFile(Path.Combine(languageDirectory, "en.json"), CreateDefaultEnglishLanguage());
    }

    private void EnsureLanguageFile(string filePath, GrenadeManagerLanguage language)
    {
        if (File.Exists(filePath))
        {
            return;
        }

        var languageJson = JsonSerializer.Serialize(language, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, languageJson);
    }

    private static GrenadeManagerLanguage CreateDefaultEnglishLanguage()
    {
        return new GrenadeManagerLanguage
        {
            FriendlyFireMessageTemplate =
                " {grey}[Absynthium - Grenade] {red}{attacker}{white} hit {green}{victim}{white} with {lightred}{grenade}{white} [Grenade TK count: {orange}{tk_count}{white}]",
            FriendlyFireRepeatMessageTemplate =
                " {grey}[Absynthium - Grenade] {red}{attacker}{white} TKed {green}{victim}{white} with {lightred}{grenade}{white}, -{orange}{damage}{white} HP [Victim HP left: {orange}{hp}{white}] [Attacker HP left: {orange}{attacker_hp}{white}] [Grenade TK count: {orange}{tk_count}{white}]",
            FriendlyFireVictimMessageTemplate =
                " {grey}[Absynthium - Grenade] {red}{attacker}{white} hit you with {lightred}{grenade}{white}. Reverse TKs taken by thrower: {orange}{reverse_count}{white}",
            ReversePendingWarningTemplate =
                " {grey}[Absynthium - Grenade]{white} Warning {red}{attacker}{white}: {orange}{remaining_tk}{white} grenade TK before {lightred}TK reverse{white} activates.",
            ReverseNextWarningTemplate =
                " {grey}[Absynthium - Grenade]{white} Warning {red}{attacker}{white}: your next teammate grenade hit will apply {lightred}TK reverse{white}.",
            GrenadeThrowMessageTemplate = "[{team_short}] {attacker} @{place} : {grenade} !",
            ReverseDamageWarningTemplate =
                " {grey}[Absynthium - Grenade]{white} You took {lightred}{damage}{white} damage from your {lightred}{grenade}{white} (teammate hit: {lime}{victim}{white}).",
            GrenadeDisplayNames = new Dictionary<string, string>
            {
                ["hegrenade"] = "HE grenade",
                ["molotov"] = "Molotov",
                ["incgrenade"] = "Incendiary",
                ["flashbang"] = "Flashbang",
                ["smokegrenade"] = "Smoke grenade",
                ["decoy"] = "Decoy"
            }
        };
    }

    private void OnMapStart(string mapName)
    {
        _infernoOwnerByEntityIndex.Clear();
        _recentFriendlyFireAttribution.Clear();
        _friendlyFireNoLossBaselines.Clear();
        ScheduleGrenadeRadioDisable();
    }

    private static string NormalizeWeaponKeyRaw(string weapon)
    {
        if (string.IsNullOrWhiteSpace(weapon))
        {
            return string.Empty;
        }

        var key = weapon.Trim().ToLowerInvariant();
        if (key.StartsWith("weapon_", StringComparison.Ordinal))
        {
            key = key.Substring(7);
        }

        if (key.EndsWith("_projectile", StringComparison.Ordinal))
        {
            key = key.Substring(0, key.Length - 11);
        }

        return key;
    }

    private static bool IsContinuousFireGrenade(string grenadeKey)
        => grenadeKey is "molotov" or "incgrenade";

    private static string NormalizeIncidentGrenadeKey(string grenadeKey)
    {
        var normalized = NormalizeGrenadeKey(grenadeKey);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = NormalizeWeaponKeyRaw(grenadeKey);
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return IsContinuousFireGrenade(normalized) ? "fire" : normalized;
    }

    private static string NormalizeGrenadeKey(string weapon)
    {
        var key = NormalizeWeaponKeyRaw(weapon);
        if (key.Length == 0)
        {
            return string.Empty;
        }

        if (key.Contains("inferno", StringComparison.Ordinal) ||
            key.Contains("molotov", StringComparison.Ordinal))
        {
            return "molotov";
        }

        if (key.Contains("incendiary", StringComparison.Ordinal) ||
            key.Contains("incgrenade", StringComparison.Ordinal))
        {
            return "incgrenade";
        }

        return key;
    }

    private static string GetDamageGrenadeKey(CTakeDamageInfo info)
    {
        var key = NormalizeGrenadeKey(info.Inflictor.Value?.DesignerName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        key = NormalizeGrenadeKey(info.Ability.Value?.DesignerName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        key = NormalizeGrenadeKey(info.Attacker.Value?.DesignerName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        return string.Empty;
    }

    private static string GetDamageGrenadeKeyForTakeDamageInfo(CTakeDamageInfo info)
    {
        var key = GetDamageGrenadeKey(info);
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        key = NormalizeGrenadeKey(ReadEntityDesignerName(info.Inflictor.Value));
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        key = NormalizeGrenadeKey(ReadEntityDesignerName(info.Ability.Value));
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        key = NormalizeGrenadeKey(ReadEntityDesignerName(info.Attacker.Value));
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        var inflictorHint = NormalizeWeaponKeyRaw(ReadEntityDesignerName(info.Inflictor.Value));
        if (inflictorHint.Contains("inferno", StringComparison.Ordinal) ||
            inflictorHint.Contains("molotov", StringComparison.Ordinal))
        {
            return "molotov";
        }

        if (inflictorHint.Contains("incendiary", StringComparison.Ordinal) ||
            inflictorHint.Contains("incgrenade", StringComparison.Ordinal))
        {
            return "incgrenade";
        }

        return IsBurnDamage(info) ? "molotov" : string.Empty;
    }

    private static string ReadEntityDesignerName(CEntityInstance? entity)
    {
        if (entity == null || !entity.IsValid)
        {
            return string.Empty;
        }

        try
        {
            return entity.DesignerName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void PublishFriendlyFireMessage(
        CCSPlayerController attacker,
        CCSPlayerController victim,
        string grenadeKey,
        int remainingHp,
        int damageAmount,
        bool isSelfDamage)
    {
        var incidentGrenadeKey = NormalizeIncidentGrenadeKey(grenadeKey);
        var shouldRegisterIncident = isSelfDamage || ShouldRegisterFriendlyFireIncident(attacker.SteamID, victim.SteamID, incidentGrenadeKey);
        var tkCount = isSelfDamage
            ? GetCount(_grenadeTkCount, attacker.SteamID)
            : shouldRegisterIncident
                ? IncrementCount(_grenadeTkCount, attacker.SteamID)
                : GetCount(_grenadeTkCount, attacker.SteamID);
        var thrownCount = GetCount(_grenadesThrown, attacker.SteamID);
        var reverseCount = GetReverseIncidentCount(tkCount);

        if (!isSelfDamage && !shouldRegisterIncident)
        {
            return;
        }

        var grenadeDisplay = GetGrenadeDisplay(grenadeKey);
        var template = tkCount <= 1 ? GetFriendlyFireTemplate(grenadeKey) : GetFriendlyFireRepeatTemplate(grenadeKey);
        var message = ApplyTemplate(
            template,
            attacker,
            victim,
            grenadeDisplay,
            grenadeKey,
            remainingHp,
            tkCount,
            thrownCount,
            damageAmount,
            reverseCount);

        if (ShouldSendFriendlyFireMessage(attacker.SteamID, victim.SteamID, incidentGrenadeKey))
        {
            SendToAdmins(message);
            SendDebugToAttacker(attacker, message);
            SendVictimFriendlyFireMessage(
                attacker,
                victim,
                grenadeKey,
                remainingHp,
                damageAmount,
                tkCount,
                thrownCount,
                reverseCount,
                isSelfDamage);
            SendReversePendingWarning(
                attacker,
                victim,
                grenadeKey,
                remainingHp,
                damageAmount,
                tkCount,
                thrownCount,
                isSelfDamage);
            LogToServer(message);
        }
    }

    private string GetGrenadeDisplay(string grenadeKey)
    {
        if (_language.GrenadeDisplayNames.TryGetValue(grenadeKey, out var languageDisplay) &&
            !string.IsNullOrWhiteSpace(languageDisplay) &&
            !LooksLikeMessageTemplate(languageDisplay))
        {
            return languageDisplay;
        }

        return grenadeKey;
    }

    private string GetFriendlyFireTemplate(string grenadeKey)
    {
        if (_language.FriendlyFireMessageByGrenade.TryGetValue(grenadeKey, out var languageTemplate) &&
            !string.IsNullOrWhiteSpace(languageTemplate))
        {
            return languageTemplate;
        }

        if (TryGetDisplayMessageTemplate(grenadeKey, out var displayTemplate))
        {
            return displayTemplate;
        }

        if (!string.IsNullOrWhiteSpace(_language.FriendlyFireMessageTemplate))
        {
            return _language.FriendlyFireMessageTemplate;
        }

        return "{attacker} hit {victim} with {grenade} [Victim HP left: {hp}] [Grenade TK count: {tk_count}]";
    }

    private string GetFriendlyFireRepeatTemplate(string grenadeKey)
    {
        if (_language.FriendlyFireRepeatMessageByGrenade.TryGetValue(grenadeKey, out var languageTemplate) &&
            !string.IsNullOrWhiteSpace(languageTemplate))
        {
            return languageTemplate;
        }

        if (!string.IsNullOrWhiteSpace(_language.FriendlyFireRepeatMessageTemplate))
        {
            return _language.FriendlyFireRepeatMessageTemplate;
        }

        return "{attacker} TKed {victim} with {grenade}: -{damage} HP (victim: {hp} HP, thrower: {attacker_hp} HP). [Grenade TK count: {tk_count}]";
    }

    private string GetFriendlyFireVictimTemplate()
    {
        if (!string.IsNullOrWhiteSpace(_language.FriendlyFireVictimMessageTemplate))
        {
            return _language.FriendlyFireVictimMessageTemplate;
        }

        return "{attacker} hit you with {grenade}. Reverse TKs taken by thrower: {reverse_count}.";
    }

    private string GetReversePendingWarningTemplate(bool reverseOnNextTk)
    {
        if (reverseOnNextTk)
        {
            if (!string.IsNullOrWhiteSpace(_language.ReverseNextWarningTemplate))
            {
                return _language.ReverseNextWarningTemplate;
            }

            return " {grey}[Absynthium - Grenade]{white} Warning {red}{attacker}{white}: your next teammate grenade hit will apply {lightred}TK reverse{white}.";
        }

        if (!string.IsNullOrWhiteSpace(_language.ReversePendingWarningTemplate))
        {
            return _language.ReversePendingWarningTemplate;
        }

        return " {grey}[Absynthium - Grenade]{white} Warning {red}{attacker}{white}: {orange}{remaining_tk}{white} grenade TK before {lightred}TK reverse{white} activates.";
    }

    private int GetReverseIncidentCount(int tkCount)
    {
        if (!Config.ReverseGrenadeFriendlyFireEnabled)
        {
            return 0;
        }

        var reserve = Math.Max(0, Config.MinTkToTkReserve);
        return Math.Max(0, tkCount - reserve);
    }

    private void SendVictimFriendlyFireMessage(
        CCSPlayerController attacker,
        CCSPlayerController victim,
        string grenadeKey,
        int remainingHp,
        int damageAmount,
        int tkCount,
        int thrownCount,
        int reverseCount,
        bool isSelfDamage)
    {
        if (isSelfDamage || victim == attacker || !IsValidPlayer(victim))
        {
            return;
        }

        var template = GetFriendlyFireVictimTemplate();
        if (string.IsNullOrWhiteSpace(template))
        {
            return;
        }

        var grenadeDisplay = GetGrenadeDisplay(grenadeKey);
        var message = ApplyTemplate(
            template,
            attacker,
            victim,
            grenadeDisplay,
            grenadeKey,
            remainingHp,
            tkCount,
            thrownCount,
            damageAmount,
            reverseCount);
        victim.PrintToChat(message);
    }

    private void SendReversePendingWarning(
        CCSPlayerController attacker,
        CCSPlayerController victim,
        string grenadeKey,
        int remainingHp,
        int damageAmount,
        int tkCount,
        int thrownCount,
        bool isSelfDamage)
    {
        if (!Config.ReverseGrenadeFriendlyFireEnabled || isSelfDamage || attacker == victim)
        {
            return;
        }

        if (!IsValidPlayer(attacker, Config.DebugTKReverse))
        {
            return;
        }

        var reserve = Math.Max(0, Config.MinTkToTkReserve);
        if (reserve <= 0 || tkCount <= 0 || tkCount > reserve)
        {
            return;
        }

        var remainingTkBeforeReverse = reserve - tkCount + 1;
        if (remainingTkBeforeReverse <= 0)
        {
            return;
        }

        var reverseOnNextTk = remainingTkBeforeReverse == 1;
        var template = GetReversePendingWarningTemplate(reverseOnNextTk);
        if (string.IsNullOrWhiteSpace(template))
        {
            return;
        }

        var grenadeDisplay = GetGrenadeDisplay(grenadeKey);
        var message = ApplyTemplate(
            template,
            attacker,
            victim,
            grenadeDisplay,
            grenadeKey,
            remainingHp,
            tkCount,
            thrownCount,
            damageAmount,
            GetReverseIncidentCount(tkCount),
            remainingTkBeforeReverse);
        attacker.PrintToChat(message);
    }

    private static int IncrementCount(Dictionary<ulong, int> counts, ulong steamId)
    {
        if (!counts.TryGetValue(steamId, out var current))
        {
            current = 0;
        }

        current++;
        counts[steamId] = current;
        return current;
    }

    private static int GetCount(Dictionary<ulong, int> counts, ulong steamId)
        => counts.TryGetValue(steamId, out var current) ? current : 0;

    private bool ShouldRegisterGrenadeThrow(ulong throwerId, string grenadeKey)
    {
        if (!IsContinuousFireGrenade(grenadeKey))
        {
            return true;
        }

        var key = $"throw:{throwerId}:{grenadeKey}";
        var now = DateTime.UtcNow;
        if (_lastFriendlyFireMessage.TryGetValue(key, out var last) &&
            (now - last).TotalSeconds < ContinuousGrenadeIncidentWindowSeconds)
        {
            return false;
        }

        _lastFriendlyFireMessage[key] = now;
        CleanupOldCooldowns(now, Math.Max(FriendlyFireMessageDedupSeconds, ContinuousGrenadeIncidentWindowSeconds));
        return true;
    }

    private bool ShouldRegisterFriendlyFireIncident(ulong attackerId, ulong victimId, string grenadeKey)
    {
        if (string.IsNullOrWhiteSpace(grenadeKey))
        {
            return true;
        }

        if (grenadeKey != "fire" && !IsContinuousFireGrenade(grenadeKey))
        {
            return true;
        }

        var key = $"incident:{attackerId}:{victimId}:{grenadeKey}";
        var now = DateTime.UtcNow;
        if (_lastFriendlyFireMessage.TryGetValue(key, out var last) &&
            (now - last).TotalSeconds < ContinuousGrenadeIncidentWindowSeconds)
        {
            return false;
        }

        _lastFriendlyFireMessage[key] = now;
        CleanupOldCooldowns(now, Math.Max(FriendlyFireMessageDedupSeconds, ContinuousGrenadeIncidentWindowSeconds));
        return true;
    }

    private bool ShouldSendFriendlyFireMessage(ulong attackerId, ulong victimId, string grenadeKey)
    {
        if (string.IsNullOrWhiteSpace(grenadeKey))
        {
            return true;
        }

        var cooldownSeconds = FriendlyFireMessageDedupSeconds;

        var key = $"{attackerId}:{victimId}:{grenadeKey}";
        var now = DateTime.UtcNow;

        if (_lastFriendlyFireMessage.TryGetValue(key, out var last) &&
            (now - last).TotalSeconds < cooldownSeconds)
        {
            return false;
        }

        _lastFriendlyFireMessage[key] = now;
        CleanupOldCooldowns(now, cooldownSeconds);
        return true;
    }

    private bool ShouldSendReverseDamageWarning(ulong attackerId, string grenadeKey)
    {
        var groupedGrenadeKey = NormalizeIncidentGrenadeKey(grenadeKey);
        if (string.IsNullOrWhiteSpace(groupedGrenadeKey))
        {
            groupedGrenadeKey = grenadeKey;
        }

        var cooldownSeconds = ReverseWarningDedupSeconds;

        var key = $"reverse:{attackerId}:{groupedGrenadeKey}";
        var now = DateTime.UtcNow;

        if (_lastFriendlyFireMessage.TryGetValue(key, out var last) &&
            (now - last).TotalSeconds < cooldownSeconds)
        {
            return false;
        }

        _lastFriendlyFireMessage[key] = now;
        CleanupOldCooldowns(now, cooldownSeconds);
        return true;
    }

    private bool ShouldApplyTkReverse(CCSPlayerController attacker, out int previousTkCount)
    {
        previousTkCount = GetCount(_grenadeTkCount, attacker.SteamID);
        var reserve = Math.Max(0, Config.MinTkToTkReserve);
        return previousTkCount >= reserve;
    }

    private void CleanupOldCooldowns(DateTime now, double cooldownSeconds)
    {
        if (_lastFriendlyFireMessage.Count < 256)
        {
            return;
        }

        var threshold = now.AddSeconds(-Math.Max(cooldownSeconds, 5) * 2);
        var toRemove = new List<string>();
        foreach (var kvp in _lastFriendlyFireMessage)
        {
            if (kvp.Value < threshold)
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var key in toRemove)
        {
            _lastFriendlyFireMessage.Remove(key);
        }
    }

    private void ClearCooldownsForPlayer(ulong steamId)
    {
        if (_lastFriendlyFireMessage.Count == 0)
        {
            return;
        }

        var prefix = $"{steamId}:";
        var toRemove = new List<string>();
        foreach (var key in _lastFriendlyFireMessage.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) || key.Contains($":{steamId}:", StringComparison.Ordinal))
            {
                toRemove.Add(key);
            }
        }

        foreach (var key in toRemove)
        {
            _lastFriendlyFireMessage.Remove(key);
        }
    }

    private static string BuildFriendlyFireAttributionKey(ulong victimId, string grenadeKey)
    {
        var incidentGrenadeKey = NormalizeIncidentGrenadeKey(grenadeKey);
        if (string.IsNullOrWhiteSpace(incidentGrenadeKey))
        {
            incidentGrenadeKey = NormalizeGrenadeKey(grenadeKey);
        }

        if (string.IsNullOrWhiteSpace(incidentGrenadeKey))
        {
            incidentGrenadeKey = NormalizeWeaponKeyRaw(grenadeKey);
        }

        return string.IsNullOrWhiteSpace(incidentGrenadeKey)
            ? string.Empty
            : $"ffattr:{victimId}:{incidentGrenadeKey}";
    }

    private void CacheFriendlyFireAttribution(CCSPlayerController attacker, CCSPlayerController victim, string grenadeKey)
    {
        var key = BuildFriendlyFireAttributionKey(victim.SteamID, grenadeKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _recentFriendlyFireAttribution[key] = new FriendlyFireAttributionSnapshot
        {
            AttackerSteamId = attacker.SteamID,
            TimestampUtc = DateTime.UtcNow
        };

        CleanupFriendlyFireAttributions(DateTime.UtcNow);
    }

    private CCSPlayerController? TryResolveRecentFriendlyFireAttacker(CCSPlayerController victim, string grenadeKey)
    {
        var key = BuildFriendlyFireAttributionKey(victim.SteamID, grenadeKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (!_recentFriendlyFireAttribution.TryGetValue(key, out var snapshot))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if ((now - snapshot.TimestampUtc).TotalSeconds > FriendlyFireAttributionWindowSeconds)
        {
            _recentFriendlyFireAttribution.Remove(key);
            return null;
        }

        var attacker = FindPlayerBySteamId(snapshot.AttackerSteamId);
        if (attacker == null || !attacker.IsValid)
        {
            _recentFriendlyFireAttribution.Remove(key);
            return null;
        }

        return attacker;
    }

    private void CleanupFriendlyFireAttributions(DateTime now)
    {
        if (_recentFriendlyFireAttribution.Count < 256)
        {
            return;
        }

        var threshold = now.AddSeconds(-Math.Max(FriendlyFireAttributionWindowSeconds, 1d) * 2d);
        var toRemove = new List<string>();
        foreach (var kvp in _recentFriendlyFireAttribution)
        {
            if (kvp.Value.TimestampUtc < threshold)
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var key in toRemove)
        {
            _recentFriendlyFireAttribution.Remove(key);
        }
    }

    private void ClearFriendlyFireAttributionsForPlayer(ulong steamId)
    {
        if (_recentFriendlyFireAttribution.Count == 0)
        {
            return;
        }

        var victimToken = $"ffattr:{steamId}:";
        var toRemove = new List<string>();
        foreach (var kvp in _recentFriendlyFireAttribution)
        {
            if (kvp.Key.StartsWith(victimToken, StringComparison.Ordinal) || kvp.Value.AttackerSteamId == steamId)
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var key in toRemove)
        {
            _recentFriendlyFireAttribution.Remove(key);
        }
    }

    private static string BuildFriendlyFireNoLossBaselineKey(ulong attackerId, ulong victimId)
        => $"ffbase:{attackerId}:{victimId}";

    private void UpdateFriendlyFireNoLossBaseline(CCSPlayerController attacker, CCSPlayerController victim, int health, int armor)
    {
        if (health <= 0)
        {
            return;
        }

        var key = BuildFriendlyFireNoLossBaselineKey(attacker.SteamID, victim.SteamID);
        _friendlyFireNoLossBaselines[key] = new FriendlyFireNoLossBaselineSnapshot
        {
            Health = health,
            Armor = armor,
            TimestampUtc = DateTime.UtcNow
        };

        CleanupFriendlyFireNoLossBaselines(DateTime.UtcNow);
    }

    private void ApplyFriendlyFireNoLossBaselineCompensation(
        CCSPlayerController attacker,
        CCSPlayerController victim,
        string grenadeKey,
        ref int targetHealth,
        ref int targetArmor)
    {
        if (!IsContinuousFireGrenade(grenadeKey))
        {
            return;
        }

        var key = BuildFriendlyFireNoLossBaselineKey(attacker.SteamID, victim.SteamID);
        if (!_friendlyFireNoLossBaselines.TryGetValue(key, out var baseline))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - baseline.TimestampUtc).TotalSeconds > FriendlyFireNoLossBaselineWindowSeconds)
        {
            _friendlyFireNoLossBaselines.Remove(key);
            return;
        }

        var adjusted = false;
        if (targetHealth > 0 && baseline.Health > targetHealth)
        {
            var healthDrift = baseline.Health - targetHealth;
            if (healthDrift <= FriendlyFireNoLossMaxHpCompensation)
            {
                targetHealth = baseline.Health;
                adjusted = true;
            }
        }

        if (targetArmor >= 0 && baseline.Armor >= 0 && baseline.Armor > targetArmor)
        {
            var armorDrift = baseline.Armor - targetArmor;
            if (armorDrift <= FriendlyFireNoLossMaxArmorCompensation)
            {
                targetArmor = baseline.Armor;
                adjusted = true;
            }
        }

        if (adjusted && Config.DebugDamageTicks)
        {
            Logger.LogInformation(
                "FF tick baseline compensation: tick={Tick} attacker={Attacker} victim={Victim} grenade={Grenade} targetHealth={TargetHealth} targetArmor={TargetArmor}",
                Server.TickCount,
                attacker.PlayerName,
                victim.PlayerName,
                grenadeKey,
                targetHealth,
                targetArmor);
        }
    }

    private void CleanupFriendlyFireNoLossBaselines(DateTime now)
    {
        if (_friendlyFireNoLossBaselines.Count < 256)
        {
            return;
        }

        var threshold = now.AddSeconds(-Math.Max(FriendlyFireNoLossBaselineWindowSeconds, 1d) * 2d);
        var toRemove = new List<string>();
        foreach (var kvp in _friendlyFireNoLossBaselines)
        {
            if (kvp.Value.TimestampUtc < threshold)
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var key in toRemove)
        {
            _friendlyFireNoLossBaselines.Remove(key);
        }
    }

    private void ClearFriendlyFireNoLossBaselinesForPlayer(ulong steamId)
    {
        if (_friendlyFireNoLossBaselines.Count == 0)
        {
            return;
        }

        var token = $":{steamId}:";
        var toRemove = new List<string>();
        foreach (var key in _friendlyFireNoLossBaselines.Keys)
        {
            if (key.Contains(token, StringComparison.Ordinal))
            {
                toRemove.Add(key);
            }
        }

        foreach (var key in toRemove)
        {
            _friendlyFireNoLossBaselines.Remove(key);
        }
    }

    private static string BuildVictimStateSnapshotKey(ulong attackerId, ulong victimId, string grenadeKey)
    {
        var incidentGrenadeKey = NormalizeIncidentGrenadeKey(grenadeKey);
        if (string.IsNullOrWhiteSpace(incidentGrenadeKey))
        {
            incidentGrenadeKey = NormalizeGrenadeKey(grenadeKey);
        }

        if (string.IsNullOrWhiteSpace(incidentGrenadeKey))
        {
            incidentGrenadeKey = NormalizeWeaponKeyRaw(grenadeKey);
        }

        return $"snapshot:{attackerId}:{victimId}:{incidentGrenadeKey}";
    }

    private void CacheVictimStateSnapshot(CCSPlayerController attacker, CCSPlayerController victim, string grenadeKey)
    {
        var key = BuildVictimStateSnapshotKey(attacker.SteamID, victim.SteamID, grenadeKey);
        _pendingVictimStateRestore[key] = new VictimStateSnapshot
        {
            Health = GetPlayerHealth(victim),
            Armor = GetPlayerArmor(victim),
            TimestampUtc = DateTime.UtcNow
        };

        CleanupVictimStateSnapshots(DateTime.UtcNow);
    }

    private bool TryRestoreVictimStateSnapshot(CCSPlayerController attacker, CCSPlayerController victim, string grenadeKey)
    {
        var key = BuildVictimStateSnapshotKey(attacker.SteamID, victim.SteamID, grenadeKey);
        if (!_pendingVictimStateRestore.TryGetValue(key, out var snapshot))
        {
            return false;
        }

        _pendingVictimStateRestore.Remove(key);

        var now = DateTime.UtcNow;
        if ((now - snapshot.TimestampUtc).TotalSeconds > VictimStateSnapshotWindowSeconds)
        {
            return false;
        }

        RestoreFriendlyFireVictimState(victim, snapshot.Health, snapshot.Armor);
        return true;
    }

    private void CleanupVictimStateSnapshots(DateTime now)
    {
        if (_pendingVictimStateRestore.Count < 256)
        {
            return;
        }

        var threshold = now.AddSeconds(-Math.Max(VictimStateSnapshotWindowSeconds, 1d) * 2d);
        var toRemove = new List<string>();
        foreach (var kvp in _pendingVictimStateRestore)
        {
            if (kvp.Value.TimestampUtc < threshold)
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var key in toRemove)
        {
            _pendingVictimStateRestore.Remove(key);
        }
    }

    private void ClearVictimStateSnapshotsForPlayer(ulong steamId)
    {
        if (_pendingVictimStateRestore.Count == 0)
        {
            return;
        }

        var token = $":{steamId}:";
        var toRemove = new List<string>();
        foreach (var key in _pendingVictimStateRestore.Keys)
        {
            if (key.Contains(token, StringComparison.Ordinal))
            {
                toRemove.Add(key);
            }
        }

        foreach (var key in toRemove)
        {
            _pendingVictimStateRestore.Remove(key);
        }
    }

    private string ApplyTemplate(
        string template,
        CCSPlayerController attacker,
        CCSPlayerController? victim,
        string grenadeDisplay,
        string grenadeKey,
        int remainingHp,
        int tkCount,
        int thrownCount,
        int damageAmount = 0,
        int reverseCount = 0,
        int remainingTk = 0)
    {
        var result = template ?? string.Empty;
        var attackerHp = GetPlayerHealth(attacker);

        result = result.Replace("{attacker}", attacker.PlayerName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{attacker_hp}", attackerHp.ToString(), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{victim}", victim?.PlayerName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{hp}", remainingHp.ToString(), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{tk_count}", tkCount.ToString(), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{thrown_count}", thrownCount.ToString(), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{grenade}", grenadeDisplay, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{grenade_raw}", grenadeKey, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{team}", GetTeamName(attacker.TeamNum), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{team_short}", GetTeamShort(attacker.TeamNum), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{place}", GetPlaceName(attacker), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{steamid}", attacker.SteamID.ToString(), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{damage}", damageAmount.ToString(), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{reverse_count}", reverseCount.ToString(), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{remaining_tk}", remainingTk.ToString(), StringComparison.OrdinalIgnoreCase);

        return ApplyColorMarkup(result, attacker, victim);
    }

    private static string ApplyColorMarkup(string message, CCSPlayerController attacker, CCSPlayerController? victim)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message ?? string.Empty;
        }

        var normalized = NormalizeColorTags(message);
        normalized = EnsureLeadingSpaceForLeadingGreyTag(normalized);

        var colored = ColorTagRegex.Replace(normalized, match =>
        {
            var token = match.Value.Substring(1, match.Value.Length - 2).Trim();
            if (token.Length == 0)
            {
                return match.Value;
            }

            if (token.StartsWith("/", StringComparison.Ordinal))
            {
                return DefaultColorResolver(attacker);
            }

            if (token.Equals("victim", StringComparison.OrdinalIgnoreCase) && victim != null)
            {
                return ColorChar(ChatColors.ForPlayer(victim));
            }

            if (token.Equals("victimcolor", StringComparison.OrdinalIgnoreCase) && victim != null)
            {
                return ColorChar(ChatColors.ForPlayer(victim));
            }

            if (ColorTagResolvers.TryGetValue(token, out var resolver))
            {
                return resolver(attacker);
            }

            if (TryResolveHexToken(token, out var hex))
            {
                return "\x07" + hex;
            }

            return match.Value;
        });

        if (!string.IsNullOrEmpty(colored) && colored[0] != ChatColors.Default)
        {
            colored = ColorChar(ChatColors.Default) + colored;
        }

        return colored;
    }

    private static string EnsureLeadingSpaceForLeadingGreyTag(string message)
    {
        if (string.IsNullOrEmpty(message) || char.IsWhiteSpace(message[0]))
        {
            return message ?? string.Empty;
        }

        if (message.StartsWith("{grey}", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("{gray}", StringComparison.OrdinalIgnoreCase))
        {
            return " " + message;
        }

        return message;
    }

    private static string NormalizeColorTags(string message)
    {
        return Regex.Replace(
            message,
            @"\{color:([#a-z0-9_]+)\}",
            "{$1}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool TryResolveHexToken(string token, out string hex)
    {
        if (token.Length == 7 && token[0] == '#')
        {
            var candidate = token.Substring(1);
            if (IsHex(candidate))
            {
                hex = candidate.ToUpperInvariant();
                return true;
            }
        }

        if (token.Length == 6 && IsHex(token))
        {
            hex = token.ToUpperInvariant();
            return true;
        }

        hex = string.Empty;
        return false;
    }

    private static bool IsHex(string value)
    {
        foreach (var ch in value)
        {
            var isHex = (ch >= '0' && ch <= '9') ||
                        (ch >= 'a' && ch <= 'f') ||
                        (ch >= 'A' && ch <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private string GetGrenadeThrowTemplate(string grenadeKey)
    {
        if (_language.GrenadeDisplayNames.TryGetValue(grenadeKey, out var displayValue) &&
            !string.IsNullOrWhiteSpace(displayValue))
        {
            return displayValue;
        }

        if (_language.GrenadeThrowMessageByGrenade.TryGetValue(grenadeKey, out var languageTemplate) &&
            !string.IsNullOrWhiteSpace(languageTemplate))
        {
            return languageTemplate;
        }

        if (TryGetDisplayMessageTemplate(grenadeKey, out var displayTemplate))
        {
            return displayTemplate;
        }

        if (!string.IsNullOrWhiteSpace(_language.GrenadeThrowMessageTemplate))
        {
            return _language.GrenadeThrowMessageTemplate;
        }

        return "[{team_short}] {attacker} @{place} : {grenade} !";
    }

    private string GetReverseDamageWarningTemplate()
    {
        if (!string.IsNullOrWhiteSpace(_language.ReverseDamageWarningTemplate))
        {
            return _language.ReverseDamageWarningTemplate;
        }

        return "{color:Red}[TK REVERSE]{color:Default} You took {damage} damage from your {grenade} (teammate hit: {victim}).";
    }

    private bool TryGetDisplayMessageTemplate(string grenadeKey, out string template)
    {
        template = string.Empty;
        if (!_language.GrenadeDisplayNames.TryGetValue(grenadeKey, out var candidate) ||
            string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (!LooksLikeMessageTemplate(candidate))
        {
            return false;
        }

        template = candidate;
        return true;
    }

    private static bool LooksLikeMessageTemplate(string value)
    {
        foreach (var marker in TemplateMarkers)
        {
            if (value.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private void SendGrenadeThrowMessage(CCSPlayerController thrower, string message)
    {
        var scope = (Config.GrenadeThrowMessageScope ?? string.Empty).Trim().ToLowerInvariant();
        if (scope.Length == 0)
        {
            scope = "team";
        }

        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsValidPlayer(player))
            {
                continue;
            }

            if (!ShouldReceiveGrenadeThrowMessage(scope, player, thrower))
            {
                continue;
            }

            player.PrintToChat(message);
        }
    }

    private bool ShouldReceiveGrenadeThrowMessage(string scope, CCSPlayerController recipient, CCSPlayerController thrower)
    {
        return scope switch
        {
            "all" => true,
            "team" => IsPlayableTeam(thrower.TeamNum) && recipient.TeamNum == thrower.TeamNum,
            "admins" => IsAdmin(recipient),
            "attacker" => recipient == thrower,
            _ => IsPlayableTeam(thrower.TeamNum) && recipient.TeamNum == thrower.TeamNum
        };
    }

    private void ScheduleGrenadeRadioDisable()
    {
        var cvar = string.IsNullOrWhiteSpace(Config.GrenadeRadioCvar) ? "sv_ignoregrenaderadio" : Config.GrenadeRadioCvar;
        Server.NextFrame(() =>
        {
            try
            {
                Server.ExecuteCommand($"{cvar} 1");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to set grenade radio cvar.");
            }
        });
    }

    private static string GetTeamShort(int teamNum)
        => teamNum switch
        {
            2 => "T",
            3 => "CT",
            1 => "SPEC",
            _ => "UNK"
        };

    private static string GetTeamName(int teamNum)
        => teamNum switch
        {
            2 => "Terrorists",
            3 => "Counter-Terrorists",
            1 => "Spectators",
            _ => "Unknown"
        };

    private static bool IsPlayableTeam(int teamNum)
        => teamNum is 2 or 3;

    private static string GetPlaceName(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn?.Value;
        var place = pawn?.LastPlaceName;
        return string.IsNullOrWhiteSpace(place) ? string.Empty : place;
    }

    private static int GetPlayerHealth(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn?.Value;
        return pawn?.Health ?? 0;
    }

    private static CCSPlayerController? GetAttackerController(CTakeDamageInfo info)
    {
        var attacker = TryResolveController(info.Attacker.Value);
        if (attacker != null)
        {
            return attacker;
        }

        attacker = TryResolveController(info.Inflictor.Value);
        if (attacker != null)
        {
            return attacker;
        }

        attacker = TryResolveController(info.Ability.Value);
        if (attacker != null)
        {
            return attacker;
        }

        return null;
    }

    private static CCSPlayerController? TryResolveController(CEntityInstance? entity)
    {
        if (entity == null || !entity.IsValid)
        {
            return null;
        }

        if (entity is CCSPlayerController controller)
        {
            return controller;
        }

        if (entity is CBasePlayerPawn pawn)
        {
            return pawn.Controller.Value as CCSPlayerController;
        }

        foreach (var propertyName in ControllerOwnerPropertyCandidates)
        {
            var resolvedController = TryResolveControllerFromProperty(entity, propertyName);
            if (resolvedController != null)
            {
                return resolvedController;
            }
        }

        return TryResolveControllerFromLikelyOwnerProperties(entity);
    }

    private static CCSPlayerController? TryResolveControllerFromProperty(object source, string propertyName)
    {
        var property = source.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (property == null)
        {
            return null;
        }

        try
        {
            return TryResolveControllerFromUnknown(property.GetValue(source));
        }
        catch
        {
            return null;
        }
    }

    private static CCSPlayerController? TryResolveControllerFromUnknown(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is CCSPlayerController controller)
        {
            return controller;
        }

        if (value is CBasePlayerPawn pawn)
        {
            return pawn.Controller.Value as CCSPlayerController;
        }

        if (value is CEntityInstance entity)
        {
            return TryResolveController(entity);
        }

        var valueProperty = value.GetType().GetProperty(
            "Value",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (valueProperty == null)
        {
            return null;
        }

        var nestedValue = valueProperty.GetValue(value);
        if (ReferenceEquals(nestedValue, value))
        {
            return null;
        }

        return TryResolveControllerFromUnknown(nestedValue);
    }

    private static CCSPlayerController? TryResolveControllerFromLikelyOwnerProperties(object source)
    {
        foreach (var property in source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (!ContainsOwnerHint(property.Name))
            {
                continue;
            }

            CCSPlayerController? resolved;
            try
            {
                resolved = TryResolveControllerFromUnknown(property.GetValue(source));
            }
            catch
            {
                continue;
            }

            if (resolved != null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static bool ContainsOwnerHint(string value)
    {
        foreach (var hint in ControllerOwnerPropertyNameHints)
        {
            if (value.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private CCSPlayerController? TryResolveMappedInfernoAttacker(CTakeDamageInfo info)
    {
        if (!TryGetEntityIndex(info.Inflictor.Value, out var inflictorIndex) &&
            !TryGetEntityIndex(info.Ability.Value, out inflictorIndex))
        {
            return null;
        }

        if (!_infernoOwnerByEntityIndex.TryGetValue(inflictorIndex, out var ownerSteamId))
        {
            return null;
        }

        var owner = Utilities.GetPlayerFromSteamId64(ownerSteamId);
        return owner != null && owner.IsValid ? owner : null;
    }

    private static bool TryGetEntityIndex(CEntityInstance? entity, out int index)
    {
        index = 0;
        if (entity == null || !entity.IsValid)
        {
            return false;
        }

        if (entity is CBaseEntity baseEntity)
        {
            index = (int)baseEntity.Index;
            return index > 0;
        }

        if (TryReadIntProperty(entity, "Index", out var numericIndex) && numericIndex > 0)
        {
            index = numericIndex;
            return true;
        }

        return false;
    }

    private static int? TryResolveAttackerTeamNum(CTakeDamageInfo info)
    {
        var teamNum = TryResolveTeamNumFromUnknown(info.Attacker.Value);
        if (teamNum.HasValue)
        {
            return teamNum;
        }

        teamNum = TryResolveTeamNumFromUnknown(info.Inflictor.Value);
        if (teamNum.HasValue)
        {
            return teamNum;
        }

        return TryResolveTeamNumFromUnknown(info.Ability.Value);
    }

    private static bool IsFriendlyFireByRatio(CTakeDamageInfo info)
        => info.FriendlyFireDamageReductionRatio > 0.0001f;

    private static bool IsBurnDamage(CTakeDamageInfo info)
    {
        var damageType = info.BitsDamageType;
        return (damageType & DamageTypes_t.DMG_BURN) == DamageTypes_t.DMG_BURN;
    }

    private static int? TryResolveTeamNumFromUnknown(object? value, int depth = 0)
    {
        if (value == null || depth > 6)
        {
            return null;
        }

        switch (value)
        {
            case CCSPlayerController controller:
                return controller.TeamNum;
            case CBasePlayerPawn pawn:
                return pawn.TeamNum;
            case CEntityInstance entity when entity.IsValid:
            {
                if (TryReadIntProperty(entity, "TeamNum", out var entityTeamNum) && entityTeamNum > 0)
                {
                    return entityTeamNum;
                }

                foreach (var propertyName in ControllerOwnerPropertyCandidates)
                {
                    var nested = TryResolveTeamNumFromProperty(entity, propertyName, depth + 1);
                    if (nested.HasValue)
                    {
                        return nested;
                    }
                }

                break;
            }
        }

        if (TryReadIntProperty(value, "TeamNum", out var teamNum) && teamNum > 0)
        {
            return teamNum;
        }

        var nestedValue = TryReadObjectProperty(value, "Value");
        if (!ReferenceEquals(nestedValue, value))
        {
            var nestedTeamNum = TryResolveTeamNumFromUnknown(nestedValue, depth + 1);
            if (nestedTeamNum.HasValue)
            {
                return nestedTeamNum;
            }
        }

        return null;
    }

    private static int? TryResolveTeamNumFromProperty(object source, string propertyName, int depth)
    {
        var nested = TryReadObjectProperty(source, propertyName);
        if (nested == null || ReferenceEquals(nested, source))
        {
            return null;
        }

        return TryResolveTeamNumFromUnknown(nested, depth);
    }

    private static object? TryReadObjectProperty(object source, string propertyName)
    {
        var property = source.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (property == null || !property.CanRead || property.GetIndexParameters().Length > 0)
        {
            return null;
        }

        try
        {
            return property.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static void SuppressFriendlyFireDamage(CTakeDamageInfo info)
    {
        info.Damage = 0f;
        info.TotalledDamage = 0f;

        foreach (var propertyName in DamageInfoZeroPropertyCandidates)
        {
            TrySetNumericProperty(info, propertyName, 0d);
        }
    }

    private static int GetReverseDamageAmount(CTakeDamageInfo info)
    {
        var damage = 0f;
        foreach (var propertyName in ReverseDamagePropertyCandidates)
        {
            var candidate = propertyName switch
            {
                "Damage" => info.Damage,
                "TotalledDamage" => info.TotalledDamage,
                _ => ReadDamageInfoFloat(info, propertyName)
            };

            if (float.IsFinite(candidate) && candidate > damage)
            {
                damage = candidate;
            }
        }

        return (int)MathF.Ceiling(Math.Max(0f, damage));
    }

    private static float ReadDamageInfoFloat(CTakeDamageInfo info, string propertyName)
    {
        var property = typeof(CTakeDamageInfo).GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property == null)
        {
            return 0f;
        }

        var value = property.GetValue(info);
        return value switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            _ => 0f
        };
    }

    private bool TryApplyReverseDamage(
        CCSPlayerController attacker,
        int damage,
        out int appliedDamage,
        bool preserveOneHp = false)
    {
        appliedDamage = 0;

        if (!attacker.PawnIsAlive)
        {
            return false;
        }

        var attackerPawn = attacker.PlayerPawn?.Value;
        if (attackerPawn == null || !attackerPawn.IsValid)
        {
            return false;
        }

        var currentHealth = attackerPawn.Health;
        if (currentHealth <= 0)
        {
            return false;
        }

        var minimumHealth = preserveOneHp ? 1 : 0;
        var remainingHealth = Math.Max(minimumHealth, currentHealth - damage);
        appliedDamage = Math.Max(0, currentHealth - remainingHealth);
        if (appliedDamage <= 0)
        {
            return false;
        }

        if (remainingHealth > 0)
        {
            attackerPawn.Health = remainingHealth;
            Utilities.SetStateChanged(attackerPawn, "CBaseEntity", "m_iHealth");
            return true;
        }

        attacker.CommitSuicide(false, true);
        return true;
    }

    private static void RestoreFriendlyFireVictimHealth(CCSPlayerController victim, int amount)
    {
        if (amount <= 0 || !victim.PawnIsAlive)
        {
            return;
        }

        var pawn = victim.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        var current = pawn.Health;
        if (current <= 0)
        {
            return;
        }

        var max = GetPlayerMaxHealth(victim);
        var restored = current + amount;
        if (max > 0)
        {
            restored = Math.Min(restored, max);
        }

        if (restored == current)
        {
            return;
        }

        pawn.Health = restored;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    private static int GetEventArmorDamage(EventPlayerHurt @event)
    {
        return TryReadIntProperty(@event, "DmgArmor", out var dmgArmor) && dmgArmor > 0
            ? dmgArmor
            : TryReadIntProperty(@event, "DamageArmor", out var damageArmor) && damageArmor > 0
                ? damageArmor
                : 0;
    }

    private static void RestoreFriendlyFireVictimArmor(CCSPlayerController victim, int amount)
    {
        if (amount <= 0 || !IsValidAlivePlayer(victim))
        {
            return;
        }

        var currentArmor = GetPlayerArmor(victim);
        if (currentArmor < 0)
        {
            return;
        }

        var restoredArmor = Math.Min(100, currentArmor + amount);
        if (restoredArmor <= currentArmor)
        {
            return;
        }

        if (TrySetIntProperty(victim, "PawnArmor", restoredArmor))
        {
            TrySetStateChanged(victim, "CCSPlayerController", "m_iPawnArmor");
        }

        var pawn = victim.PlayerPawn?.Value;
        if (pawn != null && pawn.IsValid && TrySetIntProperty(pawn, "ArmorValue", restoredArmor))
        {
            TrySetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        }
    }

    private static void RestoreFriendlyFireVictimState(CCSPlayerController victim, int targetHealth, int targetArmor)
    {
        if (!IsValidAlivePlayer(victim))
        {
            return;
        }

        var pawn = victim.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        if (targetHealth > 0)
        {
            var max = GetPlayerMaxHealth(victim);
            var clampedHealth = max > 0 ? Math.Min(targetHealth, max) : targetHealth;
            if (clampedHealth > 0 && pawn.Health != clampedHealth)
            {
                pawn.Health = clampedHealth;
                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
            }
        }

        if (targetArmor >= 0)
        {
            var clampedArmor = Math.Clamp(targetArmor, 0, 100);
            if (TrySetIntProperty(victim, "PawnArmor", clampedArmor))
            {
                TrySetStateChanged(victim, "CCSPlayerController", "m_iPawnArmor");
            }

            if (TrySetIntProperty(pawn, "ArmorValue", clampedArmor))
            {
                TrySetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
            }
        }
    }

    private static bool ShouldSoftRestoreHealth(int currentHealth, int targetHealth)
    {
        if (currentHealth <= 0 || targetHealth <= 0 || currentHealth >= targetHealth)
        {
            return false;
        }

        var delta = targetHealth - currentHealth;
        return delta > 0 && delta <= 4;
    }

    private void EnforceVictimStateSoftNextFrames(
        CCSPlayerController victim,
        string grenadeKey,
        int targetHealth,
        int targetArmor,
        bool hasKnownAttacker)
    {
        if (!IsContinuousFireGrenade(grenadeKey))
        {
            return;
        }

        if (targetHealth <= 0 && targetArmor < 0)
        {
            return;
        }

        var victimId = victim.SteamID;
        EnforceVictimStateSoftNextFrame(victimId, grenadeKey, targetHealth, targetArmor, framesRemaining: 3, hasKnownAttacker);
    }

    private void EnforceVictimStateSoftNextFrame(
        ulong victimId,
        string grenadeKey,
        int targetHealth,
        int targetArmor,
        int framesRemaining,
        bool hasKnownAttacker)
    {
        if (framesRemaining <= 0)
        {
            return;
        }

        Server.NextFrame(() =>
        {
            var victimNow = FindPlayerBySteamId(victimId);
            if (victimNow == null || !IsValidAlivePlayer(victimNow))
            {
                return;
            }

            var currentHealth = GetPlayerHealth(victimNow);
            var currentArmor = GetPlayerArmor(victimNow);
            var shouldRestoreHealth = ShouldSoftRestoreHealth(currentHealth, targetHealth);
            var shouldRestoreArmor =
                targetArmor >= 0 &&
                currentArmor >= 0 &&
                currentArmor < targetArmor &&
                (targetArmor - currentArmor) <= 4;
            if (shouldRestoreHealth || shouldRestoreArmor)
            {
                var restoreHealth = shouldRestoreHealth ? targetHealth : currentHealth;
                var restoreArmor = shouldRestoreArmor ? targetArmor : currentArmor;
                RestoreFriendlyFireVictimState(victimNow, restoreHealth, restoreArmor);

                if (Config.DebugDamageTicks)
                {
                    Logger.LogInformation(
                        "FF tick soft-enforce: tick={Tick} victim={Victim} grenade={Grenade} health={HealthBefore}->{HealthAfter} armor={ArmorBefore}->{ArmorAfter} knownAttacker={KnownAttacker}",
                        Server.TickCount,
                        victimNow.PlayerName,
                        grenadeKey,
                        currentHealth,
                        GetPlayerHealth(victimNow),
                        currentArmor,
                        GetPlayerArmor(victimNow),
                        hasKnownAttacker);
                }
            }

            EnforceVictimStateSoftNextFrame(
                victimId,
                grenadeKey,
                targetHealth,
                targetArmor,
                framesRemaining - 1,
                hasKnownAttacker);
        });
    }

    private void EnforceVictimNoLossNextFrame(
        CCSPlayerController attacker,
        CCSPlayerController victim,
        string grenadeKey,
        int victimHealthBefore,
        int victimArmorBefore)
    {
        if (!IsContinuousFireGrenade(grenadeKey))
        {
            return;
        }

        var attackerId = attacker.SteamID;
        var victimId = victim.SteamID;
        var attackerName = attacker.PlayerName;

        Server.NextFrame(() =>
        {
            var victimNow = FindPlayerBySteamId(victimId);
            if (victimNow == null || !IsValidAlivePlayer(victimNow))
            {
                return;
            }

            var attackerNow = FindPlayerBySteamId(attackerId);
            if (attackerNow != null &&
                IsValidPlayer(attackerNow, Config.DebugTKReverse) &&
                attackerNow.TeamNum != victimNow.TeamNum)
            {
                return;
            }

            var currentHealth = GetPlayerHealth(victimNow);
            var currentArmor = GetPlayerArmor(victimNow);
            var shouldRestoreHealth = victimHealthBefore > 0 && currentHealth > 0 && currentHealth < victimHealthBefore;
            var shouldRestoreArmor = victimArmorBefore >= 0 && currentArmor >= 0 && currentArmor < victimArmorBefore;
            if (!shouldRestoreHealth && !shouldRestoreArmor)
            {
                return;
            }

            var targetHealth = shouldRestoreHealth ? victimHealthBefore : currentHealth;
            var targetArmor = shouldRestoreArmor ? victimArmorBefore : currentArmor;
            RestoreFriendlyFireVictimState(victimNow, targetHealth, targetArmor);

            if (Config.DebugDamageTicks)
            {
                Logger.LogInformation(
                    "FF tick enforce: tick={Tick} attacker={Attacker} victim={Victim} grenade={Grenade} health={HealthBefore}->{HealthAfter} armor={ArmorBefore}->{ArmorAfter}",
                    Server.TickCount,
                    attackerName,
                    victimNow.PlayerName,
                    grenadeKey,
                    currentHealth,
                    GetPlayerHealth(victimNow),
                    currentArmor,
                    GetPlayerArmor(victimNow));
            }
        });
    }

    private void EnforceVictimNoLossNextFrame(
        CCSPlayerController victim,
        string grenadeKey,
        int victimHealthBefore,
        int victimArmorBefore)
    {
        if (!IsContinuousFireGrenade(grenadeKey))
        {
            return;
        }

        var victimId = victim.SteamID;

        Server.NextFrame(() =>
        {
            var victimNow = FindPlayerBySteamId(victimId);
            if (victimNow == null || !IsValidAlivePlayer(victimNow))
            {
                return;
            }

            var currentHealth = GetPlayerHealth(victimNow);
            var currentArmor = GetPlayerArmor(victimNow);
            var shouldRestoreHealth = victimHealthBefore > 0 && currentHealth > 0 && currentHealth < victimHealthBefore;
            var shouldRestoreArmor = victimArmorBefore >= 0 && currentArmor >= 0 && currentArmor < victimArmorBefore;
            if (!shouldRestoreHealth && !shouldRestoreArmor)
            {
                return;
            }

            var targetHealth = shouldRestoreHealth ? victimHealthBefore : currentHealth;
            var targetArmor = shouldRestoreArmor ? victimArmorBefore : currentArmor;
            RestoreFriendlyFireVictimState(victimNow, targetHealth, targetArmor);

            if (Config.DebugDamageTicks)
            {
                Logger.LogInformation(
                    "FF tick enforce (unknown attacker): tick={Tick} victim={Victim} grenade={Grenade} health={HealthBefore}->{HealthAfter} armor={ArmorBefore}->{ArmorAfter}",
                    Server.TickCount,
                    victimNow.PlayerName,
                    grenadeKey,
                    currentHealth,
                    GetPlayerHealth(victimNow),
                    currentArmor,
                    GetPlayerArmor(victimNow));
            }
        });
    }

    private static int GetPlayerArmor(CCSPlayerController player)
    {
        if (TryReadIntProperty(player, "PawnArmor", out var pawnArmor) && pawnArmor >= 0)
        {
            return pawnArmor;
        }

        var pawn = player.PlayerPawn?.Value;
        if (pawn != null && pawn.IsValid &&
            TryReadIntProperty(pawn, "ArmorValue", out var armorValue) && armorValue >= 0)
        {
            return armorValue;
        }

        return -1;
    }

    private static CCSPlayerController? FindPlayerBySteamId(ulong steamId)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player != null && player.IsValid && player.SteamID == steamId)
            {
                return player;
            }
        }

        return null;
    }

    private static bool TryReadIntProperty(object source, string propertyName, out int value)
    {
        value = 0;
        var property = source.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property == null || !property.CanRead)
        {
            return false;
        }

        var raw = property.GetValue(source);
        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case short s:
                value = s;
                return true;
            case ushort us:
                value = us;
                return true;
            case byte b:
                value = b;
                return true;
            case sbyte sb:
                value = sb;
                return true;
            case long l:
                value = (int)l;
                return true;
            case uint ui:
                value = (int)ui;
                return true;
            case float f:
                value = (int)MathF.Round(f);
                return true;
            case double d:
                value = (int)Math.Round(d);
                return true;
            default:
                return false;
        }
    }

    private static bool TrySetNumericProperty(object source, string propertyName, double value)
    {
        var property = source.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property == null || !property.CanWrite)
        {
            return false;
        }

        var type = property.PropertyType;
        object converted;
        if (type == typeof(float))
        {
            converted = (float)value;
        }
        else if (type == typeof(double))
        {
            converted = value;
        }
        else if (type == typeof(int))
        {
            converted = (int)Math.Round(value);
        }
        else if (type == typeof(short))
        {
            converted = (short)Math.Round(value);
        }
        else if (type == typeof(ushort))
        {
            converted = (ushort)Math.Max(0, Math.Round(value));
        }
        else if (type == typeof(byte))
        {
            converted = (byte)Math.Clamp((int)Math.Round(value), byte.MinValue, byte.MaxValue);
        }
        else if (type == typeof(sbyte))
        {
            converted = (sbyte)Math.Clamp((int)Math.Round(value), sbyte.MinValue, sbyte.MaxValue);
        }
        else if (type == typeof(long))
        {
            converted = (long)Math.Round(value);
        }
        else if (type == typeof(uint))
        {
            converted = (uint)Math.Max(0d, Math.Round(value));
        }
        else
        {
            return false;
        }

        property.SetValue(source, converted);
        return true;
    }

    private static bool TrySetIntProperty(object source, string propertyName, int value)
    {
        var property = source.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property == null || !property.CanWrite)
        {
            return false;
        }

        var type = property.PropertyType;
        object converted;
        if (type == typeof(int))
        {
            converted = value;
        }
        else if (type == typeof(short))
        {
            converted = (short)value;
        }
        else if (type == typeof(ushort))
        {
            converted = (ushort)Math.Max(0, value);
        }
        else if (type == typeof(byte))
        {
            converted = (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);
        }
        else if (type == typeof(sbyte))
        {
            converted = (sbyte)Math.Clamp(value, sbyte.MinValue, sbyte.MaxValue);
        }
        else if (type == typeof(long))
        {
            converted = (long)value;
        }
        else if (type == typeof(uint))
        {
            converted = (uint)Math.Max(0, value);
        }
        else
        {
            return false;
        }

        property.SetValue(source, converted);
        return true;
    }

    private static bool IsValidAlivePlayer(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || !player.PawnIsAlive)
        {
            return false;
        }

        var pawn = player.PlayerPawn?.Value;
        return pawn != null && pawn.IsValid;
    }

    private static void TrySetStateChanged(CBaseEntity entity, string className, string fieldName)
    {
        try
        {
            Utilities.SetStateChanged(entity, className, fieldName);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static int GetPlayerMaxHealth(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return 100;
        }

        var maxHealthProperty = pawn.GetType().GetProperty(
            "MaxHealth",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (maxHealthProperty?.GetValue(pawn) is int maxHealth && maxHealth > 0)
        {
            return maxHealth;
        }

        return 100;
    }

    private void SendReverseDamageWarning(CCSPlayerController attacker, CCSPlayerController victim, string grenadeKey, int damage)
    {
        if (!ShouldSendReverseDamageWarning(attacker.SteamID, grenadeKey))
        {
            return;
        }

        var template = GetReverseDamageWarningTemplate();
        var grenadeDisplay = GetGrenadeDisplay(grenadeKey);
        var message = ApplyTemplate(
            template,
            attacker,
            victim,
            grenadeDisplay,
            grenadeKey,
            GetPlayerHealth(victim),
            GetCount(_grenadeTkCount, attacker.SteamID),
            GetCount(_grenadesThrown, attacker.SteamID),
            damage,
            GetReverseIncidentCount(GetCount(_grenadeTkCount, attacker.SteamID)));
        attacker.PrintToChat(message);
    }

    private void SendToAdmins(string message)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsValidPlayer(player))
            {
                continue;
            }

            if (!IsAdmin(player))
            {
                continue;
            }

            player.PrintToChat(message);
        }
    }

    private void SendDebugToAttacker(CCSPlayerController attacker, string message)
    {
        if (!Config.DebugShowOwnTkToAttacker)
        {
            return;
        }

        if (!IsValidPlayer(attacker))
        {
            return;
        }

        if (!Config.DebugShowOwnTkToAttackerEvenIfAdmin && IsAdmin(attacker))
        {
            return;
        }

        attacker.PrintToChat(message);
    }

    private bool IsAdmin(CCSPlayerController player)
    {
        if (string.IsNullOrWhiteSpace(Config.AdminPermission))
        {
            return true;
        }

        return AdminManager.PlayerHasPermissions(player, Config.AdminPermission);
    }

    private bool IsValidPlayer(CCSPlayerController? player, bool allowBots = false)
    {
        if (player == null || !player.IsValid)
        {
            return false;
        }

        if (!allowBots && player.IsBot)
        {
            return false;
        }

        return !player.IsHLTV;
    }

    private void LogToServer(string message)
    {
        Logger.LogInformation("{Message}", SanitizeForLog(message));
    }

    private static string SanitizeForLog(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(message.Length);
        for (var i = 0; i < message.Length; i++)
        {
            var ch = message[i];
            if (ch == '\x07')
            {
                if (i + 6 < message.Length && IsHex(message.Substring(i + 1, 6)))
                {
                    i += 6;
                }

                continue;
            }

            if (char.IsControl(ch))
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
