using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared.Database;
using Content.Shared.Players;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Roles;
using Microsoft.CodeAnalysis;
using Content.Shared.CCVar;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Content.Server.Administration.Managers;
using Content.Server.GameTicking;
using Content.Server.Players;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using JetBrains.Annotations;
using Robust.Server.Player;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Utility;

namespace Content.Server.Administration.Managers;

public sealed class BanManager : IBanManager, IPostInjectInit
{
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ILocalizationManager _localizationManager = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly INetManager _netManager = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;

    private ISawmill _sawmill = default!;
    private readonly HttpClient _httpClient = new();

    public const string SawmillId = "admin.bans";
    public const string JobPrefix = "Job:";
    // SpaceStories ban track - start
    private string _webhookUrl = string.Empty;
    private WebhookData? _webhookData;
    private string _webhookName = "Ban Machine";
    private string _webhookAvatarUrl = "https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcQCSDvjudwZh_G5qrZI5OrDNMLMmYzNBLYWP3Tl_cS_zQ&s";
    // SpaceStories ban track - end

    private readonly Dictionary<NetUserId, HashSet<ServerRoleBanDef>> _cachedRoleBans = new();

    public void Initialize()
    {
        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;

        _netManager.RegisterNetMessage<MsgRoleBans>();
        _config.OnValueChanged(CCVars.DiscordBanWebhook, OnWebhookChanged, true); // SpaceStories ban track
    }

    private async void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        if (e.NewStatus != SessionStatus.Connected
            || _cachedRoleBans.ContainsKey(e.Session.UserId))
            return;

