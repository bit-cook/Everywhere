using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Initialization;

/// <summary>
/// Initializes the chat window hotkey listener and preloads the chat window.
/// </summary>
/// <param name="settings"></param>
/// <param name="shortcutListener"></param>
/// <param name="visualElementContext"></param>
/// <param name="logger"></param>
public class ChatWindowInitializer(
    IServiceProvider serviceProvider,
    Settings settings,
    IShortcutListener shortcutListener,
    IVisualElementContext visualElementContext,
    ILogger<ChatWindowInitializer> logger
) : IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    private readonly Lock _syncLock = new();

    private IDisposable? _chatShortcutSubscription;
    private IDisposable? _textSelectionSubscription;

    public Task InitializeAsync()
    {
        var chatWindow = serviceProvider.GetRequiredService<ChatWindow>();
        var chatWindowViewModel = chatWindow.ViewModel;
        var chatWindowHandle = chatWindow.TryGetPlatformHandle()?.Handle ?? 0;

        // Preload ChatWindow to avoid delay on first open
        chatWindow.Initialize();

        settings.Shortcut.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShortcutSettings.ChatWindow))
            {
                HandleChatShortcutChanged(chatWindow, chatWindowHandle, settings.Shortcut.ChatWindow);
            }
        };
        settings.ChatWindow.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatWindowSettings.AutomaticallyAddTextSelection))
            {
                HandleTextSelectionChanged(chatWindowViewModel, settings.ChatWindow.AutomaticallyAddTextSelection);
            }
        };

        HandleChatShortcutChanged(chatWindow, chatWindowHandle, settings.Shortcut.ChatWindow);
        HandleTextSelectionChanged(chatWindowViewModel, settings.ChatWindow.AutomaticallyAddTextSelection);

        return Task.CompletedTask;
    }

    private void HandleChatShortcutChanged(ChatWindow chatWindow, nint chatWindowHandle, KeyboardShortcut shortcut)
    {
        RegisterShortcutListener(
            shortcut,
            () => ThreadPool.QueueUserWorkItem(_ =>
            {
                IVisualElement? element;
                nint? hWnd;
                try
                {
                    element = visualElementContext.FocusedElement ??
                        visualElementContext.ElementFromPointer()?
                            .GetAncestors(true)
                            .LastOrDefault();
                    hWnd = element?.NativeWindowHandle;
                }
                catch
                {
                    element = null;
                    hWnd = null;
                }

                Dispatcher.UIThread.Invoke(() =>
                {
                    if (chatWindow.IsFocused || chatWindowHandle == hWnd)
                    {
                        WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(true)); // Hide chat window if it's already focused
                    }
                    else
                    {
                        WeakReferenceMessenger.Default.Send(new ActivateChatSessionMessage(element));
                    }
                });
            }),
            ref _chatShortcutSubscription);
    }

    private void RegisterShortcutListener(KeyboardShortcut shortcut, Action callback, ref IDisposable? subscription)
    {
        using var _ = _syncLock.EnterScope();

        subscription?.Dispose();
        if (!shortcut.IsValid) return;

        subscription = shortcutListener.Register(shortcut, callback);
    }

    private void HandleTextSelectionChanged(ChatWindowViewModel chatWindowViewModel, bool isEnabled)
    {
        using var _ = _syncLock.EnterScope();

        _textSelectionSubscription?.Dispose();
        if (isEnabled) _textSelectionSubscription = visualElementContext.Subscribe(chatWindowViewModel);
    }
}