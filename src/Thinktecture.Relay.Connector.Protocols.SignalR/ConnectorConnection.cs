using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Thinktecture.Relay.Acknowledgement;
using Thinktecture.Relay.Connector.Targets;
using Thinktecture.Relay.Transport;

namespace Thinktecture.Relay.Connector.Protocols.SignalR;

/// <inheritdoc cref="IConnectorConnection"/>
public class ConnectorConnection<TRequest, TResponse, TAcknowledge> : IConnectorConnection, IDisposable
	where TRequest : IClientRequest
	where TResponse : ITargetResponse
	where TAcknowledge : IAcknowledgeRequest
{
	private readonly IClientRequestHandler<TRequest> _clientRequestHandler;
	private readonly ILogger<ConnectorConnection<TRequest, TResponse, TAcknowledge>> _logger;

	// TODO move to LoggerMessage source generator when destructuring is supported
	// (see https://github.com/dotnet/runtime/issues/69490)
	private readonly Action<ILogger, Guid, string, IClientRequest, Exception?> _logHandlingRequestDetailed =
		LoggerMessage.Define<Guid, string, IClientRequest>(LogLevel.Trace, 11200,
			"Handling request {RelayRequestId} on connection {TransportConnectionId} {@Request}");

	// TODO move to LoggerMessage source generator when destructuring is supported
	// (see https://github.com/dotnet/runtime/issues/69490)
	private readonly Action<ILogger, Guid, string, Guid, Exception?> _logHandlingRequestSimple =
		LoggerMessage.Define<Guid, string, Guid>(LogLevel.Debug, 11201,
			"Handling request {RelayRequestId} on connection {TransportConnectionId} from origin {OriginId}");

	private readonly DiscoveryDocumentRetryPolicy _retryPolicy;

	private CancellationTokenSource? _cancellationTokenSource = new CancellationTokenSource();
	private string _connectionId = string.Empty;
	private bool? _enableTracing;
	private HubConnection? _hubConnection;

	/// <summary>
	/// Initializes a new instance of the <see cref="ConnectorConnection{TRequest,TResponse,TAcknowledge}"/> class.
	/// </summary>
	/// <param name="logger">An <see cref="ILogger{TCategoryName}"/>.</param>
	/// <param name="retryPolicy">The <see cref="DiscoveryDocumentRetryPolicy"/>.</param>
	/// <param name="clientRequestHandler">An <see cref="IClientRequestHandler{T}"/>.</param>
	/// <param name="hubConnection">The <see cref="HubConnection"/>.</param>
	public ConnectorConnection(ILogger<ConnectorConnection<TRequest, TResponse, TAcknowledge>> logger,
		DiscoveryDocumentRetryPolicy retryPolicy, IClientRequestHandler<TRequest> clientRequestHandler,
		HubConnection hubConnection)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
		_clientRequestHandler = clientRequestHandler ?? throw new ArgumentNullException(nameof(clientRequestHandler));

		_hubConnection = hubConnection ?? throw new ArgumentNullException(nameof(hubConnection));
		_hubConnection.On<TRequest>("RequestTarget", RequestTargetAsync);
		_hubConnection.On<ITenantConfig>("Configure", ConfigureAsync);

		_hubConnection.Closed += HubConnectionClosed;
		_hubConnection.Reconnecting += HubConnectionReconnecting;
		_hubConnection.Reconnected += HubConnectionReconnected;
	}

	/// <inheritdoc />
	public event AsyncEventHandler<string>? Connected;

	/// <inheritdoc />
	public event AsyncEventHandler<string>? Reconnecting;

	/// <inheritdoc />
	public event AsyncEventHandler<string>? Reconnected;

	/// <inheritdoc />
	public event AsyncEventHandler<string>? Disconnected;

	/// <inheritdoc />
	public async Task ConnectAsync(CancellationToken cancellationToken)
	{
		await ConnectAsyncInternal(cancellationToken);
		await Connected.InvokeAsync(this, _connectionId);
	}

	/// <inheritdoc />
	public async Task DisconnectAsync(CancellationToken cancellationToken)
	{
		if (_hubConnection is null) return;

		_logger.LogTrace("Disconnecting connection {TransportConnectionId}", _connectionId);

		if (_cancellationTokenSource is not null)
		{
			await _cancellationTokenSource.CancelAsync();
		}
		await _hubConnection.StopAsync(cancellationToken);
		_logger.LogInformation("Disconnected on connection {TransportConnectionId}", _connectionId);

		await Disconnected.InvokeAsync(this, _connectionId);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_hubConnection is null && _cancellationTokenSource is null) return;

		lock (this)
		{
			var connection = _hubConnection;
			_hubConnection = null;

			if (connection is not null)
			{
				connection.Closed -= HubConnectionClosed;
				connection.Reconnecting -= HubConnectionReconnecting;
				connection.Reconnected -= HubConnectionReconnected;
				connection.DisposeAsync().GetAwaiter().GetResult();
			}

			var cancellationTokenSource = _cancellationTokenSource;
			_cancellationTokenSource = null;

			cancellationTokenSource?.Cancel();
			cancellationTokenSource?.Dispose();
		}
	}

	private async Task HubConnectionClosed(Exception? ex)
	{
		if (ex is null or OperationCanceledException)
		{
			_logger.LogDebug(11202, "Connection {TransportConnectionId} gracefully closed", _connectionId);
		}
		else
		{
			_logger.LogWarning(11203, ex, "Connection {TransportConnectionId} closed", _connectionId);

			var token = _cancellationTokenSource?.Token;
			if (token is null) return;

			await Reconnecting.InvokeAsync(this, _connectionId);
			await ConnectAsyncInternal(token.Value);
			await Reconnected.InvokeAsync(this, _connectionId);
		}
	}

	private async Task HubConnectionReconnecting(Exception? ex)
	{
		if (ex is null)
		{
			_logger.LogInformation(11205,
				"Trying to reconnect after connection {TransportConnectionId} was lost", _connectionId);
		}
		else
		{
			_logger.LogWarning(11204, ex,
				"Trying to reconnect after connection {TransportConnectionId} was lost due to an error",
				_connectionId);
		}
		await Reconnecting.InvokeAsync(this, _connectionId);
	}

	private async Task HubConnectionReconnected(string? connectionId)
	{
		if (connectionId is null)
		{
			_logger.LogWarning(11206, "Reconnected without a connection id");
		}
		else if (_connectionId == connectionId)
		{
			_logger.LogDebug(11207, "Reconnected on connection {TransportConnectionId}", _connectionId);
		}
		else
		{
			_logger.LogInformation(11208,
				"Dropped connection {TransportConnectionId} in favor of new connection {NewTransportConnectionId}",
				_connectionId, connectionId);
			_connectionId = connectionId;
		}

		await Reconnected.InvokeAsync(this, _connectionId);
	}

	private async Task RequestTargetAsync(TRequest request)
	{
		if (_logger.IsEnabled(LogLevel.Trace))
			_logHandlingRequestDetailed(_logger, request.RequestId, _connectionId, request, null);
		if (_logger.IsEnabled(LogLevel.Debug))
			_logHandlingRequestSimple(_logger, request.RequestId, _connectionId, request.RequestOriginId, null);

		request.EnableTracing = request.EnableTracing || _enableTracing.GetValueOrDefault();

		var token = _cancellationTokenSource?.Token;
		if (token is null) return;

		await _clientRequestHandler.HandleAsync(request, token.Value);
	}

	private Task ConfigureAsync(ITenantConfig config)
	{
		_logger.LogTrace(11209, "Received tenant config {@Config} on connection {TransportConnectionId}",
			config, _connectionId);

		_hubConnection?.SetKeepAliveInterval(config.KeepAliveInterval);
		_retryPolicy.SetReconnectDelays(config.ReconnectMinimumDelay, config.ReconnectMaximumDelay);

		if (config.EnableTracing is not null)
		{
			_enableTracing = config.EnableTracing;
		}

		return Task.CompletedTask;
	}

	private async Task ConnectAsyncInternal(CancellationToken cancellationToken)
	{
		if (_hubConnection is null) return;

		try
		{
			await _hubConnection.StartAsync(cancellationToken);
			_connectionId = _hubConnection.ConnectionId!;

			_logger.LogInformation(11210, "Connected on connection {TransportConnectionId}", _connectionId);
		}
		catch (OperationCanceledException)
		{
			// ignore this, as this will be thrown when the service shuts down gracefully
		}
		catch (Exception ex)
		{
			// due to the retry policy this should never be caught
			_logger.LogError(11211, ex, "An error occured while trying to connect");
			await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
			await ConnectAsyncInternal(cancellationToken);
		}
	}
}
