using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Timers;
using System.Linq;
using Timer = System.Timers.Timer;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VRCGroupTools.Models;

namespace VRCGroupTools.Services;

public interface IAuditLogService
{
    event EventHandler<List<AuditLogEntry>>? NewLogsReceived;
    event EventHandler<string>? StatusChanged;
    event EventHandler<FetchProgressEventArgs>? FetchProgressChanged;
    
    Task StartPollingAsync(string groupId);
    void StopPolling();
    Task<List<AuditLogEntry>> GetAllLogsAsync();
    Task<List<AuditLogEntry>> SearchLogsAsync(string? searchQuery = null, string? eventType = null, DateTime? fromDate = null, DateTime? toDate = null);
    Task<List<AuditLogEntry>> FetchHistoricalLogsAsync(int maxPages = 100);
    Task RefreshLogsAsync();
    bool IsPolling { get; }
    int TotalLogCount { get; }
}

public class FetchProgressEventArgs : EventArgs
{
    public int PagesFetched { get; set; }
    public int TotalLogsFetched { get; set; }
}

public class AuditLogService : IAuditLogService, IDisposable
{
    private readonly IVRChatApiService _apiService;
    private readonly ICacheService _cacheService;
    private readonly IDiscordWebhookService _discordService;
    private readonly ISecurityMonitorService? _securityMonitor;
    private readonly ISettingsService? _settingsService;
    private Timer? _pollingTimer;
    private string? _currentGroupId;
    private bool _isPolling;
    private int _totalLogCount;
    private bool _isSendingUnsentDiscordLogs;

    public event EventHandler<List<AuditLogEntry>>? NewLogsReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<FetchProgressEventArgs>? FetchProgressChanged;

    public bool IsPolling => _isPolling;
    public int TotalLogCount => _totalLogCount;

    public AuditLogService(IVRChatApiService apiService, ICacheService cacheService, IDiscordWebhookService discordService, ISettingsService? settingsService = null, ISecurityMonitorService? securityMonitor = null)
    {
        _apiService = apiService;
        _cacheService = cacheService;
        _discordService = discordService;
        _settings_service = settingsService; // note: keep field consistent below
        _securityMonitor = securityMonitor;
    }

    // Helper to get the managed group's configuration (if settings service available)
    private GroupConfiguration? GetGroupConfig(string? groupId)
    {
        if (string.IsNullOrEmpty(groupId) || _settingsService == null) return null;
        try
        {
            var prop = _settingsService.GetType().GetProperty("ManagedGroups");
            if (prop != null)
            {
                var mg = prop.GetValue(_settingsService) as IEnumerable<GroupConfiguration>;
                return mg?.FirstOrDefault(g => g.GroupId == groupId);
            }
        }
        catch { }
        return null;
    }

    public async Task StartPollingAsync(string groupId)
    {
        Console.WriteLine($"[AUDIT-SVC] StartPollingAsync called for group: {groupId}");
        _currentGroupId = groupId;
        
        // Get count from database
        _totalLogCount = await _cacheService.GetAuditLogCountAsync(groupId);
        Console.WriteLine($"[AUDIT-SVC] Database has {_totalLogCount} cached audit logs for this group");
        StatusChanged?.Invoke(this, $"Loading {_totalLogCount} cached logs from database...");
        
        // Load cached logs and notify UI
        var cachedLogs = await _cache_service.LoadAuditLogsAsync(groupId);
        Console.WriteLine($"[AUDIT-SVC] Loaded {cachedLogs.Count} logs from cache");
        
        if (cachedLogs.Count > 0)
        {
            NewLogsReceived?.Invoke(this, cachedLogs);
            StatusChanged?.Invoke(this, $"✓ Loaded {cachedLogs.Count} cached logs from database");
        }
        else
        {
            StatusChanged?.Invoke(this, "No cached logs. Click 'Fetch History' to download.");
        }

        // Check for unsent Discord logs and send them if webhook configured globally or per-group
        var groupConfig = GetGroupConfig(groupId);
        bool discordConfiguredForGroup = _discordService.IsConfigured || !string.IsNullOrWhiteSpace(groupConfig?.DiscordWebhookUrl);
        if (discordConfiguredForGroup)
        {
            await SendUnsentDiscordLogsAsync();
        }

        // Start polling timer (60 seconds)
        _pollingTimer?.Dispose();
        _pollingTimer = new Timer(60000); // 60 seconds
        _pollingTimer.Elapsed += async (s, e) => await PollForNewLogsAsync();
        _pollingTimer.AutoReset = true;
        _pollingTimer.Start();
        _isPolling = true;

        // Do an initial poll
        Console.WriteLine("[AUDIT-SVC] Starting initial poll...");
        await PollForNewLogsAsync();
        
        Console.WriteLine("[AUDIT-SVC] Polling timer started (60 second interval)");
        StatusChanged?.Invoke(this, $"▶ Polling active - auto-checking every 60s | Total: {_totalLogCount} logs");
    }

