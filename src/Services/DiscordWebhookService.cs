using System.Net.Http;
using System.Text;
using System.Text;
using System.Text;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using VRCGroupTools.Models;

namespace VRCGroupTools.Services;

public interface IDiscordWebhookService
{
    Task<bool> SendMessageAsync(string title, string description, int color, string? thumbnailUrl = null);
    Task<WebhookTestResult> TestWebhookAsync(string webhookUrl);
    Task<bool> SendModerationActionAsync(string actionType, string targetName, string actorName, string reason, string? description, DateTime actionTime, DateTime? expiresAt = null, int? infractionCount = null);
    Task<bool> SendWebhookAsync(string webhookUrl, object payload);

    // New: allow audit event sending with optional per-group webhook and group config
    Task<bool> SendAuditEventAsync(string eventType, string actorName, string? targetName, string? description, string? webhookUrl = null, GroupConfiguration? groupConfig = null);

    bool IsConfigured { get; }
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
    private readonly HttpClient _httpClient;

    // Global webhook presence.
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settingsService.Settings.DiscordWebhookUrl);

    public DiscordWebhookService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _httpClient = new HttpClient();
    }

    public async Task<bool> SendMessageAsync(string title, string description, int color, string? thumbnailUrl = null)
    {
        Console.WriteLine($"[DISCORD] SendMessageAsync called - Title: {title}");

        if (!IsConfigured)
        {
            Console.WriteLine("[DISCORD] Not configured, webhook URL is missing");
            return false;
        }

        try
        {
            var embed = new
            {
                title = title,
                description = description,
                color = color,
                timestamp = DateTime.UtcNow.ToString("o"),
                thumbnail = thumbnailUrl != null ? new { url = thumbnailUrl } : null,
                footer = new { text = "VRC Group Tools" }
            };

            var payload = new { embeds = new[] { embed } };
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_settingsService.Settings.DiscordWebhookUrl, content);
            Console.WriteLine($"[DISCORD] HTTP Status: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[DISCORD] Error response: {errorBody}");
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DISCORD] Failed to send message: {ex.Message}");
            return false;
        }
    }

    public async Task<WebhookTestResult> TestWebhookAsync(string webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return new WebhookTestResult { Success = false, ErrorMessage = "Webhook URL is empty" };

        try
        {
            var embed = new
            {
                title = "✅ Webhook Connected!",
                description = "VRC Group Tools is now connected to this channel.\n\nYou will receive notifications based on your settings.",
                color = 0x4CAF50,
                timestamp = DateTime.UtcNow.ToString("o"),
                footer = new { text = "VRC Group Tools" }
            };

            var payload = new { embeds = new[] { embed } };
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(webhookUrl, content);

            if (response.IsSuccessStatusCode)
            {
                return new WebhookTestResult { Success = true, StatusCode = (int)response.StatusCode };
            }

            var body = await response.Content.ReadAsStringAsync();
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
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            Console.WriteLine("[DISCORD] SendWebhookAsync: webhookUrl is empty");
            return false;
        }

        try
        {
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(webhookUrl, content);
            Console.WriteLine($"[DISCORD] HTTP Status: {response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[DISCORD] Error response: {errorBody}");
            }
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DISCORD] SendWebhookAsync failed: {ex.Message}");
            return false;
        }
    }

    // Prefer per-group toggles when groupConfig is provided; otherwise fall back to global settings.
    public bool ShouldSendAuditEvent(string eventType, GroupConfiguration? groupConfig = null)
    {
        var settings = _settingsService.Settings;
        bool useGroup = groupConfig != null;

        return eventType switch
        {
            // User Events
            "group.user.join" => useGroup ? groupConfig!.DiscordNotifyUserJoins : settings.DiscordNotifyUserJoins,
            "group.user.leave" => useGroup ? groupConfig!.DiscordNotifyUserLeaves : settings.DiscordNotifyUserLeaves,
            "group.user.kick" => useGroup ? groupConfig!.DiscordNotifyUserKicked : settings.DiscordNotifyUserKicked,
            "group.user.ban" => useGroup ? groupConfig!.DiscordNotifyUserBanned : settings.DiscordNotifyUserBanned,
            "group.user.unban" => useGroup ? groupConfig!.DiscordNotifyUserUnbanned : settings.DiscordNotifyUserUnbanned,
            "group.user.role.add" => useGroup ? groupConfig!.DiscordNotifyRoleAssignments : settings.DiscordNotifyUserRoleAdd,
            "group.user.role.remove" => useGroup ? groupConfig!.DiscordNotifyRoleAssignments : settings.DiscordNotifyUserRoleRemove,
            "group.user.join_request" => useGroup ? groupConfig!.DiscordNotifyJoinRequest : settings.DiscordNotifyJoinRequests,
            "group.joinRequest" => useGroup ? groupConfig!.DiscordNotifyJoinRequest : settings.DiscordNotifyJoinRequests,

            // Role Events
            "group.role.create" => useGroup ? groupConfig!.DiscordNotifyRoleCreate : settings.DiscordNotifyRoleCreate,
            "group.role.update" => useGroup ? groupConfig!.DiscordNotifyRoleUpdate : settings.DiscordNotifyRoleUpdate,
            "group.role.delete" => useGroup ? groupConfig!.DiscordNotifyRoleDelete : settings.DiscordNotifyRoleDelete,

            // Instance Events
            "group.instance.create" => useGroup ? groupConfig!.DiscordNotifyInstanceCreate : settings.DiscordNotifyInstanceCreate,
            "group.instance.delete" => useGroup ? groupConfig!.DiscordNotifyInstanceDelete : settings.DiscordNotifyInstanceDelete,
            "group.instance.open" => useGroup ? groupConfig!.DiscordNotifyInstanceOpened : settings.DiscordNotifyInstanceOpened,
            "group.instance.close" => useGroup ? groupConfig!.DiscordNotifyInstanceClosed : settings.DiscordNotifyInstanceClosed,
            "group.instance.warn" => useGroup ? groupConfig!.DiscordNotifyInstanceWarn : settings.DiscordNotifyInstanceWarn,

            // Group Events
            "group.update" => useGroup ? groupConfig!.DiscordNotifyGroupUpdate : settings.DiscordNotifyGroupUpdate,

            // Invite Events
            "group.invite.create" => useGroup ? groupConfig!.DiscordNotifyInviteSent : settings.DiscordNotifyInviteCreate,
            "group.user.invite" => useGroup ? groupConfig!.DiscordNotifyInviteSent : settings.DiscordNotifyInviteCreate,
            "group.invite.accept" => useGroup ? groupConfig!.DiscordNotifyJoinApproval : settings.DiscordNotifyInviteAccept,
            "group.invite.reject" => useGroup ? groupConfig!.DiscordNotifyJoinRejection : settings.DiscordNotifyInviteReject,

            // Announcement Events
            "group.announcement.create" => useGroup ? groupConfig!.DiscordNotifyAnnouncementCreate : settings.DiscordNotifyAnnouncementCreate,
            "group.announcement.delete" => useGroup ? groupConfig!.DiscordNotifyAnnouncementDelete : settings.DiscordNotifyAnnouncementDelete,

            // Gallery Events
            "group.gallery.create" => useGroup ? groupConfig!.DiscordNotifyGalleryImageSubmitted : settings.DiscordNotifyGalleryCreate,
            "group.gallery.delete" => useGroup ? groupConfig!.DiscordNotifyGalleryImageDeleted : settings.DiscordNotifyGalleryDelete,

            // Post Events
            "group.post.create" => useGroup ? groupConfig!.DiscordNotifyPostCreate : settings.DiscordNotifyPostCreate,
            "group.post.delete" => useGroup ? groupConfig!.DiscordNotifyPostDelete : settings.DiscordNotifyPostDelete,

            _ => false
        };
    }

    // Send audit event using resolved webhook (explicit -> group -> global) and per-group toggles
    public async Task<bool> SendAuditEventAsync(string eventType, string actorName, string? targetName, string? description, string? webhookUrl = null, GroupConfiguration? groupConfig = null)
    {
        // Resolve webhook to use
        string? resolvedWebhook = webhookUrl;
        if (string.IsNullOrWhiteSpace(resolvedWebhook))
            resolvedWebhook = groupConfig?.DiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(resolvedWebhook))
            resolvedWebhook = _settingsService.Settings.DiscordWebhookUrl;

        Console.WriteLine($"[DISCORD] SendAuditEventAsync called - EventType: {eventType}, ResolvedWebhookSet: {!string.IsNullOrWhiteSpace(resolvedWebhook)}");

        if (string.IsNullOrWhiteSpace(resolvedWebhook))
        {
            Console.WriteLine($"[DISCORD] Webhook not configured (no global or group webhook), skipping notification");
            return false;
        }

        bool shouldSend = ShouldSendAuditEvent(eventType, groupConfig);
        Console.WriteLine($"[DISCORD] Event type '{eventType}' shouldSend: {shouldSend}");

        if (!shouldSend)
        {
            Console.WriteLine($"[DISCORD] Event type '{eventType}' is disabled in settings, skipping");
            return false;
        }

        var (title, color, emoji) = eventType switch
        {
            "group.user.join" => ("Member Joined", 0x4CAF50, "👋"),
            "group.user.leave" => ("Member Left", 0x9E9E9E, "🚪"),
            "group.user.kick" => ("Member Kicked", 0xFF9800, "👢"),
            "group.user.ban" => ("Member Banned", 0xF44336, "🔨"),
            "group.user.unban" => ("Member Unbanned", 0x4CAF50, "✅"),
            "group.user.role.add" => ("Role Added", 0x9C27B0, "➕"),
            "group.user.role.remove" => ("Role Removed", 0x9C27B0, "➖"),
            "group.user.join_request" => ("Join Request", 0x03A9F4, "✉️"),
            "group.invite.create" => ("Invite Sent", 0x8BC34A, "💌"),
            "group.invite.accept" => ("Invite Accepted", 0x4CAF50, "✔️"),
            "group.invite.reject" => ("Invite Rejected", 0xFF5722, "❌"),
            "group.announcement.create" => ("Announcement Posted", 0xFF9800, "📢"),
            "group.announcement.delete" => ("Announcement Deleted", 0x757575, "🗑️"),
            _ => ("Notification", 0x607D8B, "🔔")
        };

        var desc = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(actorName))
            desc.AppendLine($"**Actor:** {actorName}");
        if (!string.IsNullOrWhiteSpace(targetName))
            desc.AppendLine($"**Target:** {targetName}");
        if (!string.IsNullOrEmpty(description))
            desc.AppendLine($"**Details:** {description}");

        Console.WriteLine($"[DISCORD] Sending message: {emoji} {title}");
        var embed = new
        {
            title = $"{emoji} {title}",
            description = desc.ToString(),
            color = color,
            timestamp = DateTime.UtcNow.ToString("o"),
            footer = new { text = "VRC Group Tools" }
        };

        var payload = new { embeds = new[] { embed } };
        var success = await SendWebhookAsync(resolvedWebhook!, payload);
        Console.WriteLine($"[DISCORD] Message send result: {success}");
        return success;
    }
}
