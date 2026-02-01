using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace AbsynthiumGrenadeManager;

public sealed class GrenadeManagerConfig : BasePluginConfig
{
    public override int Version { get; set; } = 3;

    public string AdminPermission { get; set; } = "@css/ban";

    public string LogFileName { get; set; } = "grenade_tk.log";

    public bool DebugShowOwnTkToAttacker { get; set; } = false;
    public bool DebugShowOwnTkToAttackerEvenIfAdmin { get; set; } = false;
    public bool DebugIncludeSelfDamage { get; set; } = false;

    public double FriendlyFireMessageCooldownSeconds { get; set; } = 5;

    public string GrenadeRadioCvar { get; set; } = "sv_ignoregrenaderadio";

    public bool GrenadeThrowMessageEnabled { get; set; } = false;
    public string GrenadeThrowMessageScope { get; set; } = "team";
    public string GrenadeThrowMessageTemplate { get; set; } =
        "[{team_short}] {attacker} @{place} : {grenade} !";
    public Dictionary<string, string> GrenadeThrowMessageByGrenade { get; set; } = new();

    public string FriendlyFireMessageTemplate { get; set; } =
        "{attacker} a lance {grenade} sur \"{victim}\", il lui reste {hp} : (Nombre de TK grenade : {tk_count})";

    public Dictionary<string, string> FriendlyFireMessageByGrenade { get; set; } = new()
    {
        // Example override per grenade type:
        // ["hegrenade"] = "{attacker} a lance {grenade} sur \"{victim}\", il lui reste {hp}"
    };

    public Dictionary<string, string> GrenadeDisplayNames { get; set; } = new()
    {
        ["hegrenade"] = "Grenade explosive",
        ["molotov"] = "Molotov",
        ["incgrenade"] = "Incendiaire",
        ["flashbang"] = "Flashbang",
        ["smokegrenade"] = "Fumigene",
        ["decoy"] = "Leurre"
    };
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
    private string _logFilePath = string.Empty;

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

    public override void Load(bool hotReload)
    {
        EnsureLogFilePath();
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        ScheduleGrenadeRadioDisable();
    }

    public void OnConfigParsed(GrenadeManagerConfig config)
    {
        Config = config ?? new GrenadeManagerConfig();
        Config.FriendlyFireMessageByGrenade = NormalizeDictionary(Config.FriendlyFireMessageByGrenade);
        Config.GrenadeDisplayNames = NormalizeDictionary(Config.GrenadeDisplayNames);
        Config.GrenadeThrowMessageByGrenade = NormalizeDictionary(Config.GrenadeThrowMessageByGrenade);
        EnsureLogFilePath();
        ScheduleGrenadeRadioDisable();
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !IsValidPlayer(player))
        {
            return HookResult.Continue;
        }

        var grenadeKey = NormalizeGrenadeKey(@event.Weapon);
        if (string.IsNullOrWhiteSpace(grenadeKey) || !GrenadeKeys.Contains(grenadeKey))
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

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;

        if (attacker == null || victim == null)
        {
            return HookResult.Continue;
        }

        if (!IsValidPlayer(attacker) || !IsValidPlayer(victim))
        {
            return HookResult.Continue;
        }

        var isSelf = attacker == victim;
        if (isSelf && !Config.DebugIncludeSelfDamage)
        {
            return HookResult.Continue;
        }

        if (!isSelf && attacker.TeamNum != victim.TeamNum)
        {
            return HookResult.Continue;
        }

        if (@event.DmgHealth <= 0)
        {
            return HookResult.Continue;
        }

        var grenadeKey = NormalizeGrenadeKey(@event.Weapon);
        if (string.IsNullOrWhiteSpace(grenadeKey) || !DamageGrenadeKeys.Contains(grenadeKey))
        {
            return HookResult.Continue;
        }

        var tkCount = isSelf
            ? GetCount(_grenadeTkCount, attacker.SteamID)
            : IncrementCount(_grenadeTkCount, attacker.SteamID);
        var thrownCount = GetCount(_grenadesThrown, attacker.SteamID);

