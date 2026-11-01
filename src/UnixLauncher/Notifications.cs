using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace UnixLauncher;

public class NotificationItem
{
    public uint Id { get; init; }
    public string App { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTimeOffset Time { get; init; }
    public string TimeText => Time.LocalDateTime.Date == DateTime.Today ? Time.LocalDateTime.ToString("t") : Time.LocalDateTime.ToString("d MMM");
}

/// <summary>Mirrors every app's Windows toast notifications (Action Center) via UserNotificationListener.</summary>
static class Notifications
{
    public static readonly ObservableCollection<NotificationItem> Items = new();
    public static string Status { get; private set; } = "Loading…";
    public static event Action? Changed;
    public static event Action<NotificationItem>? Arrived;

    static UserNotificationListener? _listener;
    static bool _primed;

    public static async void Start()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                SetStatus("Notification access is off. Turn it on in Windows Settings › Privacy & security › Notifications.");
                return;
            }
            SetStatus("");
            // shortcut: polls every 3 s; the NotificationChanged event needs package identity (MSIX).
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += async (_, _) => await Poll();
            timer.Start();
            await Poll();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            SetStatus("Windows notifications aren't available to this build (see README › Notifications).");
        }
    }

    static void SetStatus(string s) { Status = s; Changed?.Invoke(); }

    static async Task Poll()
    {
        IReadOnlyList<UserNotification> list;
        try { list = await _listener!.GetNotificationsAsync(NotificationKinds.Toast); }
        catch { return; }

        var ids = list.Select(n => n.Id).ToHashSet();
        foreach (var gone in Items.Where(i => !ids.Contains(i.Id)).ToList()) Items.Remove(gone);

        foreach (var n in list.OrderBy(n => n.CreationTime))
        {
            if (Items.Any(i => i.Id == n.Id)) continue;
            var item = Convert(n);
            if (item == null) continue;
            Items.Insert(0, item);
            if (_primed) Arrived?.Invoke(item);
        }
        _primed = true;
        Changed?.Invoke();
    }

    static NotificationItem? Convert(UserNotification n)
    {
        try
        {
            var texts = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements().Select(t => t.Text).ToList() ?? new();
            string app;
            try { app = n.AppInfo.DisplayInfo.DisplayName; } catch { app = "App"; }
            return new NotificationItem
            {
                Id = n.Id, App = app, Time = n.CreationTime,
                Title = texts.FirstOrDefault() ?? app,
                Body = string.Join("\n", texts.Skip(1)),
            };
        }
        catch { return null; }
    }

    public static void Remove(NotificationItem item)
    {
        try { _listener?.RemoveNotification(item.Id); } catch { }
        Items.Remove(item);
        Changed?.Invoke();
    }

    public static void ClearAll()
    {
        try { _listener?.ClearNotifications(); } catch { }
        Items.Clear();
        Changed?.Invoke();
    }
}