    public void StopPolling()
    {
        Console.WriteLine("[AUDIT-SVC] StopPolling() called");
        _pollingTimer?.Stop();
        _pollingTimer?.Dispose();
        _polling_timer = null;
        _isPolling = false;
        Console.WriteLine("[AUDIT-SVC] Polling stopped");
        StatusChanged?.Invoke(this, $"⏸ Polling stopped | Total: {_totalLogCount} logs in database");
    }

    public async Task<List<AuditLogEntry>> GetAllLogsAsync()
    {
        if (string.IsNullOrEmpty(_currentGroupId)) return new List<AuditLogEntry>();
        return await _cacheService.LoadAuditLogsAsync(_currentGroupId);
    }

    public async Task<List<AuditLogEntry>> SearchLogsAsync(string? searchQuery = null, string? eventType = null, DateTime? fromDate = null, DateTime? toDate = null)
    {
        if (string.IsNullOrEmpty(_currentGroupId)) return new List<AuditLogEntry>();
        return await _cacheService.SearchAuditLogsAsync(_currentGroupId, searchQuery, eventType, fromDate, toDate);
    }

    public async Task RefreshLogsAsync()
    {
        await PollForNewLogsAsync();
    }

    private async Task PollForNewLogsAsync()
    {
        if (string.IsNullOrEmpty(_currentGroupId))
        {
            Console.WriteLine("[AUDIT-SVC] PollForNewLogsAsync: No group ID set, skipping");
            return;
        }

        try
        {
            Console.WriteLine($"[AUDIT-SVC] Polling VRChat API for new audit logs (group: {_currentGroupId})...");
            StatusChanged?.Invoke(this, "🔍 Checking VRChat API for new audit logs...");

            // Get existing log IDs before fetch
            var existingLogs = await _cacheService.LoadAuditLogsAsync(_currentGroupId);
            var existingIds = new HashSet<string>(existingLogs.Select(l => l.Id));

            var newLogs = await FetchLogsFromApiAsync(100); // Get latest 100
            Console.WriteLine($"[AUDIT-SVC] API returned {newLogs.Count} log entries");
            
            if (newLogs.Count > 0)
            {
                // Find truly new logs (not in existing)
                var trulyNewLogs = newLogs.Where(l => !existingIds.Contains(l.Id)).ToList();
                
                var savedCount = await _cache_service.AppendAuditLogsAsync(_currentGroupId, newLogs);
                _totalLogCount = await _cacheService.GetAuditLogCountAsync(_currentGroupId);
                Console.WriteLine($"[AUDIT-SVC] Saved {savedCount} new entries (duplicates skipped). Total in DB: {_totalLogCount}");
                
                // Resolve group config and whether discord is configured for this group
                var groupConfig = GetGroupConfig(_currentGroupId);
                bool discordConfiguredForGroup = _discordService.IsConfigured || !string.IsNullOrWhiteSpace(groupConfig?.DiscordWebhookUrl);

                Console.WriteLine($"[AUDIT-SVC] Truly new logs count: {trulyNewLogs.Count}, Discord configured (global): {_discord_service.IsConfigured}, group webhook present: {!string.IsNullOrWhiteSpace(groupConfig?.DiscordWebhookUrl)}");
                
                if (trulyNewLogs.Count > 0 && discordConfiguredForGroup)
                {
                    Console.WriteLine($"[AUDIT-SVC] Sending {trulyNewLogs.Count} Discord notifications...");
                    int sent = 0;
                    var sentLogIds = new List<string>();
                    foreach (var log in trulyNewLogs) // Send all new logs
                    {
                        Console.WriteLine($"[AUDIT-SVC] Processing log {sent + 1}/{trulyNewLogs.Count}: EventType={log.EventType}, Actor={log.ActorName}");
                        var success = await SendDiscordNotificationAsync(log, groupConfig);
                        if (success)
                        {
                            sentLogIds.Add(log.Id);
                        }
                        sent++;
                        await Task.Delay(500); // Rate limit
                    }
                    
                    // Mark successfully sent logs
                    if (sentLogIds.Count > 0)
                    {
                        await _cacheService.MarkLogsAsSentToDiscordAsync(sentLogIds);
                        Console.WriteLine($"[AUDIT-SVC] Marked {sentLogIds.Count} logs as sent to Discord");
                    }
                    
                    Console.WriteLine($"[AUDIT-SVC] Completed sending {sent} Discord notifications");
                }
                else if (trulyNewLogs.Count > 0 && !discordConfiguredForGroup)
                {
                    Console.WriteLine($"[AUDIT-SVC] Discord webhook not configured for this group - skipping {trulyNewLogs.Count} notifications");
                }
                else if (trulyNewLogs.Count == 0)
                {
                    Console.WriteLine($"[AUDIT-SVC] No truly new logs to send to Discord");
                }
                
                // Reload from database and notify
                var allLogs = await _cacheService.LoadAuditLogsAsync(_currentGroupId);
                NewLogsReceived?.Invoke(this, allLogs);
                
                if (savedCount > 0)
                {
                    StatusChanged?.Invoke(this, $"✓ Found {savedCount} new entries | Total: {_totalLogCount} logs");
                }
                else
                {
                    StatusChanged?.Invoke(this, $"✓ Up to date | Total: {_totalLogCount} logs");
                }
            }
            else
            {
                Console.WriteLine("[AUDIT-SVC] API returned 0 logs (may be rate limited or no new logs)");
                StatusChanged?.Invoke(this, $"✓ No new logs from API | Total: {_totalLogCount} logs");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIT-SVC] ERROR polling: {ex.Message}");
            Console.WriteLine($"[AUDIT-SVC] Stack trace: {ex.StackTrace}");
            StatusChanged?.Invoke(this, $"✗ Polling error: {ex.Message}");
        }
    }