        if (!ShouldSendFriendlyFireMessage(attacker.SteamID, victim.SteamID, grenadeKey))
        {
            return HookResult.Continue;
        }

        var grenadeDisplay = GetGrenadeDisplay(grenadeKey);
        var template = GetFriendlyFireTemplate(grenadeKey);
        var remainingHp = @event.Health;

        var message = ApplyTemplate(template, attacker, victim, grenadeDisplay, grenadeKey, remainingHp, tkCount, thrownCount);

        SendToAdmins(message);
        SendDebugToAttacker(attacker, message);
        LogToServerAndFile(message);

        return HookResult.Continue;
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

    private void EnsureLogFilePath()
    {
        var baseDir = ModuleDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Directory.GetCurrentDirectory();
        }

        var cssRoot = FindCounterStrikeSharpRoot(baseDir) ?? baseDir;
        var logDir = Path.Combine(cssRoot, "logs");
        Directory.CreateDirectory(logDir);

        var fileName = string.IsNullOrWhiteSpace(Config.LogFileName) ? "grenade_tk.log" : Config.LogFileName;
        _logFilePath = Path.Combine(logDir, fileName);
    }

    private static string? FindCounterStrikeSharpRoot(string startDir)
    {
        var current = new DirectoryInfo(startDir);
        while (current != null)
        {
            if (current.Name.Equals("counterstrikesharp", StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private void OnMapStart(string mapName)
        => ScheduleGrenadeRadioDisable();

    private static string NormalizeGrenadeKey(string weapon)
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

        if (key == "inferno")
        {
            return "molotov";
        }

        if (key == "incendiary")
        {
            return "incgrenade";
        }

        return key;
    }

    private string GetGrenadeDisplay(string grenadeKey)
    {
        if (Config.GrenadeDisplayNames.TryGetValue(grenadeKey, out var display) &&
            !string.IsNullOrWhiteSpace(display))
        {
            return display;
        }

        return grenadeKey;
    }

    private string GetFriendlyFireTemplate(string grenadeKey)
    {
        if (Config.FriendlyFireMessageByGrenade.TryGetValue(grenadeKey, out var template) &&
            !string.IsNullOrWhiteSpace(template))
        {
            return template;
        }

        return Config.FriendlyFireMessageTemplate;
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

    private bool ShouldSendFriendlyFireMessage(ulong attackerId, ulong victimId, string grenadeKey)
    {
        var cooldownSeconds = Config.FriendlyFireMessageCooldownSeconds;
        if (cooldownSeconds <= 0)
        {
            return true;
        }

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

    private string ApplyTemplate(
        string template,
        CCSPlayerController attacker,
        CCSPlayerController? victim,
        string grenadeDisplay,
        string grenadeKey,
        int remainingHp,
        int tkCount,
        int thrownCount)
    {
        var result = template ?? string.Empty;

        result = result.Replace("{attacker}", attacker.PlayerName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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

        return ApplyColorMarkup(result, attacker, victim);
    }

    private static string ApplyColorMarkup(string message, CCSPlayerController attacker, CCSPlayerController? victim)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message ?? string.Empty;
        }

        var normalized = NormalizeColorTags(message);

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
        if (Config.GrenadeThrowMessageByGrenade.TryGetValue(grenadeKey, out var template) &&
            !string.IsNullOrWhiteSpace(template))
        {
            return template;
        }

        return Config.GrenadeThrowMessageTemplate;
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
            "team" => recipient.TeamNum == thrower.TeamNum,
            "admins" => IsAdmin(recipient),
            "attacker" => recipient == thrower,
            _ => recipient.TeamNum == thrower.TeamNum
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

    private bool IsValidPlayer(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid)
        {
            return false;
        }

        if (player.IsBot)
        {
            return false;
        }

        return !player.IsHLTV;
    }

    private void LogToServerAndFile(string message)
    {
        Logger.LogInformation("{Message}", message);

        if (string.IsNullOrWhiteSpace(_logFilePath))
        {
            return;
        }

        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            File.AppendAllText(_logFilePath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to write grenade TK log file.");
        }
    }
}
