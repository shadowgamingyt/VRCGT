using System.Net.Http;
using System.Text;
using Newtonsoft.Json;

namespace VRCGroupTools.Services;

public interface IDiscordWebhookService
{
    Task<bool> SendMessageAsync(string title, string description, int color, string? thumbnailUrl = null, string? groupId = null);
    Task<WebhookTestResult> TestWebhookAsync(string webhookUrl);
    Task<bool> SendModerationActionAsync(string actionType, string targetUserId, string targetName, string actorName, string reason, string? description, DateTime actionTime, DateTime? expiresAt = null, int? infractionCount = null, string? groupId = null);
    Task<bool> SendWebhookAsync(string webhookUrl, object payload);
    bool IsConfigured { get; }
    bool IsConfiguredForGroup(string? groupId);
    Task<bool> SendAuditEventAsync(string eventType, string actorName, string? targetName, string? description, string? groupId = null);
    bool ShouldSendAuditEvent(string eventType, string? groupId = null);
    string? GetWebhookUrlForEventType(string eventType, string? groupId = null);
}

public class WebhookTestResult
{
    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class DiscordWebhookService : IDiscordWebhookService
{
    private readonly ISettingsService _settingsService;

    // Use one HttpClient for the lifetime of the app.
    // (Avoid socket exhaustion / weird intermittent send failures)
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settingsService.Settings.DiscordWebhookUrl);

    public DiscordWebhookService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public bool IsConfiguredForGroup(string? groupId)
    {
        if (string.IsNullOrEmpty(groupId))
        {
            // global
            return !string.IsNullOrWhiteSpace(_settingsService.Settings.DiscordWebhookUrl);
        }

        var groupConfig = _settingsService.GetGroupConfig(groupId);
        if (groupConfig == null)
        {
            // fall back to global if group config isn't found
            return !string.IsNullOrWhiteSpace(_settingsService.Settings.DiscordWebhookUrl);
        }

        // IMPORTANT: consider category-specific webhooks as "configured"
        return
            !string.IsNullOrWhiteSpace(groupConfig.DiscordWebhookUrl) ||
            !string.IsNullOrWhiteSpace(groupConfig.DiscordWebhookMemberEvents) ||
            !string.IsNullOrWhiteSpace(groupConfig.DiscordWebhookRoleEvents) ||
            !string.IsNullOrWhiteSpace(groupConfig.DiscordWebhookInstanceEvents) ||
            !string.IsNullOrWhiteSpace(groupConfig.DiscordWebhookGroupEvents) ||
            !string.IsNullOrWhiteSpace(groupConfig.DiscordWebhookInviteEvents) ||
            !string.IsNullOrWhiteSpace(groupConfig.DiscordWebhookAnnouncementEvents) ||
            !string.IsNullOrWhiteSpace(groupConfig.DiscordWebhookGalleryEvents) ||
            !string.IsNullOrWhiteSpace(groupConfig.DiscordWebhookPostEvents) ||
            !string.IsNullOrWhiteSpace(_settingsService.Settings.DiscordWebhookUrl); // global fallback
    }

    public async Task<bool> SendMessageAsync(string title, string description, int color, string? thumbnailUrl = null, string? groupId = null)
    {
        // Determine event type for routing if possible (mostly used by SendAuditEventAsync)
        string? eventType = ExtractEventTypeFromTitle(title);

        string? webhookUrl = null;

        // 1) Group/category webhook if groupId provided
        if (!string.IsNullOrEmpty(groupId) && !string.IsNullOrEmpty(eventType))
        {
            webhookUrl = GetWebhookUrlForEventType(eventType, groupId);
        }

        // 2) Group legacy webhook
        if (string.IsNullOrEmpty(webhookUrl) && !string.IsNullOrEmpty(groupId))
        {
            webhookUrl = _settingsService.GetGroupConfig(groupId)?.DiscordWebhookUrl;
        }

        // 3) Global fallback ALWAYS (this fixes "group configured wrong => sends nowhere")
        if (string.IsNullOrEmpty(webhookUrl))
        {
            webhookUrl = _settingsService.Settings.DiscordWebhookUrl;
        }

        webhookUrl = NormalizeWebhookUrl(webhookUrl);

        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            Console.WriteLine("[DISCORD] Not configured: no webhook URL (group/category/global).");
            return false;
        }