        var netChannel = e.Session.ConnectedClient;
        await CacheDbRoleBans(e.Session.UserId, netChannel.RemoteEndPoint.Address, netChannel.UserData.HWId.Length == 0 ? null : netChannel.UserData.HWId);
    }

    private async Task<bool> AddRoleBan(ServerRoleBanDef banDef)
    {
        if (banDef.UserId != null)
        {
            if (!_cachedRoleBans.TryGetValue(banDef.UserId.Value, out var roleBans))
            {
                roleBans = new HashSet<ServerRoleBanDef>();
                _cachedRoleBans.Add(banDef.UserId.Value, roleBans);
            }
            if (!roleBans.Contains(banDef))
                roleBans.Add(banDef);
        }

        await _db.AddServerRoleBanAsync(banDef);
        return true;
    }

    public HashSet<string>? GetRoleBans(NetUserId playerUserId)
    {
        return _cachedRoleBans.TryGetValue(playerUserId, out var roleBans) ? roleBans.Select(banDef => banDef.Role).ToHashSet() : null;
    }

    private async Task CacheDbRoleBans(NetUserId userId, IPAddress? address = null, ImmutableArray<byte>? hwId = null)
    {
        var roleBans = await _db.GetServerRoleBansAsync(address, userId, hwId, false);

        var userRoleBans = new HashSet<ServerRoleBanDef>();
        foreach (var ban in roleBans)
        {
            userRoleBans.Add(ban);
        }

        _cachedRoleBans[userId] = userRoleBans;
    }

    public void Restart()
    {
        // Clear out players that have disconnected.
        var toRemove = new List<NetUserId>();
        foreach (var player in _cachedRoleBans.Keys)
        {
            if (!_playerManager.TryGetSessionById(player, out _))
                toRemove.Add(player);
        }

        foreach (var player in toRemove)
        {
            _cachedRoleBans.Remove(player);
        }

        // Check for expired bans
        foreach (var roleBans in _cachedRoleBans.Values)
        {
            roleBans.RemoveWhere(ban => DateTimeOffset.Now > ban.ExpirationTime);
        }
    }

    #region Server Bans
    public async void CreateServerBan(NetUserId? target, string? targetUsername, NetUserId? banningAdmin, (IPAddress, int)? addressRange, ImmutableArray<byte>? hwid, uint? minutes, NoteSeverity severity, string reason)
    {
        DateTimeOffset? expires = null;
        if (minutes > 0)
        {
            expires = DateTimeOffset.Now + TimeSpan.FromMinutes(minutes.Value);
        }

        _systems.TryGetEntitySystem<GameTicker>(out var ticker);
        int? roundId = ticker == null || ticker.RoundId == 0 ? null : ticker.RoundId;
        var playtime = target == null ? TimeSpan.Zero : (await _db.GetPlayTimes(target.Value)).Find(p => p.Tracker == PlayTimeTrackingShared.TrackerOverall)?.TimeSpent ?? TimeSpan.Zero;

        var banDef = new ServerBanDef(
            null,
            target,
            addressRange,
            hwid,
            DateTimeOffset.Now,
            expires,
            roundId,
            playtime,
            reason,
            severity,
            banningAdmin,
            null);

        await _db.AddServerBanAsync(banDef);
        var adminName = banningAdmin == null
            ? Loc.GetString("system-user")
            : (await _db.GetPlayerRecordByUserId(banningAdmin.Value))?.LastSeenUserName ?? Loc.GetString("system-user");
        var targetName = target is null ? "null" : $"{targetUsername} ({target})";
        var addressRangeString = addressRange != null
            ? $"{addressRange.Value.Item1}/{addressRange.Value.Item2}"
            : "null";
        var hwidString = hwid != null
            ? string.Concat(hwid.Value.Select(x => x.ToString("x2")))
            : "null";
        var expiresString = expires == null ? Loc.GetString("server-ban-string-never") : $"{expires}";
        var TimeString = minutes == null ? Loc.GetString("server-ban-string-infinity") : $"{TimeSpan.FromMinutes(minutes.Value)}";

        var key = _cfg.GetCVar(CCVars.AdminShowPIIOnBan) ? "server-ban-string" : "server-ban-string-no-pii";

        var logMessage = Loc.GetString(
            key,
            ("admin", adminName),
            ("severity", severity),
            ("expires", expiresString),
            ("name", targetName),
            ("ip", addressRangeString),
            ("hwid", hwidString),
            ("reason", reason));

        _sawmill.Info(logMessage);
        _chat.SendAdminAlert(logMessage);
        SendWebhook(await GenerateBanPayload(await _db.GetServerBansAsync(null, target, null), minutes));
        // If we're not banning a player we don't care about disconnecting people
        if (target == null)
            return;

        // Is the player connected?
        if (!_playerManager.TryGetSessionById(target.Value, out var targetPlayer))
            return;
        // If they are, kick them
        var message = banDef.FormatBanMessage(_cfg, _localizationManager);
        targetPlayer.ConnectedClient.Disconnect(message);
    }
    #endregion

    #region Job Bans
    // If you are trying to remove timeOfBan, please don't. It's there because the note system groups role bans by time, reason and banning admin.
    // Removing it will clutter the note list. Please also make sure that department bans are applied to roles with the same DateTimeOffset.
    public async void CreateRoleBan(NetUserId? target, string? targetUsername, NetUserId? banningAdmin, (IPAddress, int)? addressRange, ImmutableArray<byte>? hwid, string role, uint? minutes, NoteSeverity severity, string reason, DateTimeOffset timeOfBan)
    {
        if (!_prototypeManager.TryIndex(role, out JobPrototype? _))
        {
            throw new ArgumentException($"Invalid role '{role}'", nameof(role));
        }

        role = string.Concat(JobPrefix, role);
        DateTimeOffset? expires = null;
        if (minutes > 0)
        {
            expires = DateTimeOffset.Now + TimeSpan.FromMinutes(minutes.Value);
        }

        _systems.TryGetEntitySystem(out GameTicker? ticker);
        int? roundId = ticker == null || ticker.RoundId == 0 ? null : ticker.RoundId;
        var playtime = target == null ? TimeSpan.Zero : (await _db.GetPlayTimes(target.Value)).Find(p => p.Tracker == PlayTimeTrackingShared.TrackerOverall)?.TimeSpent ?? TimeSpan.Zero;

        var banDef = new ServerRoleBanDef(
            null,
            target,
            addressRange,
            hwid,
            timeOfBan,
            expires,
            roundId,
            playtime,
            reason,
            severity,
            banningAdmin,
            null,
            role);

        if (!await AddRoleBan(banDef))
        {
            _chat.SendAdminAlert(Loc.GetString("cmd-roleban-existing", ("target", targetUsername ?? "null"), ("role", role)));
            return;
        }

        SendWebhook(await GenerateJobBanPayload(banDef, minutes));

        var length = expires == null ? Loc.GetString("cmd-roleban-inf") : Loc.GetString("cmd-roleban-until", ("expires", expires));
        _chat.SendAdminAlert(Loc.GetString("cmd-roleban-success", ("target", targetUsername ?? "null"), ("role", role), ("reason", reason), ("length", length)));

        if (target != null)
        {
            SendRoleBans(target.Value);
        }
    }

    public HashSet<string>? GetJobBans(NetUserId playerUserId)
    {
        if (!_cachedRoleBans.TryGetValue(playerUserId, out var roleBans))
            return null;
        return roleBans
            .Where(ban => ban.Role.StartsWith(JobPrefix, StringComparison.Ordinal))
            .Select(ban => ban.Role[JobPrefix.Length..])
            .ToHashSet();
    }
    #endregion

    public void SendRoleBans(NetUserId userId)
    {
        if (!_playerManager.TryGetSessionById(userId, out var player))
        {
            return;
        }

        SendRoleBans(player);
    }

    public void SendRoleBans(IPlayerSession pSession)
    {
        if (!_cachedRoleBans.TryGetValue(pSession.UserId, out var roleBans))
        {
            _sawmill.Error($"Tried to send rolebans for {pSession.Name} but none cached?");
            return;
        }

        var bans = new MsgRoleBans()
        {
            Bans = roleBans.Select(o => o.Role).ToList()
        };

        _sawmill.Debug($"Sent rolebans to {pSession.Name}");
        _netManager.ServerSendMessage(bans, pSession.ConnectedClient);
    }

    public void PostInject()
    {
        _sawmill = _logManager.GetSawmill(SawmillId);
    }

    #region Webhook
    private async void SendWebhook(WebhookPayload payload)
    {
        if (_webhookUrl == string.Empty) return;

        var request = await _httpClient.PostAsync($"{_webhookUrl}?wait=true",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        var content = await request.Content.ReadAsStringAsync();
        if (!request.IsSuccessStatusCode)
        {
            _sawmill.Log(LogLevel.Error, $"Discord returned bad status code when posting message (perhaps the message is too long?): {request.StatusCode}\nResponse: {content}");
            return;
        }

        var id = JsonNode.Parse(content)?["id"];
        if (id == null)
        {
            _sawmill.Log(LogLevel.Error, $"Could not find id in json-content returned from discord webhook: {content}");
            return;
        }
    }
    private async Task<WebhookPayload> GenerateJobBanPayload(ServerRoleBanDef banDef, uint? minutes = null)
    {
        var adminName = banDef.BanningAdmin == null
            ? Loc.GetString("system-user")
            : (await _db.GetPlayerRecordByUserId(banDef.BanningAdmin.Value))?.LastSeenUserName ?? Loc.GetString("system-user");
        var targetName = banDef.UserId == null
            ? Loc.GetString("system-user")
            : (await _db.GetPlayerRecordByUserId(banDef.UserId.Value))?.LastSeenUserName ?? Loc.GetString("system-user");
        var expiresString = banDef.ExpirationTime == null ? Loc.GetString("server-ban-string-never") : $"{banDef.ExpirationTime}";
        var timeString = banDef.ExpirationTime == null || minutes == null ? Loc.GetString("server-ban-string-infinity") : Loc.GetString("server-time-ban-timestring", ("hours", minutes.Value / 60));
        var reason = banDef.Reason;
        var severity = banDef.Severity;
        //var id = banDef.Id;
        var round = banDef.RoundId;
        var role = banDef.Role;

        var color = severity switch
        {
            NoteSeverity.None => 0x808080,
            NoteSeverity.Minor => 0x394FCC,
            NoteSeverity.Medium => 0xC6A500,
            NoteSeverity.High => 0x8B0000,
            _ => throw new ArgumentOutOfRangeException(nameof(severity), $"{severity} was not matched."),
        };

        return new WebhookPayload
        {
            Username = _webhookName,
            AvatarUrl = _webhookAvatarUrl,
            Embeds = new List<Embed>
                {
                    new()
                    {
                        Description = Loc.GetString(
            "server-role-ban-string",
            ("round", $"{round}"),
            ("targetName", targetName),
            ("adminName", adminName),
            ("TimeNow", DateTimeOffset.Now),
            ("expiresString", expiresString),
            ("timeString", timeString),
            ("role", role),
            ("reason", reason)),
                        Color = color,
                        Author = new EmbedAuthor
                        {
                        Name = $"{Loc.GetString("server-role-ban")}",
                        IconUrl = "https://cdn.discordapp.com/emojis/1129749368199712829.webp?size=40&quality=lossless" // Смайлик бан хаммера. URL прямо из дискорд)
                        }
        },
                },
        };
    }
    private async Task<WebhookPayload> GenerateBanPayload(List<ServerBanDef> bans, uint? minutes = null)
    {
        var banDef = bans[bans.Count - 1]; // Без этого не получить ID бана, так что подумай, так ли плох этот костыль.
        var ban_type = banDef.ExpirationTime == null ? Loc.GetString("server-perma-ban") : Loc.GetString("server-time-ban");

        var adminName = banDef.BanningAdmin == null
            ? Loc.GetString("system-user")
            : (await _db.GetPlayerRecordByUserId(banDef.BanningAdmin.Value))?.LastSeenUserName ?? Loc.GetString("system-user");
        var targetName = banDef.UserId == null
            ? Loc.GetString("system-user")
            : (await _db.GetPlayerRecordByUserId(banDef.UserId.Value))?.LastSeenUserName ?? Loc.GetString("system-user");
        var expiresString = banDef.ExpirationTime == null ? Loc.GetString("server-ban-string-never") : $"{banDef.ExpirationTime}";
        var timeString = banDef.ExpirationTime == null || minutes == null ? Loc.GetString("server-ban-string-infinity") : Loc.GetString("server-time-ban-timestring", ("hours", minutes.Value / 60));
        var reason = banDef.Reason;
        var severity = banDef.Severity;
        var id = banDef.Id;
        var round = banDef.RoundId;

        var color = severity switch
        {
            NoteSeverity.None => 0x808080,
            NoteSeverity.Minor => 0x394FCC,
            NoteSeverity.Medium => 0xC6A500,
            NoteSeverity.High => 0x8B0000,
            _ => throw new ArgumentOutOfRangeException(nameof(severity), $"{severity} was not matched."),
        };

        return new WebhookPayload
        {
            Username = _webhookName,
            AvatarUrl = _webhookAvatarUrl,
            Embeds = new List<Embed>
                {
                    new()
                    {
                        Description = Loc.GetString(
            "server-time-ban-string",
            ("round", $"{round}"),
            ("targetName", targetName),
            ("adminName", adminName),
            ("TimeNow", DateTimeOffset.Now),
            ("expiresString", expiresString),
            ("timeString", timeString),
            ("reason", reason)),
                        Color = color,
                        Author = new EmbedAuthor
                        {
                        Name = $"{ban_type} #{id}",
                        IconUrl = "https://cdn.discordapp.com/emojis/1129749368199712829.webp?size=40&quality=lossless" // Смайлик бан хаммера. URL прямо из дискорд)
                        }
        },
                },
        };
    }
    private void OnWebhookChanged(string url)
    {
        _webhookUrl = url;

        if (url == string.Empty)
            return;

        // Basic sanity check and capturing webhook ID and token
        var match = Regex.Match(url, @"^https://discord\.com/api/webhooks/(\d+)/((?!.*/).*)$");

        if (!match.Success)
        {
            // TODO: Ideally, CVar validation during setting should be better integrated
            _sawmill.Warning("Webhook URL does not appear to be valid. Using anyways...");
            return;
        }

        if (match.Groups.Count <= 2)
        {
            _sawmill.Error("Could not get webhook ID or token.");
            return;
        }

        var webhookId = match.Groups[1].Value;
        var webhookToken = match.Groups[2].Value;

        // Fire and forget
        _ = SetWebhookData(webhookId, webhookToken);
    }
    private async Task SetWebhookData(string id, string token)
    {
        var response = await _httpClient.GetAsync($"https://discord.com/api/v10/webhooks/{id}/{token}");

        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _sawmill.Log(LogLevel.Error, $"Discord returned bad status code when trying to get webhook data (perhaps the webhook URL is invalid?): {response.StatusCode}\nResponse: {content}");
            return;
        }

        _webhookData = JsonSerializer.Deserialize<WebhookData>(content);
    }

    // https://discord.com/developers/docs/resources/channel#embed-object-embed-structure
    private struct Embed
    {
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("color")]
        public int Color { get; set; } = 0;

        [JsonPropertyName("author")]
        public EmbedAuthor? Author { get; set; } = null;
        public Embed()
        {
        }
    }
    // https://discord.com/developers/docs/resources/channel#embed-object-embed-author-structure
    private struct EmbedAuthor
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; set; }

        public EmbedAuthor()
        {
        }
    }
    // https://discord.com/developers/docs/resources/webhook#webhook-object-webhook-structure
    private struct WebhookData
    {
        [JsonPropertyName("guild_id")]
        public string? GuildId { get; set; } = null;

        [JsonPropertyName("channel_id")]
        public string? ChannelId { get; set; } = null;

        public WebhookData()
        {
        }
    }
    // https://discord.com/developers/docs/resources/channel#message-object-message-structure
    private struct WebhookPayload
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("avatar_url")]
        public string? AvatarUrl { get; set; } = "";

        [JsonPropertyName("embeds")]
        public List<Embed>? Embeds { get; set; } = null;

        [JsonPropertyName("allowed_mentions")]
        public Dictionary<string, string[]> AllowedMentions { get; set; } =
            new()
            {
                    { "parse", Array.Empty<string>() },
            };

        public WebhookPayload()
        {
        }
    }

    #endregion
}
