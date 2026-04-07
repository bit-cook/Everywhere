using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
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

    private IDisposable? _chatWindowShortcutSubscription;
    private IDisposable? _pickElementShortcutSubscription;
    private IDisposable? _screenshotShortcutSubscription;
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
            switch (args.PropertyName)
            {
                case nameof(ShortcutSettings.ChatWindow):
                    HandleChatWindowShortcutChanged(chatWindow, chatWindowHandle, settings.Shortcut.ChatWindow);
                    break;
                case nameof(ShortcutSettings.PickVisualElement):
                    HandlePickElementShortcutChanged(chatWindowViewModel, settings.Shortcut.PickVisualElement);
                    break;
                case nameof(ShortcutSettings.TakeScreenshot):
                    HandleScreenshotShortcutChanged(chatWindowViewModel, settings.Shortcut.TakeScreenshot);
                    break;
            }
        };
        settings.ChatWindow.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatWindowSettings.AutomaticallyAddTextSelection))
            {
                HandleTextSelectionChanged(chatWindowViewModel, settings.ChatWindow.AutomaticallyAddTextSelection);
            }
        };

        HandleChatWindowShortcutChanged(chatWindow, chatWindowHandle, settings.Shortcut.ChatWindow);
        HandlePickElementShortcutChanged(chatWindowViewModel, settings.Shortcut.PickVisualElement);
        HandleScreenshotShortcutChanged(chatWindowViewModel, settings.Shortcut.TakeScreenshot);
        HandleTextSelectionChanged(chatWindowViewModel, settings.ChatWindow.AutomaticallyAddTextSelection);

        return Task.CompletedTask;
    }

    private void HandleChatWindowShortcutChanged(ChatWindow chatWindow, nint chatWindowHandle, KeyboardShortcut shortcut)
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
                    if (chatWindowHandle == hWnd) element = null; // Don't allow to select itself
                }
                catch
                {
                    element = null;
                    hWnd = null;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (chatWindow.IsVisible && chatWindowHandle == hWnd)
                    {
                        WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(true)); // Hide chat window if it's already focused
                    }
                    else
                    {
                        WeakReferenceMessenger.Default.Send(new ActivateChatSessionMessage(element));
                    }
                });
            }),
            ref _chatWindowShortcutSubscription);
    }

    private void HandlePickElementShortcutChanged(ChatWindowViewModel chatWindowViewModel, KeyboardShortcut shortcut)
    {
        RegisterShortcutListener(
            shortcut,
            () => Dispatcher.UIThread.Post(() => chatWindowViewModel.PickVisualElementCommand.Execute(null)),
            ref _pickElementShortcutSubscription);
    }

    private void HandleScreenshotShortcutChanged(ChatWindowViewModel chatWindowViewModel, KeyboardShortcut shortcut)
    {
        RegisterShortcutListener(
            shortcut,
            () => Dispatcher.UIThread.Post(() => chatWindowViewModel.TakeScreenshotCommand.Execute(null)),
            ref _screenshotShortcutSubscription);
    }

    private void RegisterShortcutListener(KeyboardShortcut shortcut, Action callback, ref IDisposable? subscription)
    {
        using var _ = _syncLock.EnterScope();

        subscription?.Dispose();
        if (!shortcut.IsValid) return;

        try
        {
            subscription = shortcutListener.Register(shortcut, callback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register shortcut {Shortcut}", shortcut);
        }
    }

    private void HandleTextSelectionChanged(ChatWindowViewModel chatWindowViewModel, bool isEnabled)
    {
        using var _ = _syncLock.EnterScope();

        _textSelectionSubscription?.Dispose();
        if (isEnabled) _textSelectionSubscription = visualElementContext.Subscribe(chatWindowViewModel);
    }
}