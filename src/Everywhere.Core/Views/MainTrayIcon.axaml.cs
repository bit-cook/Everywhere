using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common;
using Everywhere.Utilities;

namespace Everywhere.Views;

public class MainTrayIcon : TrayIcon
{
    private readonly App _app;
    private readonly DebounceExecutor<MainTrayIcon, DispatcherTimerImpl> _trayIconClickedDebounce;
    private int _trayIconClickCount;

    public MainTrayIcon(App app)
    {
        _app = app;
        _trayIconClickedDebounce = new DebounceExecutor<MainTrayIcon, DispatcherTimerImpl>(
            () => this,
            sender =>
            {
                if (sender._trayIconClickCount >= 2)
                {
                    sender.HandleOpenMainWindowMenuItemClicked(sender, EventArgs.Empty);
                }
                else
                {
                    sender.HandleOpenChatWindowMenuItemClicked(sender, EventArgs.Empty);
                }

                sender._trayIconClickCount = 0;
            },
            TimeSpan.FromMilliseconds(300)
        );

        AvaloniaXamlLoader.Load(this);
    }

    private void HandleTrayIconClicked(object? sender, EventArgs e)
    {
        _trayIconClickCount++;
        if (_trayIconClickCount >= 2)
        {
            // Double click detected, open main window immediately.
            HandleOpenMainWindowMenuItemClicked(this, EventArgs.Empty);
            _trayIconClickCount = 0;
            _trayIconClickedDebounce.Cancel();
        }
        else
        {
            // Start or reset the debounce timer for single click.
            _trayIconClickedDebounce.Trigger();
        }
    }

    private void HandleOpenChatWindowMenuItemClicked(object? sender, EventArgs e) =>
        WeakReferenceMessenger.Default.Send(new ActivateChatSessionMessage());

    private void HandleOpenMainWindowMenuItemClicked(object? sender, EventArgs e) => _app.ShowMainWindow();

    private void HandleOpenDebugWindowMenuItemClicked(object? sender, EventArgs e) => _app.ShowDebugWindow();

    private void HandleExitMenuItemClicked(object? sender, EventArgs e) => Environment.Exit(0);
}