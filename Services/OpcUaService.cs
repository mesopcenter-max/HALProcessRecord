using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using HALProcessRecord.ViewModels;
using UaStatusCodes = Opc.Ua.StatusCodes;

namespace HALProcessRecord.Services;

public sealed class OpcUaService : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpcUaService> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private Session? _session;
    private string? _connectedEndpointUrl;

    public OpcUaService(IConfiguration configuration, ILogger<OpcUaService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<OpcUaStatusViewModel> ReadStatusAsync(string? furnace = null, CancellationToken cancellationToken = default)
    {
        var endpointUrl = GetEndpointUrl(furnace);
        var status = new OpcUaStatusViewModel
        {
            EndpointUrl = endpointUrl
        };

        try
        {
            await EnsureConnectedAsync(endpointUrl, cancellationToken);

            if (_session is null || !_session.Connected)
            {
                status.ConnectionStatus = "Disconnected";
                status.ErrorMessage = "OPC UA session is not connected.";
                return status;
            }

            var close = ReadNode(GetSetting("DoorCloseNodeId", "ns=4;i=5"));
            var open = ReadNode(GetSetting("DoorOpenNodeId", "ns=4;i=6"));

            status.DoorClosed = ToBoolean(close.Value);
            status.DoorOpen = ToBoolean(open.Value);
            status.DoorCloseNodeId = GetSetting("DoorCloseNodeId", "ns=4;i=5");
            status.DoorOpenNodeId = GetSetting("DoorOpenNodeId", "ns=4;i=6");
            status.DoorClosedSourceTimestamp = close.SourceTimestamp;
            status.DoorOpenSourceTimestamp = open.SourceTimestamp;

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
            status.ReadAt = DateTime.Now;
            status.Status = GetDoorStatus(status.DoorOpen, status.DoorClosed);

            if (close.StatusCode != UaStatusCodes.Good || open.StatusCode != UaStatusCodes.Good)
            {
                status.ErrorMessage = $"Door signal status: Close={close.StatusCode}, Open={open.StatusCode}.";
            }

            return status;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to read values from OPC UA server {Endpoint}.", status.EndpointUrl);
            await DisconnectAsync();

            status.ConnectionStatus = "Disconnected";
            status.Status = "Unavailable";
            status.ErrorMessage = ex.Message;
            return status;
        }
    }

    private async Task EnsureConnectedAsync(string endpointUrl, CancellationToken cancellationToken)
    {
        if (_session is not null && _session.Connected &&
            string.Equals(_connectedEndpointUrl, endpointUrl, StringComparison.OrdinalIgnoreCase))
            return;

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null && _session.Connected &&
                string.Equals(_connectedEndpointUrl, endpointUrl, StringComparison.OrdinalIgnoreCase))
                return;

            await DisconnectAsync();
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
            _connectedEndpointUrl = endpointUrl;
        }
        finally
        {
            _connectionLock.Release();
        }
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
            return false;

        return Convert.ToBoolean(value);
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

    private async Task DisconnectAsync()
    {
        if (_session is null)
            return;

        try
        {
            _session.KeepAlive -= Session_KeepAlive;
            await _session.CloseAsync();
            _session.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while closing OPC UA session.");
        }
        finally
        {
            _session = null;
            _connectedEndpointUrl = null;
        }

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        try
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _connectionLock.Dispose();
        }
    }
}

