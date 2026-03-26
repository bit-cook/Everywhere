using System.Diagnostics;
using System.Diagnostics.Metrics;
using Everywhere.Configuration;
using OpenTelemetry.Trace;
using PuppeteerSharp;
using Sentry.OpenTelemetry;
using ZLinq;
using OpenTelemetrySdk = OpenTelemetry.Sdk;

namespace Everywhere.Common;

public static partial class Telemetry
{
    public static bool SendOnlyNecessaryData { get; set; }

    private static SentryMeterListener? _sentryMeterListener;

    public static void Initialize()
    {
        if (string.IsNullOrEmpty(SentryDsn)) return;

        var sentry = SentrySdk.Init(o =>
        {
            o.Dsn = SentryDsn;
            o.AutoSessionTracking = true;
            o.IsGlobalModeEnabled = true;
            o.EnableLogs = true;
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
                    .Any(e => e is
                        OperationCanceledException or
                        TimeoutException or
                        HandledException { IsExpected: true } or
                        PuppeteerException) ? // No one knows why PuppeteerSharp throws so many exceptions and leave them unhandled in Task.Run
                    null :
                    evt);
            o.SetBeforeSendTransaction(transaction => SendOnlyNecessaryData ? null : transaction);
            o.SetBeforeBreadcrumb(breadcrumb => SendOnlyNecessaryData ? null : breadcrumb);
        });

        SentrySdk.ConfigureScope(scope =>
        {
            scope.User.Id = RuntimeConstants.DeviceId;
            scope.User.Username = null;
            scope.User.IpAddress = null;
            scope.User.Email = null;
        });

        var traceProvider = OpenTelemetrySdk.CreateTracerProviderBuilder()
            .AddSource("Everywhere.*")
            .AddSentry()
            .Build();

        // Start listening to System.Diagnostics.Metrics and forwarding to Sentry.
        // Metrics are always sent (anonymous aggregated data, no PII).
        _sentryMeterListener = new SentryMeterListener();

        AppDomain.CurrentDomain.ProcessExit += delegate
        {
            _sentryMeterListener.Dispose();
            traceProvider.Dispose();
            sentry.Dispose();
        };
    }

    /// <summary>
    /// Bridges <see cref="System.Diagnostics.Metrics"/> instruments to Sentry Metrics API.
    /// Listens on all meters whose name starts with "Everywhere." and forwards measurements
    /// to SentrySdk.Experimental.Metrics.
    /// </summary>
    /// <remarks>
    /// Sentry currently has no native OpenTelemetry Metrics export, so we use
    /// <see cref="MeterListener"/> to manually bridge the gap. When Sentry adds native support,
    /// this class can be replaced with a standard OTel MeterProvider exporter.
    /// </remarks>
    private sealed class SentryMeterListener : IDisposable
    {
        private readonly MeterListener _listener;

        public SentryMeterListener()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = OnInstrumentPublished,
            };

            _listener.SetMeasurementEventCallback<byte>(OnMeasurement);
            _listener.SetMeasurementEventCallback<short>(OnMeasurement);
            _listener.SetMeasurementEventCallback<int>(OnMeasurement);
            _listener.SetMeasurementEventCallback<long>(OnMeasurement);
            _listener.SetMeasurementEventCallback<float>(OnMeasurement);
            _listener.SetMeasurementEventCallback<double>(OnMeasurement);

            _listener.Start();
        }

        private static void OnInstrumentPublished(Instrument instrument, MeterListener listener)
        {
            if (instrument.Meter.Name.StartsWith("Everywhere.", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        }

        private static void OnMeasurement<T>(
            Instrument instrument,
            T measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state) where T : struct
        {
            Debug.Assert(tags.AsValueEnumerable().All(kv => kv.Value is not null));

            switch (instrument)
            {
                case Counter<T>:
                {
                    SentrySdk.Experimental.Metrics.EmitCounter(instrument.Name, measurement, tags!); // null values will be ignored by Sentry SDK
                    break;
                }
                case Histogram<T>:
                {
                    SentrySdk.Experimental.Metrics.EmitDistribution(instrument.Name, measurement, MapUnit(instrument.Unit), tags!);
                    break;
                }
                case Gauge<T>:
                {
                    SentrySdk.Experimental.Metrics.EmitGauge(instrument.Name, measurement, MapUnit(instrument.Unit), tags!);
                    break;
                }
            }
        }

        /// <summary>
        /// Maps instrument unit strings to Sentry <see cref="MeasurementUnit"/>.
        /// </summary>
        private static MeasurementUnit MapUnit(string? unit) => unit switch
        {
            "ns" => MeasurementUnit.Duration.Nanosecond,
            "μs" => MeasurementUnit.Duration.Microsecond,
            "ms" => MeasurementUnit.Duration.Millisecond,
            "s" or "second" => MeasurementUnit.Duration.Second,
            "m" or "minute" => MeasurementUnit.Duration.Minute,
            "h" or "hour" => MeasurementUnit.Duration.Hour,
            "d" or "day" => MeasurementUnit.Duration.Day,
            "w" or "week" => MeasurementUnit.Duration.Week,
            "%" or "ratio" => MeasurementUnit.Fraction.Ratio,
            "/" or "percent" => MeasurementUnit.Fraction.Percent,
            null => MeasurementUnit.None,
            _ => MeasurementUnit.Custom(unit),
        };

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}