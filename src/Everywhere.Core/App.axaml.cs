using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AttachedProperties;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Views;
using LiveMarkdown.Avalonia;
using Serilog;
using ShadUI;
using Window = Avalonia.Controls.Window;

namespace Everywhere;

public class App : Application, IRecipient<ApplicationCommand>
{
    public static string Version => typeof(TransientWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public static IClipboard Clipboard =>
        _topLevelImpl?.TryGetFeature<IClipboard>() ?? throw new InvalidOperationException("Clipboard is not available.");
    public static IStorageProvider StorageProvider =>
        _topLevelImpl?.TryGetFeature<IStorageProvider>() ?? throw new InvalidOperationException("StorageProvider is not available.");
    public static ILauncher Launcher =>
        _topLevelImpl?.TryGetFeature<ILauncher>() ?? throw new InvalidOperationException("Launcher is not available.");
    public static IScreenImpl ScreenImpl =>
        _topLevelImpl?.TryGetFeature<IScreenImpl>() ?? throw new InvalidOperationException("ScreenImpl is not available.");

    public static ThemeManager ThemeManager => _themeManager ?? throw new InvalidOperationException("Application is not initialized.");

    private static ITopLevelImpl? _topLevelImpl;
    private static ThemeManager? _themeManager;

    private TransientWindow? _mainWindow, _debugWindow;

    public override void Initialize()
    {
        InitializeErrorHandler();

        AvaloniaXamlLoader.Load(this);

        _topLevelImpl = new Window().PlatformImpl ?? throw new InvalidOperationException("Application is not initialized correctly.");

#if DEBUG
        if (Design.IsDesignMode)
        {
            ServiceLocator.Build(x => x.AddAvaloniaBasicServices());
            return;
        }
#endif

        // After this, ThemeChanged event from the system can be received
        _themeManager = new ThemeManager(this);

        // Register to receive application commands
        // e.g. ShowMainWindow
        WeakReferenceMessenger.Default.Register(this);

        InitializeMarkdown();
        InitializeApp();

        TrayIcon.SetIcons(this, [new MainTrayIcon(this)]);

        RecordAppLaunchMetric();
    }

    private static void InitializeErrorHandler()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Logger.Error(e.Exception, "UI Thread Unhandled Exception");

            NativeMessageBox.Show(
                "Unexpected Error",
                $"An unexpected error occurred:\n{e.Exception.Message}\n\nPlease check the logs for more details.",
                NativeMessageBoxButtons.Ok,
                NativeMessageBoxIcon.Error);

            e.Handled = true;
        };
    }

    private static void InitializeMarkdown()
    {
        AsyncImageLoader.DefaultDecoders =
        [
            SvgImageDecoder.Shared,
            DefaultBitmapDecoder.Shared
        ];

        MarkdownNode.Register<MathInlineNode>();
        MarkdownNode.Register<MathBlockNode>();

        MarkdownRenderer.ConfigurePipeline += x => x.UseMermaid();
        MarkdownNode.Register<MermaidBlockNode>();
    }

    private static void InitializeApp()
    {
        try
        {
            foreach (var group in ServiceLocator
                         .Resolve<IEnumerable<IAsyncInitializer>>()
                         .GroupBy(i => i.Index)
                         .OrderBy(g => g.Key))
            {
                Task.WhenAll(group.Select(i => i.InitializeAsync())).WaitOnDispatcherFrame();
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Fatal(ex, "Failed to initialize application");

            NativeMessageBox.Show(
                "Initialization Error",
                $"An error occurred during application initialization:\n{ex.Message}\n\nPlease check the logs for more details.",
                NativeMessageBoxButtons.Ok,
                NativeMessageBoxIcon.Error);
        }
    }

    private static void RecordAppLaunchMetric()
    {
        const string OsType =
#if WINDOWS
            "Windows";
#elif LINUX
                "Linux";
#elif MACOS
                "macOS";
#else
                "Unknown";
#endif

        using var meter = new Meter(typeof(App).FullName.NotNull(), Version);
        meter.CreateCounter<int>("app.launches").Add(
            1,
            new TagList
            {
                { "os.type", OsType },
                { "os.description", RuntimeInformation.OSDescription },
                { "app.version", Version },
                { "user.id", RuntimeConstants.DeviceId }
            });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime:
            {
                DisableAvaloniaDataAnnotationValidation();
                ShowMainWindowOnNeeded();
                break;
            }
        }
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToList();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }

    /// <summary>
    /// Show the main window if it was not shown before or the version has changed.
    /// </summary>
    private void ShowMainWindowOnNeeded()
    {
        // If the --ui command line argument is present, show the main window.
        if (Environment.GetCommandLineArgs().Contains("--ui"))
        {
            ShowWindow<MainView>(ref _mainWindow);
            return;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        var persistentState = ServiceLocator.Resolve<PersistentState>();
        if (persistentState.PreviousLaunchVersion == version) return;

        ShowWindow<MainView>(ref _mainWindow);
    }


    /// <summary>
    /// Flag to prevent multiple calls to ShowWindow method from event loop.
    /// </summary>
    private static bool _isShowWindowBusy;

    private static void ShowWindow<TContent>(ref TransientWindow? window) where TContent : Control
    {
        if (_isShowWindowBusy) return;
        try
        {
            _isShowWindowBusy = true;
            if (window is { IsVisible: true })
            {
                if (window.WindowState is WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                var windowHelper = ServiceLocator.Resolve<IWindowHelper>();
                windowHelper.SetCloaked(window, false);
            }
            else
            {
                window?.Close();
                var content = ServiceLocator.Resolve<TContent>();
                content.To<ISetLogicalParent>().SetParent(null);
                window = new TransientWindow
                {
                    [SaveWindowPlacementAssist.KeyProperty] = typeof(TContent).FullName,
                    Content = content
                };
                window.Show();
            }
        }
        finally
        {
            _isShowWindowBusy = false;
        }
    }

    public void ShowMainWindow() => ShowWindow<MainView>(ref _mainWindow);

    public void ShowDebugWindow() => ShowWindow<VisualTreeDebugger>(ref _debugWindow);

    public void Receive(ApplicationCommand command)
    {
        if (command is ShowWindowCommand { Name: nameof(MainView) })
        {
            Dispatcher.UIThread.Invoke(() => ShowWindow<MainView>(ref _mainWindow));
        }
    }
}