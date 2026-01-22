using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using VRCGroupTools.Services;
using VRCGroupTools.Data.Models;
using System.ComponentModel;
using System.Windows;

namespace VRCGroupTools.ViewModels;

public partial class SecuritySettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ISecurityMonitorService _securityMonitor;
    private readonly IVRChatApiService _apiService;
    private readonly IDiscordWebhookService _discordService;

    // Instance ban settings
    [ObservableProperty]
    private bool _securityMonitorInstanceBans;

    [ObservableProperty]
    private int _securityInstanceBanThreshold;

    [ObservableProperty]
    private int _securityInstanceBanTimeframeMinutes;

    // Group ban settings
    [ObservableProperty]
    private bool _securityMonitorGroupBans;

    [ObservableProperty]
    private int _securityGroupBanThreshold;

    [ObservableProperty]
    private int _securityGroupBanTimeframeMinutes;

    // Role removal settings
    [ObservableProperty]
    private bool _securityMonitorRoleRemovals;

    [ObservableProperty]
    private int _securityRoleRemovalThreshold;

    [ObservableProperty]
    private int _securityRoleRemovalTimeframeMinutes;

    // Invite rejection settings
    [ObservableProperty]
    private bool _securityMonitorInviteRejections;

    [ObservableProperty]
    private int _securityInviteRejectionThreshold;

    [ObservableProperty]
    private int _securityInviteRejectionTimeframeMinutes;

    // Post deletion settings
    [ObservableProperty]
    private bool _securityMonitorPostDeletions;

    [ObservableProperty]
    private int _securityPostDeletionThreshold;

    [ObservableProperty]
    private int _securityPostDeletionTimeframeMinutes;

    // General settings
    [ObservableProperty]
    private bool _securityRequireOwnerRole;

    [ObservableProperty]
    private bool _securityNotifyDiscord;

    [ObservableProperty]
    private bool _securityLogAllActions;

    [ObservableProperty]
    private string _securityOwnerUserId = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isLoadingIncidents;

    [ObservableProperty]
    private ObservableCollection<SecurityIncidentViewModel> _recentIncidents = new();

    public SecuritySettingsViewModel(
        ISettingsService settingsService,
        ISecurityMonitorService securityMonitor,
        IVRChatApiService apiService,
        IDiscordWebhookService discordService)
    {
        _settingsService = settingsService;
        _securityMonitor = securityMonitor;
        _apiService = apiService;
        _discord_service = discordService;

        LoadSettings();

        // Automatically load recent incidents when the view model is created
        // Fire-and-forget so constructor doesn't block UI
        _ = LoadRecentIncidentsAsync();
    }

    private void LoadSettings()
    {
        var settings = _settings_service.Settings;

        SecurityMonitoringEnabled = settings.SecurityMonitoringEnabled;
        SecurityAutoRemoveRoles = settings.SecurityAutoRemoveRoles;
        SecurityAlertWebhookUrl = settings.SecurityAlertWebhookUrl;

        SecurityMonitorInstanceKicks = settings.SecurityMonitorInstanceKicks;
        SecurityInstanceKickThreshold = settings.SecurityInstanceKickThreshold;
        SecurityInstanceKickTimeframeMinutes = settings.SecurityInstanceKickTimeframeMinutes;

        SecurityMonitorGroupKicks = settings.SecurityMonitorGroupKicks;
        SecurityGroupKickThreshold = settings.SecurityGroupKickThreshold;
        SecurityGroupKickTimeframeMinutes = settings.SecurityGroupKickTimeframeMinutes;

        SecurityMonitorInstanceBans = settings.SecurityMonitorInstanceBans;
        SecurityInstanceBanThreshold = settings.SecurityInstanceBanThreshold;
        SecurityInstanceBanTimeframeMinutes = settings.SecurityInstanceBanTimeframeMinutes;

        SecurityMonitorGroupBans = settings.SecurityMonitorGroupBans;
        SecurityGroupBanThreshold = settings.SecurityGroupBanThreshold;
        SecurityGroupBanTimeframeMinutes = settings.SecurityGroupBanTimeframeMinutes;

        SecurityMonitorRoleRemovals = settings.SecurityMonitorRoleRemovals;
        SecurityRoleRemovalThreshold = settings.SecurityRoleRemovalThreshold;
        SecurityRoleRemovalTimeframeMinutes = settings.SecurityRoleRemovalTimeframeMinutes;

        SecurityMonitorInviteRejections = settings.SecurityMonitorInviteRejections;
        SecurityInviteRejectionThreshold = settings.SecurityInviteRejectionThreshold;
        SecurityInviteRejectionTimeframeMinutes = settings.SecurityInviteRejectionTimeframeMinutes;

        SecurityMonitorPostDeletions = settings.SecurityMonitorPostDeletions;
        SecurityPostDeletionThreshold = settings.SecurityPostDeletionThreshold;
        SecurityPostDeletionTimeframeMinutes = settings.SecurityPostDeletionTimeframeMinutes;

        SecurityRequireOwnerRole = settings.SecurityRequireOwnerRole;
        SecurityNotifyDiscord = settings.SecurityNotifyDiscord;
        SecurityLogAllActions = settings.SecurityLogAllActions;
        SecurityOwnerUserId = settings.SecurityOwnerUserId;
    }

    [RelayCommand]
    private async Task LoadRecentIncidentsAsync()
    {
        try
        {
            IsLoadingIncidents = true;
            RecentIncidents.Clear();

            var groupId = _settings_service.Settings.GroupId;
            if (string.IsNullOrEmpty(groupId))
            {
                StatusMessage = "⚠ Please set a Group ID first";
                return;
            }

            var incidents = await _securityMonitor.GetRecentIncidentsAsync(groupId, 30); // Last 30 days

            foreach (var incident in incidents)
            {
                var vm = new SecurityIncidentViewModel(incident, _securityMonitor);

                // Remove resolved incidents from the UI when they are marked resolved
                vm.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(vm.IsResolved) && vm.IsResolved)
                    {
                        try
                        {
                            // Use WPF dispatcher if available
                            var dispatcher = Application.Current?.Dispatcher;
                            if (dispatcher != null)
                            {
                                dispatcher.Invoke(() => { if (RecentIncidents.Contains(vm)) RecentIncidents.Remove(vm); });
                            }
                            else
                            {
                                if (RecentIncidents.Contains(vm)) RecentIncidents.Remove(vm);
                            }
                        }
                        catch
                        {
                            if (RecentIncidents.Contains(vm)) RecentIncidents.Remove(vm);
                        }
                    }
                };

                RecentIncidents.Add(vm);
            }

            StatusMessage = $"Loaded {incidents.Count} recent incidents";
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ Failed to load incidents: {ex.Message}";
            LoggingService.Error("SECURITY-UI", ex, "Failed to load recent incidents");
        }
        finally
        {
            IsLoadingIncidents = false;
        }
    }

    [RelayCommand]
    private void SetCurrentUserAsOwner()
    {
        try
        {
            var currentUserId = _apiService.CurrentUserId;
            if (!string.IsNullOrEmpty(currentUserId))
            {
                SecurityOwnerUserId = currentUserId;
                StatusMessage = $"✓ Owner set to current user: {currentUserId}";
            }
            else
            {
                StatusMessage = "⚠ Current user ID not available";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ Failed to set owner: {ex.Message}";
            LoggingService.Error("SECURITY-UI", ex, "Failed to set current user as owner");
        }
    }

    [RelayCommand]
    private async Task TestWebhookAsync()
    {
        try
        {
            var webhookUrl = SecurityAlertWebhookUrl;
            if (string.IsNullOrEmpty(webhookUrl))
            {
                webhookUrl = _settings_service.Settings.DiscordWebhookUrl;
            }

            if (string.IsNullOrEmpty(webhookUrl))
            {
                StatusMessage = "⚠ Please enter a webhook URL first";
                return;
            }

            // Simple validation
            if (!webhookUrl.StartsWith("https://discord.com/api/webhooks/") &&
                !webhookUrl.StartsWith("https://discordapp.com/api/webhooks/"))
            {
                StatusMessage = "⚠ Invalid Discord webhook URL format";
                return;
            }

            StatusMessage = "Testing webhook...";
            var result = await _discord_service.TestWebhookAsync(webhookUrl);

            if (result.Success)
            {
                StatusMessage = "✅ Webhook connected successfully!";
            }
            else
            {
                var status = result.StatusCode.HasValue ? $" (HTTP {result.StatusCode})" : "";
                var error = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "" : $" {result.ErrorMessage}";
                StatusMessage = $"❌ Failed to connect{status}.{error}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ Error: {ex.Message}";
        }
    }
}