    // Modified helper to accept group config and forward to discord service
    private async Task<bool> SendDiscordNotificationAsync(AuditLogEntry log, GroupConfiguration? groupConfig = null)
    {
        try
        {
            // Use the interface method that supports group webhook & config
            var success = await _discordService.SendAuditEventAsync(
                log.EventType,
                log.ActorName ?? "Unknown",
                log.TargetName,
                log.Description,
                groupConfig?.DiscordWebhookUrl,
                groupConfig
            );
            return success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIT-SVC] Discord notification failed: {ex.Message}");
            return false;
        }
    }

    private async Task SendUnsentDiscordLogsAsync()
    {
        if (string.IsNullOrEmpty(_currentGroupId))
        {
            Console.WriteLine("[AUDIT-SVC] SendUnsentDiscordLogsAsync: No group ID set");
            return;
        }

        if (_isSendingUnsentDiscordLogs)
        {
            Console.WriteLine("[AUDIT-SVC] SendUnsentDiscordLogsAsync: already running, skipping");
            return;
        }

        _isSendingUnsentDiscordLogs = true;

        try
        {
            Console.WriteLine("[AUDIT-SVC] Checking for unsent Discord logs...");
            var unsentLogs = await _cacheService.GetUnsentDiscordLogsAsync(_currentGroupId, 100);
            
            if (unsentLogs.Count == 0)
            {
                Console.WriteLine("[AUDIT-SVC] No unsent Discord logs found");
                return;
            }

            var groupedLogs = unsentLogs
                .GroupBy(BuildDiscordDedupKey)
                .ToList();

            Console.WriteLine($"[AUDIT-SVC] Found {unsentLogs.Count} unsent logs ({groupedLogs.Count} unique). Sending to Discord...");
            StatusChanged?.Invoke(this, $"📤 Sending {groupedLogs.Count} pending Discord notifications...");
            
            int sent = 0;
            int skipped = 0;
            var sentLogIds = new List<string>();

            var groupConfig = GetGroupConfig(_currentGroupId);

            foreach (var group in groupedLogs.OrderBy(g => g.Min(l => l.CreatedAt)))
            {
                var log = group.OrderBy(l => l.CreatedAt).First();
                var groupIds = group.Select(l => l.Id).ToList();

                if (!_discordService.ShouldSendAuditEvent(log.EventType, groupConfig))
                {
                    sentLogIds.AddRange(groupIds);
                    skipped++;
                    Console.WriteLine($"[AUDIT-SVC] Skipping disabled event type '{log.EventType}' for {groupIds.Count} log(s)");
                    continue;
                }

                Console.WriteLine($"[AUDIT-SVC] Sending unsent log {sent + 1}/{groupedLogs.Count}: {log.EventType} - {log.ActorName}");
                var success = await SendDiscordNotificationAsync(log, groupConfig);

                if (success)
                {
                    sentLogIds.AddRange(groupIds);
                    sent++;
                }

                await Task.Delay(500); // Rate limit - 500ms between messages
            }
            
            // Mark successfully sent logs
            if (sentLogIds.Count > 0)
            {
                await _cacheService.MarkLogsAsSentToDiscordAsync(sentLogIds);
                Console.WriteLine($"[AUDIT-SVC] Successfully sent and marked {sentLogIds.Count} logs");
                var sentMessage = skipped > 0
                    ? $"✓ Sent {sent} notifications (skipped {skipped} disabled)"
                    : $"✓ Sent {sent} pending Discord notifications";
                StatusChanged?.Invoke(this, sentMessage);
            }
            
            if (sent < groupedLogs.Count)
            {
                Console.WriteLine($"[AUDIT-SVC] Warning: Only {sent}/{groupedLogs.Count} logs were successfully sent");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIT-SVC] Error sending unsent Discord logs: {ex.Message}");
            StatusChanged?.Invoke(this, $"✗ Error sending pending notifications: {ex.Message}");
        }
        finally
        {
            _isSendingUnsentDiscordLogs = false;
        }
    }

    private static string ComputeAuditLogId(string rawJson)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(rawJson);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private static string BuildDiscordDedupKey(AuditLogEntry log)
    {
        if (!string.IsNullOrWhiteSpace(log.RawData))
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(log.RawData);
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        return string.Join("|", new[]
        {
            log.EventType,
            log.ActorId ?? string.Empty,
            log.TargetId ?? string.Empty,
            log.Description ?? string.Empty,
            log.CreatedAt.ToUniversalTime().ToString("o")
        });
    }

    public void Dispose()
    {
        StopPolling();
    }

    private async Task TrackSecurityActionAsync(AuditLogEntry log)
    {
        if (_securityMonitor == null || string.IsNullOrEmpty(_currentGroupId))
            return;

        // Only track actions that have an actor (someone performed the action)
        if (string.IsNullOrEmpty(log.ActorId))
            return;

        // Check if this is an instance-specific action by looking for instanceId in RawData
        bool isInstanceAction = false;
        if (!string.IsNullOrEmpty(log.RawData))
        {
            try
            {
                var additionalData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(log.RawData);
                isInstanceAction = additionalData?.ContainsKey("instanceId") == true;
            }
            catch { /* Ignore parse errors */ }
        }

        // Map audit log event types to security action types, with instance vs group distinction
        var actionType = log.EventType.ToLower() switch
        {
            "group.user.kick" => isInstanceAction ? "instance_kick" : "group_kick",
            "group.user.ban" => isInstanceAction ? "instance_ban" : "group_ban",
            "group.user.role.remove" or "group.role.remove" => "role_remove",
            "group.invite.reject" => "invite_reject",
            "group.post.delete" => "post_delete",
            "group.announcement.delete" => "post_delete",
            "group.gallery.delete" => "post_delete",
            _ => null
        };

        if (actionType != null)
        {
            await _securityMonitor.TrackActionAsync(
                _currentGroupId,
                log.ActorId,
                log.ActorName ?? "Unknown",
                actionType,
                log.TargetId,
                log.TargetName,
                new { EventType = log.EventType, Timestamp = log.CreatedAt, IsInstanceAction = isInstanceAction }
            );
        }
    }
}
