using System.Diagnostics;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Telemetry;

public sealed class RuntimeTelemetry : IDisposable
{
    public const string ActivitySourceName = "SwarmAssistant.Runtime";

    private readonly ILogger _logger;
    private readonly string _profile;
    private readonly TracerProvider? _tracerProvider;

    public RuntimeTelemetry(RuntimeOptions options, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<RuntimeTelemetry>();
        _profile = options.Profile;
        ActivitySource = new ActivitySource(ActivitySourceName);

        if (!options.LangfuseTracingEnabled)
        {
            _logger.LogInformation("Langfuse tracing disabled by configuration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.LangfusePublicKey) || string.IsNullOrWhiteSpace(options.LangfuseSecretKey))
        {
            _logger.LogWarning(
                "Langfuse tracing enabled but keys are missing. Set Runtime__LangfusePublicKey and Runtime__LangfuseSecretKey.");
            return;
        }

        var endpoint = string.IsNullOrWhiteSpace(options.LangfuseOtlpEndpoint)
            ? $"{options.LangfuseBaseUrl.TrimEnd('/')}/api/public/otel/v1/traces"
            : options.LangfuseOtlpEndpoint;

        try
        {
            _tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(
                    ResourceBuilder
                        .CreateDefault()
                        .AddService("SwarmAssistant.Runtime")
                        .AddAttributes(new[]
                        {
                            new KeyValuePair<string, object>("deployment.environment", options.Profile),
                            new KeyValuePair<string, object>("langfuse.environment", options.Profile),
                        }))
                .AddSource(ActivitySourceName)
                .AddOtlpExporter(otlp =>
                {
                    otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    otlp.Endpoint = new Uri(endpoint);
                    otlp.Headers = $"Authorization=Basic {BuildBasicAuth(options.LangfusePublicKey, options.LangfuseSecretKey)}";
                })
                .Build();

            _logger.LogInformation("Langfuse OTLP tracing enabled endpoint={Endpoint}", endpoint);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to initialize Langfuse OTLP tracing endpoint={Endpoint}", endpoint);
        }
    }

    public ActivitySource ActivitySource { get; }

    public Activity? StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal,
        string? taskId = null,
        string? role = null,
        IDictionary<string, object?>? tags = null)
    {
        var activity = ActivitySource.StartActivity(name, kind);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("swarm.profile", _profile);

        if (!string.IsNullOrWhiteSpace(taskId))
        {
            activity.SetTag("swarm.task.id", taskId);
            activity.SetTag("langfuse.session.id", taskId);
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            activity.SetTag("swarm.role", role);
        }

        if (tags is not null)
        {
            foreach (var (key, value) in tags)
            {
                activity.SetTag(key, value);
            }
        }

        return activity;
    }

    public void Dispose()
    {
        _tracerProvider?.Dispose();
        ActivitySource.Dispose();
    }

    private static string BuildBasicAuth(string publicKey, string secretKey)
    {
        var bytes = Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}");
        return Convert.ToBase64String(bytes);
    }
}
