#if DEBUG
#define DISABLE_TELEMETRY
using Avalonia.Controls;
#endif

using System.Diagnostics;
using System.IO.Pipes;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Interop;
using Everywhere.Views;
using MessagePack;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Sentry.OpenTelemetry;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;
using ZLinq;

namespace Everywhere.Common;

public static class Entrance
{
    public static bool SendOnlyNecessaryTelemetry { get; set; }

    public static event EventHandler<UnobservedTaskExceptionEventArgs>? UnobservedTaskExceptionFilter;

    private static Mutex? _appMutex;

    private const string BundleName = "com.sylinko.everywhere";

    /// <summary>
    /// Releases the application mutex. Only call this method when the application is exiting.
    /// </summary>
    public static void ReleaseMutex()
    {
        if (_appMutex == null) return;

        _appMutex.ReleaseMutex();
        _appMutex.Dispose();
        _appMutex = null;
    }

    public static void Initialize()
    {
#if !DISABLE_TELEMETRY
        InitializeTelemetry();
#endif
        InitializeLogger();
        InitializeErrorHandling();
    }

    /// <summary>
    /// Initializes the application mutex to ensure a single instance of the application.
    /// </summary>
    public static void InitializeSingleInstance()
    {
#if DEBUG
        if (Design.IsDesignMode) return;
#endif

        _appMutex = new Mutex(true, BundleName, out var createdNew);
        if (createdNew)
        {
            Task.Run(StartHostPipeServer);
            return;
        }

        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--autorun"))
        {
            // Bring the existing instance to the foreground.
            SendToHost(new ShowWindowCommand(nameof(MainView))).GetAwaiter().GetResult();
        }

        Environment.Exit(0);
    }

    private static async Task StartHostPipeServer()
    {
        var retryCount = 3;
        while (retryCount > 0)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    BundleName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync();

                var lengthBuffer = new byte[4];
                if (await server.ReadAsync(lengthBuffer.AsMemory(0, 4)) != 4) continue;

                var length = BitConverter.ToInt32(lengthBuffer, 0);
                var buffer = new byte[length];
                if (await server.ReadAsync(buffer.AsMemory(0, length)) != length) continue;

                try
                {
                    var command = MessagePackSerializer.Deserialize<ApplicationCommand>(buffer);
                    WeakReferenceMessenger.Default.Send(command);
                }
                catch (Exception ex)
                {
                    Log.ForContext(typeof(Entrance)).Error(ex, "Failed to deserialize host command.");
                }
            }
            catch (Exception ex)
            {
                Log.ForContext(typeof(Entrance)).Error(ex, "Host pipe server error.");

                retryCount--;
                await Task.Delay(1000);
            }
        }
    }

    private static async Task SendToHost(ApplicationCommand command)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", BundleName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(1000);

            var bytes = MessagePackSerializer.Serialize(command);
            var lengthBytes = BitConverter.GetBytes(bytes.Length);

            await client.WriteAsync(lengthBytes);
            await client.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send command to host instance.");

            // Show message box if the command is ShowMainWindowCommand as a fallback
            if (command is ShowWindowCommand)
            {
                NativeMessageBox.Show(
                    LocaleResolver.Common_Info,
                    LocaleResolver.Entrance_EverywhereAlreadyRunning,
                    NativeMessageBoxButtons.Ok,
                    NativeMessageBoxIcon.Information);
            }
        }
    }

    private static void InitializeTelemetry()
    {
        var sentry = SentrySdk.Init(o =>
        {
            o.Dsn = "https://25114ca299b74da64aed26ffc2ac072e@o4510145762689024.ingest.us.sentry.io/4510145814069248";
            o.AutoSessionTracking = true;
            o.Experimental.EnableLogs = true;
#if DEBUG
            o.TracesSampleRate = 1.0;
            o.Environment = "debug";
            o.Debug = true;
#else
            o.TracesSampleRate = 0.2;
            o.Environment = "stable";
#endif
            o.Release = typeof(Entrance).Assembly.GetName().Version?.ToString();

            o.UseOpenTelemetry();
            o.SetBeforeSend(evt =>
                evt.Exception.Segregate()
                    .AsValueEnumerable()
                    .Any(e => e is not OperationCanceledException and not HandledException { IsExpected: true }) ?
                    evt :
                    null);
            o.SetBeforeSendTransaction(transaction => SendOnlyNecessaryTelemetry ? null : transaction);
            o.SetBeforeBreadcrumb(breadcrumb => SendOnlyNecessaryTelemetry ? null : breadcrumb);
        });

        SentrySdk.ConfigureScope(scope =>
        {
            scope.User.Username = null;
            scope.User.IpAddress = null;
            scope.User.Email = null;
        });

        var traceProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("Everywhere.*")
            .AddSentry()
            .Build();

        AppDomain.CurrentDomain.ProcessExit += delegate
        {
            traceProvider.Dispose();
            sentry.Dispose();
        };
    }

    private static void InitializeLogger()
    {
        var dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everywhere");

        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.With<ActivityEnricher>()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                new JsonFormatter(),
                Path.Combine(dataPath, "logs", ".jsonl"),
                rollingInterval: RollingInterval.Day)
#if !DISABLE_TELEMETRY
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(logEvent =>
                    logEvent.Properties.TryGetValue("SourceContext", out var sourceContextValue) &&
                    sourceContextValue.As<ScalarValue>()?.Value?.ToString()?.StartsWith("Everywhere.") is true)
                .WriteTo.Sentry(LogEventLevel.Error, LogEventLevel.Information))
#endif
            .CreateLogger();
    }

    private static void InitializeErrorHandling()
    {
        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            Log.Logger.Error(e.ExceptionObject as Exception, "Unhandled Exception");
        };

        TaskScheduler.UnobservedTaskException += static (s, e) =>
        {
            UnobservedTaskExceptionFilter?.Invoke(s, e);
            if (e.Observed) return;

            Log.Logger.Error(e.Exception, "Unobserved Task Exception");
            e.SetObserved();
        };
    }

    private sealed class ActivityEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            if (Activity.Current is not { } activity) return;

            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty(
                    nameof(activity.TraceId),
                    activity.TraceId)
            );
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty(
                    nameof(activity.SpanId),
                    activity.SpanId)
            );
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty(
                    "ActivityId",
                    activity.Id)
            );
        }
    }
}