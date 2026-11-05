using System.Collections.Concurrent;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using HALProcessRecord.ViewModels;
using HALProcessRecord.Utilities;
using UaStatusCodes = Opc.Ua.StatusCodes;

namespace HALProcessRecord.Services;

public sealed class OpcUaService : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpcUaService> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private Session? _session;
    private string? _connectedEndpointUrl;

    public OpcUaService(IConfiguration configuration, ILogger<OpcUaService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<OpcUaStatusViewModel> ReadStatusAsync(string? furnace = null, CancellationToken cancellationToken = default)
    {
        await _readLock.WaitAsync(cancellationToken);
        try
        {
            return await ReadFurnaceStatusAsync(furnace, cancellationToken);
        }
        finally
        {
            _readLock.Release();
        }
    }

    public async Task<OpcMachineStatusViewModel> ReadRingRollingStatusAsync(string? machine = null, CancellationToken cancellationToken = default)
    {
        await _readLock.WaitAsync(cancellationToken);
        try
        {
            var endpointUrl = GetMachineEndpointUrl(machine);
            var status = new OpcMachineStatusViewModel { EndpointUrl = endpointUrl };

            try
            {
                if (IsRetrySuppressed(endpointUrl))
                {
                    status.ErrorMessage = "Endpoint retry is temporarily delayed after a connection failure.";
                    return status;
                }

                await EnsureConnectedAsync(endpointUrl, cancellationToken);

                if (_session is null || !_session.Connected)
                {
                    status.ErrorMessage = "OPC UA session is not connected.";
                    return status;
                }

                var autoStartLed = ReadNode(GetRingSetting("AutoStartLEDNodeId", "ns=4;i=3"));
                var autoStopLed = ReadNode(GetRingSetting("AutoStopLEDNodeId", "ns=4;i=5"));
                var autoStartPb = ReadNode(GetRingSetting("AutoStartPBNodeId", "ns=4;i=2"));
                var autoStopPb = ReadNode(GetRingSetting("AutoStopPBNodeId", "ns=4;i=4"));
                var materialForce = ReadNode(GetRingSetting("MaterialForceNodeId", "ns=4;i=30"));
                var materialTemperature = ReadNode(GetRingSetting("MaterialTemperatureNodeId", "ns=4;i=9"));

                status.AutoStartLED = ToBoolean(autoStartLed.Value);
                status.AutoStopLED = ToBoolean(autoStopLed.Value);
                status.AutoStartPB = ToBoolean(autoStartPb.Value);
                status.AutoStopPB = ToBoolean(autoStopPb.Value);
                status.MaterialForce = ToSingle(materialForce.Value);
                status.MaterialTemperature = ToSingle(materialTemperature.Value);
                var readAt = IndiaTime.Now;
                status.MaterialForceSourceTimestamp = IndiaTime.NormalizeOpcSourceTimestamp(materialForce.SourceTimestamp, readAt);
                status.AutoStartLEDSourceTimestamp = IndiaTime.NormalizeOpcSourceTimestamp(autoStartLed.SourceTimestamp, readAt);
                status.AutoStopLEDSourceTimestamp = IndiaTime.NormalizeOpcSourceTimestamp(autoStopLed.SourceTimestamp, readAt);
                status.AutoStartPBSourceTimestamp = IndiaTime.NormalizeOpcSourceTimestamp(autoStartPb.SourceTimestamp, readAt);
                status.AutoStopPBSourceTimestamp = IndiaTime.NormalizeOpcSourceTimestamp(autoStopPb.SourceTimestamp, readAt);
                status.ConnectionStatus = "Connected";
                status.ReadAt = readAt;
                return status;
            }
            catch (Exception ex)
            {
                SuppressRetries(endpointUrl);
                _logger.LogWarning(
                    "Unable to read ring rolling values from OPC UA server {Endpoint}: {Message}",
                    endpointUrl,
                    ex.Message);
                await DisconnectAsync(endpointUrl);
                status.ConnectionStatus = "Disconnected";
                status.ErrorMessage = ex.Message;
                return status;
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    private async Task<OpcUaStatusViewModel> ReadFurnaceStatusAsync(string? furnace, CancellationToken cancellationToken)
    {
        var endpointUrl = GetEndpointUrl(furnace);
        var status = new OpcUaStatusViewModel
        {
            EndpointUrl = endpointUrl
        };

        try
        {
            if (IsRetrySuppressed(endpointUrl))
            {
                status.ErrorMessage = "Endpoint retry is temporarily delayed after a connection failure.";
                return status;
            }

            await EnsureConnectedAsync(endpointUrl, cancellationToken);

            if (_session is null || !_session.Connected)
            {
                status.ConnectionStatus = "Disconnected";
                status.ErrorMessage = "OPC UA session is not connected.";
                return status;
            }

            // Allow a per-furnace override for the door close node id (RRP_12 uses ns=4;i=2)
            var doorCloseNodeId = string.Equals(furnace, "RRP_12", StringComparison.OrdinalIgnoreCase)
                ? "ns=4;i=2"
                : GetSetting("DoorCloseNodeId", "ns=4;i=5");

            var close = ReadNode(doorCloseNodeId);
            var open = ReadNode(GetSetting("DoorOpenNodeId", "ns=4;i=6"));

            EnsureGoodSignal(close, "door close");
            EnsureGoodSignal(open, "door open");

            status.DoorClosed = ToBoolean(close.Value);
            status.DoorOpen = ToBoolean(open.Value);
            status.DoorCloseNodeId = GetSetting("DoorCloseNodeId", "ns=4;i=5");
            status.DoorOpenNodeId = GetSetting("DoorOpenNodeId", "ns=4;i=6");
            var readAt = IndiaTime.Now;
            status.DoorClosedSourceTimestamp = IndiaTime.NormalizeOpcSourceTimestamp(close.SourceTimestamp, readAt);
            status.DoorOpenSourceTimestamp = IndiaTime.NormalizeOpcSourceTimestamp(open.SourceTimestamp, readAt);

            var codeNodeId = GetSetting("CodeNodeId", string.Empty);
            if (string.IsNullOrWhiteSpace(codeNodeId))
            {
                status.Code = "Not configured";
                status.CodeNodeId = "";
            }
            else
            {
                var code = ReadNode(codeNodeId);
                status.Code = code.Value?.ToString() ?? "";
                status.CodeNodeId = codeNodeId;
            }

            status.ConnectionStatus = "Connected";
            status.ReadAt = readAt;
            status.Status = GetDoorStatus(status.DoorOpen, status.DoorClosed);

            _logger.LogDebug(
                "Read furnace door signals from {Endpoint}: close={CloseValue} ({CloseType}), open={OpenValue} ({OpenType}), status={Status}",
                endpointUrl,
                close.Value,
                close.Value?.GetType().Name ?? "null",
                open.Value,
                open.Value?.GetType().Name ?? "null",
                status.Status);

            return status;
        }
        catch (Exception ex)
        {
            SuppressRetries(endpointUrl);
            _logger.LogWarning(
                "Unable to read values from OPC UA server {Endpoint}: {Message}",
                status.EndpointUrl,
                ex.Message);
            await DisconnectAsync(endpointUrl);

            status.ConnectionStatus = "Disconnected";
            status.Status = "Unavailable";
            status.ErrorMessage = ex.Message;
            return status;
        }
    }

    private async Task EnsureConnectedAsync(string endpointUrl, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(endpointUrl, out var cachedSession) && cachedSession.Connected)
        {
            _session = cachedSession;
            _connectedEndpointUrl = endpointUrl;
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(endpointUrl, out cachedSession) && cachedSession.Connected)
            {
                _session = cachedSession;
                _connectedEndpointUrl = endpointUrl;
                return;
            }

            if (cachedSession is not null)
                await DisconnectAsync(endpointUrl);

            _session = null;
            _connectedEndpointUrl = null;
            var operationTimeout = GetIntSetting("OperationTimeoutMs", 5000);
            var sessionTimeout = GetIntSetting("SessionTimeoutMs", 60000);
            var autoAccept = GetBoolSetting("AutoAcceptUntrustedCertificates", true);

            var config = new ApplicationConfiguration
            {
                ApplicationName = "HAL Process Record",
                ApplicationUri = $"urn:{Utils.GetHostName()}:HALProcessRecord",
                ProductUri = "urn:HAL:HALProcessRecord",
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HALProcessRecord", "CertificateStores", "UA Applications"),
                        SubjectName = "CN=HAL Process Record"
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HALProcessRecord", "CertificateStores", "TrustedPeers")
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HALProcessRecord", "CertificateStores", "TrustedIssuers")
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HALProcessRecord", "CertificateStores", "RejectedCertificates")
                    },
                    AutoAcceptUntrustedCertificates = autoAccept
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas
                {
                    OperationTimeout = operationTimeout
                },
                ClientConfiguration = new ClientConfiguration
                {
                    DefaultSessionTimeout = sessionTimeout
                }
            };

            await config.ValidateAsync(ApplicationType.Client);

            if (autoAccept)
            {
                config.CertificateValidator.CertificateValidation += (_, e) =>
                {
                    e.Accept = e.Error.StatusCode == UaStatusCodes.BadCertificateUntrusted;
                };
            }

            var application = new ApplicationInstance(config, null);

            await application.CheckApplicationInstanceCertificatesAsync(false, 2048);

            var selectedEndpoint = await CoreClientUtils.SelectEndpointAsync(
                config,
                endpointUrl,
                false,
                operationTimeout,
                null!,
                CancellationToken.None);

            if (selectedEndpoint is null)
                throw new InvalidOperationException($"No OPC UA endpoint was found at {endpointUrl}.");

            var endpointConfiguration = EndpointConfiguration.Create(config);
            endpointConfiguration.OperationTimeout = operationTimeout;

            var configuredEndpoint = new ConfiguredEndpoint(
                null,
                selectedEndpoint,
                endpointConfiguration);

#pragma warning disable CS0618
            _session = await Session.CreateAsync(
                null,
                config,
                null,
                configuredEndpoint,
                updateBeforeConnect: false,
                checkDomain: false,
                sessionName: "HAL Process Record OPC UA Session",
                sessionTimeout: (uint)sessionTimeout,
                identity: null,
                preferredLocales: null,
                DiagnosticsMasks.None,
                CancellationToken.None);
#pragma warning restore CS0618

            _session.KeepAlive += Session_KeepAlive;
            _sessions[endpointUrl] = _session;
            _connectedEndpointUrl = endpointUrl;
            _retryAfter.TryRemove(endpointUrl, out _);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private bool IsRetrySuppressed(string endpointUrl)
    {
        return _retryAfter.TryGetValue(endpointUrl, out var retryAfter) && retryAfter > DateTime.UtcNow;
    }

    private void SuppressRetries(string endpointUrl)
    {
        _retryAfter[endpointUrl] = DateTime.UtcNow.AddSeconds(1);
    }

    private DataValue ReadNode(string nodeIdText)
    {
        if (_session is null || !_session.Connected)
            throw new InvalidOperationException("OPC UA session is not connected.");

        var nodeId = NodeId.Parse(nodeIdText);
        return _session.ReadValueAsync(nodeId).GetAwaiter().GetResult();
    }

    private static bool ToBoolean(object? value)
    {
        if (value is bool booleanValue)
            return booleanValue;

        if (value is null)
            throw new InvalidOperationException("OPC UA returned a null door signal value.");

        if (value is string text)
        {
            if (bool.TryParse(text, out var parsedBoolean))
                return parsedBoolean;

            if (decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsedNumber))
                return parsedNumber != 0;

            throw new InvalidOperationException($"OPC UA returned an unrecognized door signal value '{text}'.");
        }

        if (value is IConvertible convertible)
        {
            try
            {
                return convertible.ToDecimal(System.Globalization.CultureInfo.InvariantCulture) != 0;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw new InvalidOperationException($"OPC UA returned an unrecognized door signal value '{value}'.", ex);
            }
        }

        throw new InvalidOperationException($"OPC UA returned an unsupported door signal type '{value.GetType().Name}'.");
    }

    private static void EnsureGoodSignal(DataValue signal, string signalName)
    {
        if (signal.StatusCode != UaStatusCodes.Good)
            throw new InvalidOperationException($"OPC UA {signalName} signal has bad status {signal.StatusCode}.");
    }

    private static float ToSingle(object? value)
    {
        return value is null ? 0 : Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string GetDoorStatus(bool doorOpen, bool doorClosed)
    {
        if (doorOpen && !doorClosed)
            return "OPEN";

        if (doorClosed && !doorOpen)
            return "CLOSED";

        if (!doorOpen && !doorClosed)
            return "UNKNOWN";

        return "CONFLICT";
    }

    private string GetEndpointUrl(string? furnace)
    {
        if (!string.IsNullOrWhiteSpace(furnace))
        {
            var configured = _configuration[$"OpcUa:FurnaceEndpoints:{furnace}"];
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
        }

        return GetSetting("EndpointUrl", "opc.tcp://172.133.63.150:4840");
    }

    private string GetMachineEndpointUrl(string? machine)
    {
        if (!string.IsNullOrWhiteSpace(machine))
        {
            var configured = _configuration[$"OpcUa:RingRollingEndpoints:{machine}"];
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
        }

        return "opc.tcp://172.133.78.30:4840";
    }

    private string GetRingSetting(string key, string defaultValue)
    {
        return _configuration[$"OpcUa:RingRolling:{key}"] ?? defaultValue;
    }

    private string GetSetting(string key, string defaultValue)
    {
        return _configuration[$"OpcUa:{key}"] ?? defaultValue;
    }

    private int GetIntSetting(string key, int defaultValue)
    {
        return int.TryParse(_configuration[$"OpcUa:{key}"], out var value)
            ? value
            : defaultValue;
    }

    private bool GetBoolSetting(string key, bool defaultValue)
    {
        return bool.TryParse(_configuration[$"OpcUa:{key}"], out var value)
            ? value
            : defaultValue;
    }

    private void Session_KeepAlive(Opc.Ua.Client.ISession session, KeepAliveEventArgs e)
    {
        if (ServiceResult.IsBad(e.Status))
        {
            _logger.LogWarning("OPC UA keep-alive status is {Status}.", e.Status);
        }
    }

    private async Task DisconnectAsync(string endpointUrl)
    {
        if (!_sessions.Remove(endpointUrl, out var session))
            return;

        if (ReferenceEquals(_session, session))
        {
            _session = null;
            _connectedEndpointUrl = null;
        }

        try
        {
            session.KeepAlive -= Session_KeepAlive;
            await session.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while closing OPC UA session.");
        }
        finally
        {
            session.Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            foreach (var endpointUrl in _sessions.Keys.ToArray())
                DisconnectAsync(endpointUrl).GetAwaiter().GetResult();
        }
        finally
        {
            _connectionLock.Dispose();
            _readLock.Dispose();
        }
    }
}

