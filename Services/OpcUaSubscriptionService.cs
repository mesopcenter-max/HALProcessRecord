using System.Collections.Concurrent;
using System.Threading.Channels;
using HALProcessRecord.Data;
using HALProcessRecord.Models;
using HALProcessRecord.Utilities;
using HALProcessRecord.ViewModels;
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
    private readonly TimingMonitorService _timingMonitorService;
    private readonly ILogger<OpcUaSubscriptionService> _logger;
    private readonly ConcurrentDictionary<string, EndpointSubscription> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _snapshotLock = new();
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<SignalNotification> _notifications = Channel.CreateUnbounded<SignalNotification>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private CancellationTokenSource? _stopping;
    private Task? _writerTask;
    private Task? _snapshotTask;

    public OpcUaSubscriptionService(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        TimingMonitorService timingMonitorService,
        ILogger<OpcUaSubscriptionService> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _timingMonitorService = timingMonitorService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_stopping is not null)
            return;

        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _writerTask = PersistNotificationsAsync(_stopping.Token);
        _snapshotTask = PersistPeriodicSnapshotsAsync(_stopping.Token);

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
        if (ServiceResult.IsBad(value.StatusCode))
        {
            _logger.LogWarning(
                "Ignoring OPC UA notification with bad status {Status} for {Endpoint}/{Signal}.",
                value.StatusCode,
                endpoint.Name,
                signal.Name);
            return;
        }
        if (value.StatusCode != UaStatusCodes.Good)
            _logger.LogWarning("OPC UA notification status {Status} for {Endpoint}/{Signal}; processing the value and checking server diagnostics for possible queue overflow.", value.StatusCode, endpoint.Name, signal.Name);

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
        await foreach (var notification in _notifications.Reader.ReadAllAsync(cancellationToken))
        {
            Snapshot snapshot;
            lock (_snapshotLock)
            {
                if (!_snapshots.TryGetValue(notification.Endpoint, out var current))
                    current = new Snapshot(notification.Kind);
                current.Apply(notification);
                _snapshots[notification.Endpoint] = current;
                snapshot = current.Copy();
            }

            if (!snapshot.IsReadyForTiming(notification.Kind))
                continue;

            if (notification.Kind == EndpointKind.Furnace)
            {
                await _timingMonitorService.ProcessFurnaceSignalAsync(
                    notification.Endpoint,
                    snapshot.CreateFurnaceStatus(notification.ReceivedAt),
                    cancellationToken);
            }
            else
            {
                await _timingMonitorService.ProcessMachineSignalAsync(
                    notification.Endpoint,
                    snapshot.CreateMachineStatus(notification.ReceivedAt),
                    notification.Signal != "MaterialTemperature",
                    cancellationToken);
            }
        }
    }

    private async Task PersistPeriodicSnapshotsAsync(CancellationToken cancellationToken)
    {
        var intervalMilliseconds = Math.Max(1, _configuration.GetValue("OpcUa:DataSnapshotIntervalMs", 3));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMilliseconds));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            Dictionary<string, Snapshot> readySnapshots;
            lock (_snapshotLock)
            {
                readySnapshots = _snapshots
                    .Where(x => x.Value.IsCompleteForPersistence(x.Value.Kind))
                    .ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.OrdinalIgnoreCase);
            }

            if (readySnapshots.Count == 0)
                continue;

            var capturedAt = IndiaTime.Now;
            var furnaceRows = new List<FurnaceDataEntity>();
            var machineRows = new List<MachineDataEntity>();
            foreach (var (endpoint, snapshot) in readySnapshots)
            {
                if (snapshot.Kind == EndpointKind.Furnace)
                {
                    furnaceRows.Add(new FurnaceDataEntity
                    {
                        Furnace = endpoint,
                        DoorClosed = snapshot.DoorClosed,
                        DoorOpen = snapshot.DoorOpen,
                        HalfDoor = false,
                        Code = string.Empty,
                        DoorOpenSourceTimestamp = snapshot.DoorOpenTimestamp,
                        DoorClosedSourceTimestamp = snapshot.DoorClosedTimestamp,
                        SourceReadAt = capturedAt,
                        CapturedAt = capturedAt
                    });
                }
                else
                {
                    machineRows.Add(new MachineDataEntity
                    {
                        Machine = endpoint,
                        AutoStartLed = snapshot.AutoStartLed,
                        AutoStopLed = snapshot.AutoStopLed,
                        AutoStartPushButton = snapshot.AutoStartPushButton,
                        AutoStopPushButton = snapshot.AutoStopPushButton,
                        MaterialForce = snapshot.MaterialForce,
                        MaterialTemperature = snapshot.MaterialTemperature,
                        MaterialForceSourceTimestamp = snapshot.MaterialForceTimestamp,
                        AutoStartLedSourceTimestamp = snapshot.AutoStartLedTimestamp,
                        AutoStopLedSourceTimestamp = snapshot.AutoStopLedTimestamp,
                        AutoStartPushButtonSourceTimestamp = snapshot.AutoStartPushButtonTimestamp,
                        AutoStopPushButtonSourceTimestamp = snapshot.AutoStopPushButtonTimestamp,
                        SourceReadAt = capturedAt,
                        CapturedAt = capturedAt
                    });
                }
            }

            while (true)
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    db.FurnaceData.AddRange(furnaceRows);
                    db.MachineData.AddRange(machineRows);
                    await db.SaveChangesAsync(cancellationToken);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Unable to persist periodic OPC UA snapshots for {FurnaceCount} furnaces and {MachineCount} machines; retrying.",
                        furnaceRows.Count,
                        machineRows.Count);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
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
                new("DoorClosed", string.Equals(item.Key, "RRP_12", StringComparison.OrdinalIgnoreCase)
                    ? "ns=4;i=2"
                    : GetSetting("DoorCloseNodeId", "ns=4;i=5"))
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
        if (_snapshotTask is not null)
        {
            try { await _snapshotTask; } catch (OperationCanceledException) { }
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
        private readonly HashSet<string> _receivedSignals = new(StringComparer.OrdinalIgnoreCase);
        public EndpointKind Kind { get; }
        public DateTime LastReceivedAt { get; private set; }
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
        public DateTime? MaterialForceTimestamp;

        public Snapshot(EndpointKind kind)
        {
            Kind = kind;
        }

        public Snapshot Copy()
        {
            var copy = new Snapshot(Kind)
            {
                LastReceivedAt = LastReceivedAt,
                DoorOpen = DoorOpen,
                DoorOpenTimestamp = DoorOpenTimestamp,
                DoorClosed = DoorClosed,
                DoorClosedTimestamp = DoorClosedTimestamp,
                AutoStartLed = AutoStartLed,
                AutoStopLed = AutoStopLed,
                AutoStartPushButton = AutoStartPushButton,
                AutoStopPushButton = AutoStopPushButton,
                MaterialForce = MaterialForce,
                MaterialTemperature = MaterialTemperature,
                AutoStartLedTimestamp = AutoStartLedTimestamp,
                AutoStopLedTimestamp = AutoStopLedTimestamp,
                AutoStartPushButtonTimestamp = AutoStartPushButtonTimestamp,
                AutoStopPushButtonTimestamp = AutoStopPushButtonTimestamp,
                MaterialForceTimestamp = MaterialForceTimestamp
            };
            copy._receivedSignals.UnionWith(_receivedSignals);
            return copy;
        }

        public void Apply(SignalNotification notification)
        {
            LastReceivedAt = notification.ReceivedAt;
            _receivedSignals.Add(notification.Signal);
            switch (notification.Signal)
            {
                case "DoorOpen": DoorOpen = ToBoolean(notification.Value); DoorOpenTimestamp = notification.SourceTimestamp; break;
                case "DoorClosed": DoorClosed = ToBoolean(notification.Value); DoorClosedTimestamp = notification.SourceTimestamp; break;
                case "AutoStartLed": AutoStartLed = ToBoolean(notification.Value); AutoStartLedTimestamp = notification.SourceTimestamp; break;
                case "AutoStopLed": AutoStopLed = ToBoolean(notification.Value); AutoStopLedTimestamp = notification.SourceTimestamp; break;
                case "AutoStartPushButton": AutoStartPushButton = ToBoolean(notification.Value); AutoStartPushButtonTimestamp = notification.SourceTimestamp; break;
                case "AutoStopPushButton": AutoStopPushButton = ToBoolean(notification.Value); AutoStopPushButtonTimestamp = notification.SourceTimestamp; break;
                case "MaterialForce": MaterialForce = ToDecimal(notification.Value); MaterialForceTimestamp = notification.SourceTimestamp; break;
                case "MaterialTemperature": MaterialTemperature = ToDecimal(notification.Value); break;
            }
        }

        public bool IsReadyForTiming(EndpointKind kind) => kind == EndpointKind.Furnace
            ? _receivedSignals.Contains("DoorOpen") && _receivedSignals.Contains("DoorClosed")
            : _receivedSignals.Contains("AutoStartLed") &&
              _receivedSignals.Contains("AutoStopLed") &&
              _receivedSignals.Contains("AutoStartPushButton") &&
              _receivedSignals.Contains("AutoStopPushButton") &&
              _receivedSignals.Contains("MaterialForce");

        public bool IsCompleteForPersistence(EndpointKind kind) => IsReadyForTiming(kind) &&
            (kind == EndpointKind.Furnace || _receivedSignals.Contains("MaterialTemperature"));

        public OpcUaStatusViewModel CreateFurnaceStatus(DateTime readAt) => new()
        {
            ConnectionStatus = "Connected",
            DoorOpen = DoorOpen,
            DoorClosed = DoorClosed,
            DoorOpenSourceTimestamp = DoorOpenTimestamp,
            DoorClosedSourceTimestamp = DoorClosedTimestamp,
            ReadAt = readAt
        };

        public OpcMachineStatusViewModel CreateMachineStatus(DateTime readAt) => new()
        {
            ConnectionStatus = "Connected",
            AutoStartLED = AutoStartLed,
            AutoStopLED = AutoStopLed,
            AutoStartPB = AutoStartPushButton,
            AutoStopPB = AutoStopPushButton,
            MaterialForce = (float)MaterialForce,
            MaterialTemperature = (float)MaterialTemperature,
            MaterialForceSourceTimestamp = MaterialForceTimestamp,
            AutoStartLEDSourceTimestamp = AutoStartLedTimestamp,
            AutoStopLEDSourceTimestamp = AutoStopLedTimestamp,
            AutoStartPBSourceTimestamp = AutoStartPushButtonTimestamp,
            AutoStopPBSourceTimestamp = AutoStopPushButtonTimestamp,
            ReadAt = readAt
        };

        private static bool ToBoolean(object? value) => value switch
        {
            bool booleanValue => booleanValue,
            null => false,
            _ => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture) != 0
        };

        private static decimal ToDecimal(object? value) => value is null ? 0 : Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