        // Ensure we never send an "empty message" payload.
        // If title/description are empty, Discord may reject the request as empty.
        title = string.IsNullOrWhiteSpace(title) ? "Notification" : title.Trim();
        description = description ?? string.Empty;
        if (string.IsNullOrWhiteSpace(description))
        {
            // Zero-width space to keep embed valid
            description = "\u200B";
        }

        var payload = BuildEmbedPayload(title, description, color, thumbnailUrl);

        return await ExecuteWebhookAsync(webhookUrl, payload, debugContext: $"SendMessageAsync group={groupId ?? "(none)"} eventType={eventType ?? "(unknown)"}");
    }

    public async Task<WebhookTestResult> TestWebhookAsync(string webhookUrl)
    {
        webhookUrl = NormalizeWebhookUrl(webhookUrl);

        if (string.IsNullOrWhiteSpace(webhookUrl))
            return new WebhookTestResult { Success = false, ErrorMessage = "Webhook URL is empty" };

        var embed = new
        {
            title = "✅ Webhook Connected!",
            description = "VRC Group Tools is now connected to this channel.\n\nYou will receive notifications based on your settings.",
            color = 0x4CAF50,
            timestamp = DateTime.UtcNow.ToString("o"),
            footer = new { text = "VRC Group Tools" }
        };

        var payload = new
        {
            embeds = new[] { embed },
            // Prevent accidental @everyone/@here mention abuse from dynamic text
            allowed_mentions = new { parse = Array.Empty<string>() }
        };

        try
        {
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(webhookUrl, content);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                return new WebhookTestResult { Success = true, StatusCode = (int)response.StatusCode };
            }

            return new WebhookTestResult
            {
                Success = false,
                StatusCode = (int)response.StatusCode,
                ErrorMessage = string.IsNullOrWhiteSpace(body) ? response.StatusCode.ToString() : body
            };
        }
        catch (Exception ex)
        {
            return new WebhookTestResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<bool> SendWebhookAsync(string webhookUrl, object payload)
    {
        webhookUrl = NormalizeWebhookUrl(webhookUrl);
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return false;

        return await ExecuteWebhookAsync(webhookUrl, payload, debugContext: "SendWebhookAsync");
    }

    public bool ShouldSendAuditEvent(string eventType, string? groupId = null)
    {
        var type = NormalizeEventType(eventType);

        // Group-specific settings
        if (!string.IsNullOrEmpty(groupId))
        {
            var groupConfig = _settingsService.GetGroupConfig(groupId);
            if (groupConfig != null)
            {
                return type switch
                {
                    // Member Events
                    "group.user.join" => groupConfig.DiscordNotifyUserJoins,
                    "group.user.leave" => groupConfig.DiscordNotifyUserLeaves,
                    "group.user.kick" => groupConfig.DiscordNotifyUserKicked,
                    "group.user.ban" => groupConfig.DiscordNotifyUserBanned,
                    "group.user.unban" => groupConfig.DiscordNotifyUserUnbanned,
                    "group.user.role.add" => groupConfig.DiscordNotifyUserRoleAdd,
                    "group.user.role.remove" => groupConfig.DiscordNotifyUserRoleRemove,

                    // Join Request Events
                    "group.request.create" or "group.joinrequest" => groupConfig.DiscordNotifyJoinRequests,
                    "group.request.accept" => groupConfig.DiscordNotifyJoinRequests,
                    "group.request.reject" => groupConfig.DiscordNotifyJoinRequests,
                    "group.request.block" => groupConfig.DiscordNotifyJoinRequests,
                    "group.request.unblock" => groupConfig.DiscordNotifyJoinRequests,

                    // Role Events
                    "group.role.create" => groupConfig.DiscordNotifyRoleCreate,
                    "group.role.update" => groupConfig.DiscordNotifyRoleUpdate,
                    "group.role.delete" => groupConfig.DiscordNotifyRoleDelete,

                    // Instance Events
                    "group.instance.create" => groupConfig.DiscordNotifyInstanceCreate,
                    "group.instance.delete" => groupConfig.DiscordNotifyInstanceDelete,
                    "group.instance.open" => groupConfig.DiscordNotifyInstanceOpened,
                    "group.instance.close" => groupConfig.DiscordNotifyInstanceClosed,
                    "group.instance.warn" => groupConfig.DiscordNotifyInstanceWarn,

                    // Group Events
                    "group.update" => groupConfig.DiscordNotifyGroupUpdate,

                    // Invite Events
                    "group.invite.create" or "group.user.invite" => groupConfig.DiscordNotifyInviteCreate,
                    "group.invite.accept" => groupConfig.DiscordNotifyInviteAccept,
                    "group.invite.reject" => groupConfig.DiscordNotifyInviteReject,

                    // Announcement Events
                    "group.announcement.create" => groupConfig.DiscordNotifyAnnouncementCreate,
                    "group.announcement.delete" => groupConfig.DiscordNotifyAnnouncementDelete,

                    // Gallery Events
                    "group.gallery.create" => groupConfig.DiscordNotifyGalleryCreate,
                    "group.gallery.delete" => groupConfig.DiscordNotifyGalleryDelete,

                    // Post Events
                    "group.post.create" => groupConfig.DiscordNotifyPostCreate,
                    "group.post.delete" => groupConfig.DiscordNotifyPostDelete,

                    _ => false
                };
            }
        }

        // Global fallback
        var settings = _settingsService.Settings;

        return type switch
        {
            // Member Events
            "group.user.join" => settings.DiscordNotifyUserJoins,
            "group.user.leave" => settings.DiscordNotifyUserLeaves,
            "group.user.kick" => settings.DiscordNotifyUserKicked,
            "group.user.ban" => settings.DiscordNotifyUserBanned,
            "group.user.unban" => settings.DiscordNotifyUserUnbanned,
            "group.user.role.add" => settings.DiscordNotifyUserRoleAdd,
            "group.user.role.remove" => settings.DiscordNotifyUserRoleRemove,

            // Join Request Events
            "group.request.create" or "group.joinrequest" => settings.DiscordNotifyJoinRequests,
            "group.request.accept" => settings.DiscordNotifyJoinRequests,
            "group.request.reject" => settings.DiscordNotifyJoinRequests,
            "group.request.block" => settings.DiscordNotifyJoinRequests,
            "group.request.unblock" => settings.DiscordNotifyJoinRequests,

            // Role Events
            "group.role.create" => settings.DiscordNotifyRoleCreate,
            "group.role.update" => settings.DiscordNotifyRoleUpdate,
            "group.role.delete" => settings.DiscordNotifyRoleDelete,

            // Instance Events
            "group.instance.create" => settings.DiscordNotifyInstanceCreate,
            "group.instance.delete" => settings.DiscordNotifyInstanceDelete,
            "group.instance.open" => settings.DiscordNotifyInstanceOpened,
            "group.instance.close" => settings.DiscordNotifyInstanceClosed,
            "group.instance.warn" => settings.DiscordNotifyInstanceWarn,

            // Group Events
            "group.update" => settings.DiscordNotifyGroupUpdate,

            // Invite Events
            "group.invite.create" or "group.user.invite" => settings.DiscordNotifyInviteCreate,
            "group.invite.accept" => settings.DiscordNotifyInviteAccept,
            "group.invite.reject" => settings.DiscordNotifyInviteReject,

            // Announcement Events
            "group.announcement.create" => settings.DiscordNotifyAnnouncementCreate,
            "group.announcement.delete" => settings.DiscordNotifyAnnouncementDelete,

            // Gallery Events
            "group.gallery.create" => settings.DiscordNotifyGalleryCreate,
            "group.gallery.delete" => settings.DiscordNotifyGalleryDelete,

            // Post Events
            "group.post.create" => settings.DiscordNotifyPostCreate,
            "group.post.delete" => settings.DiscordNotifyPostDelete,

            _ => false
        };
    }

    public async Task<bool> SendAuditEventAsync(string eventType, string actorName, string? targetName, string? description, string? groupId = null)
    {
        var type = NormalizeEventType(eventType);

        // Get webhook URL for this event type
        var webhookUrl = GetWebhookUrlForEventType(type, groupId);
        webhookUrl = NormalizeWebhookUrl(webhookUrl);

        Console.WriteLine($"[DISCORD] SendAuditEventAsync eventType={type} group={groupId ?? "(none)"} hasWebhook={(string.IsNullOrWhiteSpace(webhookUrl) ? "no" : "yes")}");

        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            Console.WriteLine("[DISCORD] No webhook configured for this event type (and no fallback).");
            return false;
        }

        // Settings check
        if (!ShouldSendAuditEvent(type, groupId))
        {
            Console.WriteLine($"[DISCORD] Event type '{type}' disabled by settings.");
            return false;
        }

        // Formatting
        var (title, color, emoji) = type switch
        {
            // User Events
            "group.user.join" => ("Member Joined", 0x4CAF50, "👋"),
            "group.user.leave" => ("Member Left", 0x9E9E9E, "🚪"),
            "group.user.kick" => ("Member Kicked", 0xFF9800, "👢"),
            "group.user.ban" => ("Member Banned", 0xF44336, "🔨"),
            "group.user.unban" => ("Member Unbanned", 0x4CAF50, "✅"),
            "group.user.role.add" => ("Role Added", 0x9C27B0, "➕"),
            "group.user.role.remove" => ("Role Removed", 0xFF5722, "➖"),

            // Join Request Events
            "group.request.create" or "group.joinrequest" => ("Join Request", 0x7C4DFF, "📥"),
            "group.request.accept" => ("Request Accepted", 0x4CAF50, "✅"),
            "group.request.reject" => ("Request Rejected", 0xF44336, "❌"),
            "group.request.block" => ("Request Blocked", 0xD32F2F, "🚫"),
            "group.request.unblock" => ("Request Unblocked", 0x8BC34A, "🔓"),

            // Role Events
            "group.role.create" => ("Role Created", 0x00BCD4, "🎭"),
            "group.role.update" => ("Role Updated", 0x9C27B0, "🏷️"),
            "group.role.delete" => ("Role Deleted", 0xF44336, "🗑️"),

            // Instance Events
            "group.instance.create" => ("Instance Created", 0x03A9F4, "🌍"),
            "group.instance.delete" => ("Instance Deleted", 0x9E9E9E, "🚫"),
            "group.instance.open" => ("Instance Opened", 0x2196F3, "🌐"),
            "group.instance.close" => ("Instance Closed", 0x9E9E9E, "🔒"),
            "group.instance.warn" => ("Instance Warning Issued", 0xFFC107, "⚠️"),

            // Group Events
            "group.update" => ("Group Updated", 0xFFC107, "⚙️"),

            // Invite Events
            "group.invite.create" or "group.user.invite" => ("Invite Sent", 0x8BC34A, "💌"),
            "group.invite.accept" => ("Invite Accepted", 0x4CAF50, "✔️"),
            "group.invite.reject" => ("Invite Rejected", 0xFF5722, "❌"),

            // Announcement Events
            "group.announcement.create" => ("Announcement Posted", 0xFF9800, "📢"),
            "group.announcement.delete" => ("Announcement Deleted", 0x757575, "🗑️"),

            // Gallery Events
            "group.gallery.create" => ("Gallery Item Added", 0xE91E63, "🖼️"),
            "group.gallery.delete" => ("Gallery Item Removed", 0x9E9E9E, "🗑️"),

            // Post Events
            "group.post.create" => ("Post Created", 0x2196F3, "📝"),
            "group.post.delete" => ("Post Deleted", 0x757575, "🗑️"),

            _ => ($"Event: {type}", 0x757575, "📋")
        };

        var sb = new StringBuilder();
        sb.AppendLine($"**Actor:** {actorName}");
        if (!string.IsNullOrWhiteSpace(targetName))
            sb.AppendLine($"**Target:** {targetName}");
        if (!string.IsNullOrWhiteSpace(description))
            sb.AppendLine($"**Details:** {description}");

        // Send using resolved URL (so we log + fail with actual status codes)
        var payload = BuildEmbedPayload($"{emoji} {title}", sb.ToString(), color, thumbnailUrl: null);
        return await ExecuteWebhookAsync(webhookUrl, payload, debugContext: $"SendAuditEventAsync type={type} group={groupId ?? "(none)"}");
    }

    public async Task<bool> SendModerationActionAsync(
        string actionType,
        string targetUserId,
        string targetName,
        string actorName,
        string reason,
        string? description,
        DateTime actionTime,
        DateTime? expiresAt = null,
        int? infractionCount = null,
        string? groupId = null)
    {
        // For moderation actions, route via legacy/group/global webhook (or your app can add a separate category later)
        if (!IsConfiguredForGroup(groupId))
            return false;

        var (title, color, emoji) = actionType.ToLowerInvariant() switch
        {
            "warning" => ("User Warned", 0xFFC107, "⚠️"),
            "kick" => ("User Kicked", 0xFF9800, "👢"),
            "ban" => ("User Banned", 0xF44336, "🔨"),
            _ => ("Moderation Action", 0x757575, "📋")
        };

        var sb = new StringBuilder();
        var vrchatUrl = $"https://vrchat.com/home/user/{targetUserId}";
        sb.AppendLine($"**Target:** [{targetName}]({vrchatUrl})");
        sb.AppendLine($"**User ID:** `{targetUserId}`");
        sb.AppendLine($"**Moderator:** {actorName}");
        sb.AppendLine($"**Reason:** {reason}");

        if (!string.IsNullOrWhiteSpace(description))
            sb.AppendLine($"**Details:** {description}");

        var unixTime = ((DateTimeOffset)actionTime).ToUnixTimeSeconds();
        sb.AppendLine($"**Time:** <t:{unixTime}:F>");

        if (expiresAt.HasValue)
        {
            var expiryUnixTime = ((DateTimeOffset)expiresAt.Value).ToUnixTimeSeconds();
            sb.AppendLine($"**Expires:** <t:{expiryUnixTime}:R>");
        }
        else if (actionType.Equals("ban", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("**Duration:** Permanent");
        }

        if (infractionCount.HasValue && infractionCount.Value > 0)
            sb.AppendLine($"**Previous Infractions:** {infractionCount.Value}");

        return await SendMessageAsync($"{emoji} {title}", sb.ToString(), color, null, groupId);
    }

    private static object BuildEmbedPayload(string title, string description, int color, string? thumbnailUrl)
    {
        var embed = new
        {
            title,
            description,
            color,
            timestamp = DateTime.UtcNow.ToString("o"),
            thumbnail = thumbnailUrl != null ? new { url = thumbnailUrl } : null,
            footer = new { text = "VRC Group Tools" }
        };

        // allowed_mentions prevents accidental mentions from dynamic text
        return new
        {
            embeds = new[] { embed },
            allowed_mentions = new { parse = Array.Empty<string>() }
        };
    }

    private static async Task<(bool ok, int status, string body)> PostJsonAsync(string url, object payload)
    {
        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();
        return (response.IsSuccessStatusCode, (int)response.StatusCode, body);
    }

    private static async Task<bool> ExecuteWebhookAsync(string webhookUrl, object payload, string debugContext)
    {
        try
        {
            var (ok, status, body) = await PostJsonAsync(webhookUrl, payload);

            if (ok)
            {
                Console.WriteLine($"[DISCORD] OK {status} ({debugContext})");
                return true;
            }

            Console.WriteLine($"[DISCORD] FAIL {status} ({debugContext}) Body: {body}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DISCORD] EXCEPTION ({debugContext}) {ex.Message}");
            return false;
        }
    }

    private static string NormalizeEventType(string eventType)
    {
        var t = (eventType ?? string.Empty).Trim().ToLowerInvariant();
        if (t.StartsWith("group.member."))
            t = t.Replace("group.member.", "group.user.");
        return t;
    }

    private static string? NormalizeWebhookUrl(string? webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return null;

        // Normalize older "discordapp.com" URLs to "discord.com"
        // (Many older webhooks still work, but this avoids confusion)
        return webhookUrl.Trim().Replace("https://discordapp.com/", "https://discord.com/");
    }

    private string? ExtractEventTypeFromTitle(string title)
    {
        var lower = (title ?? string.Empty).ToLowerInvariant();

        // Member events
        if (lower.Contains("joined")) return "group.user.join";
        if (lower.Contains("left")) return "group.user.leave";
        if (lower.Contains("kicked")) return "group.user.kick";
        if (lower.Contains("banned") && !lower.Contains("unbanned")) return "group.user.ban";
        if (lower.Contains("unbanned")) return "group.user.unban";

        // IMPORTANT: role add/remove are "group.user.role.*" and should route to RoleEvents
        if (lower.Contains("role added")) return "group.user.role.add";
        if (lower.Contains("role removed")) return "group.user.role.remove";

        // Role events
        if (lower.Contains("role created")) return "group.role.create";
        if (lower.Contains("role updated")) return "group.role.update";
        if (lower.Contains("role deleted")) return "group.role.delete";

        // Instance events
        if (lower.Contains("instance created")) return "group.instance.create";
        if (lower.Contains("instance deleted")) return "group.instance.delete";
        if (lower.Contains("instance opened")) return "group.instance.open";
        if (lower.Contains("instance closed")) return "group.instance.close";
        if (lower.Contains("instance warning")) return "group.instance.warn";

        // Group events
        if (lower.Contains("group updated")) return "group.update";

        // Invite / request events
        if (lower.Contains("join request")) return "group.request.create";
        if (lower.Contains("invite sent")) return "group.invite.create";
        if (lower.Contains("invite accepted")) return "group.invite.accept";
        if (lower.Contains("invite rejected")) return "group.invite.reject";

        // Announcement events
        if (lower.Contains("announcement posted")) return "group.announcement.create";
        if (lower.Contains("announcement deleted")) return "group.announcement.delete";

        // Gallery events
        if (lower.Contains("gallery item added")) return "group.gallery.create";
        if (lower.Contains("gallery item removed")) return "group.gallery.delete";

        // Post events
        if (lower.Contains("post created")) return "group.post.create";
        if (lower.Contains("post deleted")) return "group.post.delete";

        return null;
    }

    public string? GetWebhookUrlForEventType(string eventType, string? groupId = null)
    {
        var type = NormalizeEventType(eventType);

        // If no groupId, use global webhook only
        if (string.IsNullOrEmpty(groupId))
            return _settingsService.Settings.DiscordWebhookUrl;

        var groupConfig = _settingsService.GetGroupConfig(groupId);
        if (groupConfig == null)
            return _settingsService.Settings.DiscordWebhookUrl; // global fallback

        string? categoryWebhook = null;

        // ✅ IMPORTANT ORDER:
        // user role add/remove MUST route to RoleEvents, not MemberEvents
        if (type.StartsWith("group.user.role."))
        {
            categoryWebhook = groupConfig.DiscordWebhookRoleEvents;
        }
        else if (type.StartsWith("group.user."))
        {
            categoryWebhook = groupConfig.DiscordWebhookMemberEvents;
        }
        else if (type.Contains("role."))
        {
            categoryWebhook = groupConfig.DiscordWebhookRoleEvents;
        }
        else if (type.Contains("instance."))
        {
            categoryWebhook = groupConfig.DiscordWebhookInstanceEvents;
        }
        else if (type == "group.update")
        {
            categoryWebhook = groupConfig.DiscordWebhookGroupEvents;
        }
        else if (type.Contains("invite") || type.Contains("request") || type.Contains("joinrequest"))
        {
            categoryWebhook = groupConfig.DiscordWebhookInviteEvents;
        }
        else if (type.Contains("announcement"))
        {
            categoryWebhook = groupConfig.DiscordWebhookAnnouncementEvents;
        }
        else if (type.Contains("gallery"))
        {
            categoryWebhook = groupConfig.DiscordWebhookGalleryEvents;
        }
        else if (type.Contains("post"))
        {
            categoryWebhook = groupConfig.DiscordWebhookPostEvents;
        }

        // If category webhook exists use it; else group legacy; else global fallback
        if (!string.IsNullOrWhiteSpace(categoryWebhook))
            return categoryWebhook;

        if (!string.IsNullOrWhiteSpace(groupConfig.DiscordWebhookUrl))
            return groupConfig.DiscordWebhookUrl;

        return _settingsService.Settings.DiscordWebhookUrl;
    }
}
