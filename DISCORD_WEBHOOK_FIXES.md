# Discord Webhook Fixes & Debugging

## Changes Made

### 1. **Auto-Save Functionality Added**
Previously, users had to manually click "Save Settings" after checking Discord notification options. Now settings are **automatically saved** when any checkbox is toggled.

**Files Modified:**
- `src/ViewModels/DiscordSettingsViewModel.cs`
  - Added `_isLoading` flag to prevent saves during initialization
  - Added 25+ partial methods (`OnNotifyUserJoinsChanged`, etc.) that auto-save on change
  - Added `AutoSaveSettings()` method

### 2. **Comprehensive Debug Logging**
Added extensive console logging to trace the entire Discord webhook flow:

**DiscordWebhookService.cs:**
- Logs when `SendAuditEventAsync` is called with event type
- Logs webhook configuration status (IsConfigured)
- Logs whether each event type is enabled/disabled in settings
- Logs HTTP status codes and error responses from Discord API
- Logs successful/failed message sends

**AuditLogService.cs:**
- Logs count of truly new logs detected
- Logs Discord configuration status
- Logs each notification being processed
- Logs when Discord is not configured

**SettingsService.cs:**
- Logs file path when saving
- Logs webhook URL (truncated) after save
- Logs sample notification settings values

### 3. **Key Improvements**
- Settings are now persisted immediately when changed
- Better error visibility through console logging
- Can diagnose issues by checking console output

## How Discord Notifications Work

### Important: Only NEW Events Trigger Notifications
Discord notifications are **only sent for newly detected audit log entries**, not for existing/historical logs. The system:

1. **Polls VRChat API every 60 seconds** for new audit logs
2. **Compares** new logs against existing database entries
3. **Sends Discord webhooks** only for logs that don't exist in the database yet
4. **Rate limits** to max 10 notifications per poll (to prevent spam)

### Testing Discord Notifications

To properly test Discord webhooks:

1. **Configure Webhook URL**
   - Go to Discord Settings in the app
   - Enter your webhook URL from Discord
   - Click "Test Webhook" to verify connection

2. **Enable Event Types**
   - Check the boxes for events you want to monitor
   - Settings auto-save when you toggle checkboxes (no need to click Save)

3. **Generate NEW Events**
   - Notifications only fire for **NEW** events that happen **after** the app starts polling
   - Perform an action in your VRChat group (kick someone, update a role, etc.)
   - Wait up to 60 seconds for the next polling cycle
   - Check console output for detailed logging

4. **Check Console Output**
   Look for these log patterns:
   ```
   [AUDIT-SVC] Truly new logs count: X, Discord configured: True
   [AUDIT-SVC] Sending X Discord notifications...
   [DISCORD] SendAuditEventAsync called - EventType: group.user.kick
   [DISCORD] Event type 'group.user.kick' shouldSend: True
   [DISCORD] Sending message: 👢 Member Kicked
   [DISCORD] HTTP Status: 204
   [DISCORD] Message send result: True
   ```

## Troubleshooting

### Issue: "Webhook not configured"
**Cause:** Webhook URL is empty or not saved
**Solution:** 
- Verify webhook URL is entered
- Check console: `[SETTINGS] Saving to: C:\Users\...\settings.json`
- Open settings.json and verify `DiscordWebhookUrl` has a value

### Issue: "Event type is disabled in settings"
**Cause:** The specific event type checkbox is unchecked
**Solution:**
- Go to Discord settings
- Check the box for the event type you want
- Settings auto-save immediately

### Issue: "No truly new logs"
**Cause:** No new events have occurred since last poll
**Solution:**
- Perform an action in your VRChat group
- Wait for next polling cycle (up to 60 seconds)
- Check if new audit logs appear in the Audit Logs tab

### Issue: "HTTP Status: 404" or other error
**Cause:** Invalid webhook URL or Discord webhook was deleted
**Solution:**
- Verify webhook URL is correct
- Test webhook using "Test Webhook" button
- Check that webhook still exists in Discord

## Console Log Examples

### Successful Notification Flow
```
[AUDIT-SVC] Truly new logs count: 1, Discord configured: True
[AUDIT-SVC] Sending 1 Discord notifications (max 10)...
[AUDIT-SVC] Processing log 1: EventType=group.user.kick, Actor=ModeratorName
[DISCORD] SendAuditEventAsync called - EventType: group.user.kick, IsConfigured: True
[DISCORD] Webhook URL: https://discord.com/api/webhooks/1234567890...
[DISCORD] Event type 'group.user.kick' shouldSend: True
[DISCORD] Sending message: 👢 Member Kicked
[DISCORD] SendMessageAsync called - Title: 👢 Member Kicked
[DISCORD] HTTP Status: 204
[DISCORD] Message send result: True
[AUDIT-SVC] Completed sending 1 Discord notifications
```

### Event Disabled in Settings
```
[DISCORD] SendAuditEventAsync called - EventType: group.user.join, IsConfigured: True
[DISCORD] Event type 'group.user.join' shouldSend: False
[DISCORD] Event type 'group.user.join' is disabled in settings, skipping
```

### Webhook Not Configured
```
[AUDIT-SVC] Truly new logs count: 5, Discord configured: False
[AUDIT-SVC] Discord webhook not configured - skipping 5 notifications
```

## Files Modified Summary

1. `src/Services/DiscordWebhookService.cs` - Added debug logging throughout
2. `src/Services/AuditLogService.cs` - Added notification flow logging
3. `src/Services/SettingsService.cs` - Added save operation logging
4. `src/ViewModels/DiscordSettingsViewModel.cs` - Added auto-save functionality

## Next Steps

1. Run the application
2. Open Discord settings and configure webhook
3. Enable desired event types
4. Monitor console output (if running from VS Code/terminal)
5. Perform test actions in VRChat group
6. Verify notifications arrive in Discord channel
