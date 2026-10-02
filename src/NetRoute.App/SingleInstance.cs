using System.Windows;

namespace NetRoute.App;

/// One widget per user session. A second launch asks the first one to show its card.
sealed class SingleInstance : IDisposable
{
    const string MutexName = @"Local\NetRouteWidget";
    const string ShowEventName = @"Local\NetRouteWidget.Show";

    Mutex? _mutex;
    EventWaitHandle? _show;
    RegisteredWaitHandle? _registration;

    /// waitForPrevious: used by "Restart as admin", where the old instance is still shutting down.
    public bool TryAcquire(bool waitForPrevious)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew && !WaitForOwner(mutex, waitForPrevious))
            {
                mutex.Dispose();
                SignalExisting();
                return false;
            }
            _mutex = mutex;
        }
        catch (UnauthorizedAccessException)
        {
            SignalExisting(); // the running instance is elevated; we are not
            return false;
        }

        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        return true;
    }

    public void ListenForShow(Action onShow) =>
        _registration = ThreadPool.RegisterWaitForSingleObject(_show!, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _registration?.Unregister(null);
        _show?.Dispose();
        if (_mutex is null) return;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    static bool WaitForOwner(Mutex mutex, bool waitForPrevious)
    {
        if (!waitForPrevious) return false;
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    static void SignalExisting()
    {
        try
        {
            using var show = EventWaitHandle.OpenExisting(ShowEventName);
            show.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            MessageBox.Show("NetRoute Widget is already running. Use its icon in the system tray (next to the clock).",
                "NetRoute Widget", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
