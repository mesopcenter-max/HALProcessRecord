using System.Collections.Concurrent;
using System.Threading.Channels;
using HALProcessRecord.Data;
using HALProcessRecord.Models;
using HALProcessRecord.Utilities;
using Microsoft.EntityFrameworkCore;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using UaStatusCodes = Opc.Ua.StatusCodes;

namespace HALProcessRecord.Services;

public sealed class OpcUaSubscriptionService : BackgroundService, IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OpcUaSubscriptionService> _logger;
    private readonly ConcurrentDictionary<string, EndpointSubscription> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<SignalNotification> _notifications = Channel.CreateUnbounded<SignalNotification>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private CancellationTokenSource? _stopping;
    private Task? _writerTask;

    public OpcUaSubscriptionService(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<OpcUaSubscriptionService> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_stopping is not null)
            return;

        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _writerTask = PersistNotificationsAsync(_stopping.Token);

        foreach (var endpoint in GetEndpoints())
            _ = ConnectAndSubscribeAsync(endpoint, _stopping.Token);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ConnectAndSubscribeAsync(EndpointDefinition endpoint, CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(Math.Clamp(
            _configuration.GetValue("OpcUa:SubscriptionRetryIntervalSeconds", 3),
            1,
            300));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndSubscribeOnceAsync(endpoint, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Unable to subscribe to OPC UA endpoint {Endpoint} ({Url}); retrying in {RetrySeconds} seconds.",
                    endpoint.Name,
                    endpoint.Url,
                    retryDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ConnectAndSubscribeOnceAsync(EndpointDefinition endpoint, CancellationToken cancellationToken)
    {
        Session? session = null;
        Subscription? subscription = null;

        try
        {
            var applicationConfiguration = CreateConfiguration();
            await applicationConfiguration.ValidateAsync(ApplicationType.Client);
            var application = new ApplicationInstance(applicationConfiguration, null);
            await application.CheckApplicationInstanceCertificatesAsync(false, 2048);

            var operationTimeout = _configuration.GetValue("OpcUa:OperationTimeoutMs", 5000);
            var selectedEndpoint = await CoreClientUtils.SelectEndpointAsync(
                applicationConfiguration,
                endpoint.Url,
                false,
                operationTimeout,
                null!,
                cancellationToken);

            if (selectedEndpoint is null)
                throw new InvalidOperationException($"No OPC UA endpoint was found at {endpoint.Url}.");

            var endpointConfiguration = EndpointConfiguration.Create(applicationConfiguration);
            endpointConfiguration.OperationTimeout = operationTimeout;
            var configuredEndpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);

#pragma warning disable CS0618
            session = await Session.CreateAsync(
                null,
                applicationConfiguration,
                (ITransportWaitingConnection?)null,
                configuredEndpoint,
                false,
                false,
                "HAL Process Record Signal Subscription",
                (uint)_configuration.GetValue("OpcUa:SessionTimeoutMs", 60000),
                null,
                null,
                DiagnosticsMasks.None,
                cancellationToken);
#pragma warning restore CS0618

            var connectionLost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.KeepAlive += (_, args) =>
            {
                if (!ServiceResult.IsBad(args.Status))
                    return;

                _logger.LogWarning(
                    "OPC UA keep-alive failed for {Endpoint}: {Status}; reconnecting.",
                    endpoint.Name,
                    args.Status);
                connectionLost.TrySetResult(args.Status?.ToString() ?? "Bad OPC UA status");
            };

            subscription = new Subscription(session.DefaultSubscription)
            {
                PublishingInterval = _configuration.GetValue("OpcUa:SubscriptionPublishingIntervalMs", 10),
                LifetimeCount = 10000,
                MaxNotificationsPerPublish = 1000,
                Priority = 100
            };

            foreach (var signal in endpoint.Signals)
            {
                var item = new MonitoredItem(subscription.DefaultItem)
                {
                    StartNodeId = signal.NodeId,
                    DisplayName = signal.Name,
                    SamplingInterval = _configuration.GetValue("OpcUa:SubscriptionSamplingIntervalMs", 0),
                    QueueSize = (uint)_configuration.GetValue("OpcUa:SubscriptionQueueSize", 1000),
                    DiscardOldest = false,
                    MonitoringMode = MonitoringMode.Reporting
                };
                item.Notification += (_, args) => OnNotification(endpoint, signal, args);
                subscription.AddItem(item);
            }

            session.AddSubscription(subscription);
            await subscription.CreateAsync(cancellationToken);
            _subscriptions[endpoint.Name] = new EndpointSubscription(session, subscription);
            _logger.LogInformation("OPC UA subscription started for {Endpoint} with {Count} monitored items.", endpoint.Name, endpoint.Signals.Count);
            await Task.WhenAny(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken), connectionLost.Task);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"OPC UA keep-alive failed: {await connectionLost.Task}");
        }
        finally
        {
            if (session is not null)
            {
                if (_subscriptions.TryGetValue(endpoint.Name, out var active) && ReferenceEquals(active.Session, session))
                    _subscriptions.TryRemove(endpoint.Name, out _);

                try
                {
                    if (subscription is not null)
                    {
                        await subscription.DeleteAsync(true, CancellationToken.None);
                    }
                    await session.CloseAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error while closing OPC UA subscription for {Endpoint}.", endpoint.Name);
                }

                session.Dispose();
            }
        }
    }

    private void OnNotification(EndpointDefinition endpoint, SignalDefinition signal, MonitoredItemNotificationEventArgs args)
    {
        if (args.NotificationValue is not MonitoredItemNotification notification || notification.Value is null)
            return;

        var value = notification.Value;
        if (value.StatusCode != UaStatusCodes.Good)
            return;

        var receivedAt = IndiaTime.Now;
        var sourceTimestamp = IndiaTime.NormalizeOpcSourceTimestamp(value.SourceTimestamp, receivedAt);

        if (!_notifications.Writer.TryWrite(new SignalNotification(
                endpoint.Kind,
                endpoint.Name,
                signal.Name,
                value.Value,
                sourceTimestamp,
                receivedAt)))
        {
            _logger.LogWarning("OPC UA notification queue is closed; signal notification was dropped for {Endpoint}/{Signal}.", endpoint.Name, signal.Name);
        }
    }

    private async Task PersistNotificationsAsync(CancellationToken cancellationToken)
    {
        var snapshots = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
        await foreach (var notification in _notifications.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                var snapshot = snapshots.TryGetValue(notification.Endpoint, out var existing)
                    ? existing
                    : new Snapshot();
                snapshot.Apply(notification);
                snapshots[notification.Endpoint] = snapshot;

                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (notification.Kind == EndpointKind.Furnace)
                {
                    db.FurnaceData.Add(new FurnaceDataEntity
                    {
                        Furnace = notification.Endpoint,
                        DoorClosed = snapshot.DoorClosed,
                        DoorOpen = snapshot.DoorOpen,
                        HalfDoor = false,
                        Code = string.Empty,
                        DoorOpenSourceTimestamp = snapshot.DoorOpenTimestamp,
                        DoorClosedSourceTimestamp = snapshot.DoorClosedTimestamp,
                        SourceReadAt = notification.ReceivedAt,
                        CapturedAt = IndiaTime.Now
                    });
                }
                else
                {
                    db.MachineData.Add(new MachineDataEntity
                    {
                        Machine = notification.Endpoint,
                        AutoStartLed = snapshot.AutoStartLed,
                        AutoStopLed = snapshot.AutoStopLed,
                        AutoStartPushButton = snapshot.AutoStartPushButton,
                        AutoStopPushButton = snapshot.AutoStopPushButton,
                        MaterialForce = snapshot.MaterialForce,
                        MaterialTemperature = snapshot.MaterialTemperature,
                        AutoStartLedSourceTimestamp = snapshot.AutoStartLedTimestamp,
                        AutoStopLedSourceTimestamp = snapshot.AutoStopLedTimestamp,
                        AutoStartPushButtonSourceTimestamp = snapshot.AutoStartPushButtonTimestamp,
                        AutoStopPushButtonSourceTimestamp = snapshot.AutoStopPushButtonTimestamp,
                        SourceReadAt = notification.ReceivedAt,
                        CapturedAt = IndiaTime.Now
                    });
                }

                await db.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unable to persist OPC UA notification for {Endpoint}/{Signal}; continuing with subsequent notifications.",
                    notification.Endpoint,
                    notification.Signal);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private IEnumerable<EndpointDefinition> GetEndpoints()
    {
        foreach (var item in _configuration.GetSection("OpcUa:FurnaceEndpoints").GetChildren())
        {
            if (!AllowedFurnaces.Contains(item.Key, StringComparer.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(item.Value))
                continue;
            yield return new EndpointDefinition(item.Key, item.Value, EndpointKind.Furnace, new List<SignalDefinition>
            {
                new("DoorOpen", GetSetting("DoorOpenNodeId", "ns=4;i=6")),
                new("DoorClosed", GetSetting("DoorCloseNodeId", "ns=4;i=5"))
            });
        }

        foreach (var item in _configuration.GetSection("OpcUa:RingRollingEndpoints").GetChildren())
        {
            if (string.IsNullOrWhiteSpace(item.Value))
                continue;
            yield return new EndpointDefinition(item.Key, item.Value, EndpointKind.Machine, new List<SignalDefinition>
            {
                new("AutoStartLed", GetRingSetting("AutoStartLEDNodeId", "ns=4;i=3")),
                new("AutoStopLed", GetRingSetting("AutoStopLEDNodeId", "ns=4;i=5")),
                new("AutoStartPushButton", GetRingSetting("AutoStartPBNodeId", "ns=4;i=2")),
                new("AutoStopPushButton", GetRingSetting("AutoStopPBNodeId", "ns=4;i=4")),
                new("MaterialForce", GetRingSetting("MaterialForceNodeId", "ns=4;i=30")),
                new("MaterialTemperature", GetRingSetting("MaterialTemperatureNodeId", "ns=4;i=9"))
            });
        }
    }

    private ApplicationConfiguration CreateConfiguration()
    {
        var autoAccept = _configuration.GetValue("OpcUa:AutoAcceptUntrustedCertificates", true);
        var config = new ApplicationConfiguration
        {
            ApplicationName = "HAL Process Record Subscriptions",
            ApplicationUri = $"urn:{Utils.GetHostName()}:HALProcessRecordSubscriptions",
            ProductUri = "urn:HAL:HALProcessRecordSubscriptions",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier { StoreType = "Directory", StorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HALProcessRecord", "CertificateStores", "UA Applications"), SubjectName = "CN=HAL Process Record Subscriptions" },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HALProcessRecord", "CertificateStores", "TrustedPeers") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HALProcessRecord", "CertificateStores", "TrustedIssuers") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HALProcessRecord", "CertificateStores", "RejectedCertificates") },
                AutoAcceptUntrustedCertificates = autoAccept
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = _configuration.GetValue("OpcUa:OperationTimeoutMs", 5000) },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = _configuration.GetValue("OpcUa:SessionTimeoutMs", 60000) }
        };

        if (autoAccept)
            config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = e.Error.StatusCode == UaStatusCodes.BadCertificateUntrusted;
        return config;
    }

    private string GetSetting(string key, string fallback) => _configuration[$"OpcUa:{key}"] ?? fallback;
    private string GetRingSetting(string key, string fallback) => _configuration[$"OpcUa:RingRolling:{key}"] ?? fallback;

    private static readonly string[] AllowedFurnaces = { "RRP_1", "RRP_2", "RRP_3", "RRP_4", "RRP_5", "RRP_10", "RRP_11", "RRP_12", "RRP_13", "RRP_14", "RRP_15", "RRP_16", "RRP_17", "RRP_18", "RRP_19" };

    public async ValueTask DisposeAsync()
    {
        if (_stopping is null)
            return;
        _stopping.Cancel();
        _notifications.Writer.TryComplete();
        if (_writerTask is not null)
        {
            try { await _writerTask; } catch (OperationCanceledException) { }
        }
        foreach (var subscription in _subscriptions.Values)
        {
            try
            {
                await subscription.Subscription.DeleteAsync(true, CancellationToken.None);
                await subscription.Session.CloseAsync(CancellationToken.None);
                subscription.Session.Dispose();
            }
            catch { }
        }
        _stopping.Dispose();
    }

    private sealed record EndpointDefinition(string Name, string Url, EndpointKind Kind, List<SignalDefinition> Signals);
    private sealed record SignalDefinition(string Name, string NodeId);
    private sealed record EndpointSubscription(Session Session, Subscription Subscription);
    private sealed record SignalNotification(EndpointKind Kind, string Endpoint, string Signal, object? Value, DateTime SourceTimestamp, DateTime ReceivedAt);
    private enum EndpointKind { Furnace, Machine }

    private sealed class Snapshot
    {
        public bool DoorOpen;
        public DateTime? DoorOpenTimestamp;
        public bool DoorClosed;
        public DateTime? DoorClosedTimestamp;
        public bool AutoStartLed;
        public bool AutoStopLed;
        public bool AutoStartPushButton;
        public bool AutoStopPushButton;
        public decimal MaterialForce;
        public decimal MaterialTemperature;
        public DateTime? AutoStartLedTimestamp;
        public DateTime? AutoStopLedTimestamp;
        public DateTime? AutoStartPushButtonTimestamp;
        public DateTime? AutoStopPushButtonTimestamp;

        public void Apply(SignalNotification notification)
        {
            switch (notification.Signal)
            {
                case "DoorOpen": DoorOpen = ToBoolean(notification.Value); DoorOpenTimestamp = notification.SourceTimestamp; break;
                case "DoorClosed": DoorClosed = ToBoolean(notification.Value); DoorClosedTimestamp = notification.SourceTimestamp; break;
                case "AutoStartLed": AutoStartLed = ToBoolean(notification.Value); AutoStartLedTimestamp = notification.SourceTimestamp; break;
                case "AutoStopLed": AutoStopLed = ToBoolean(notification.Value); AutoStopLedTimestamp = notification.SourceTimestamp; break;
                case "AutoStartPushButton": AutoStartPushButton = ToBoolean(notification.Value); AutoStartPushButtonTimestamp = notification.SourceTimestamp; break;
                case "AutoStopPushButton": AutoStopPushButton = ToBoolean(notification.Value); AutoStopPushButtonTimestamp = notification.SourceTimestamp; break;
                case "MaterialForce": MaterialForce = ToDecimal(notification.Value); break;
                case "MaterialTemperature": MaterialTemperature = ToDecimal(notification.Value); break;
            }
        }

        private static bool ToBoolean(object? value) => value switch
        {
            bool booleanValue => booleanValue,
            null => false,
            _ => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture) != 0
        };

        private static decimal ToDecimal(object? value) => value is null ? 0 : Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
