using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Thinktecture.Relay.Server.Interceptor;
using Thinktecture.Relay.Server.Persistence;
using Thinktecture.Relay.Server.Transport;
using Thinktecture.Relay.Transport;

namespace Thinktecture.Relay.Server.Middleware
{
	/// <inheritdoc />
	public class RelayMiddleware<TRequest, TResponse> : IMiddleware
		where TRequest : IClientRequest
		where TResponse : class, ITargetResponse, new()
	{
		private readonly ILogger<RelayMiddleware<TRequest, TResponse>> _logger;
		private readonly IRelayClientRequestFactory<TRequest> _requestFactory;
		private readonly ITenantRepository _tenantRepository;
		private readonly IBodyStore _bodyStore;
		private readonly IRequestCoordinator<TRequest> _requestCoordinator;
		private readonly IRelayTargetResponseWriter<TResponse> _responseWriter;
		private readonly IResponseCoordinator<TResponse> _responseCoordinator;
		private readonly IRelayContext<TRequest, TResponse> _relayContext;
		private readonly IEnumerable<IClientRequestInterceptor<TRequest, TResponse>> _clientRequestInterceptors;
		private readonly IEnumerable<ITargetResponseInterceptor<TRequest, TResponse>> _targetResponseInterceptors;
		private readonly TimeSpan? _requestExpiration;
		private readonly int _maximumBodySize;

		/// <summary>
		/// Initializes a new instance of the <see cref="RelayMiddleware{TRequest,TResponse}"/> class.
		/// </summary>
		/// <param name="logger">An <see cref="ILogger{TCategoryName}"/>.</param>
		/// <param name="requestFactory">An <see cref="IRelayClientRequestFactory{TRequest}"/>.</param>
		/// <param name="tenantRepository">An <see cref="ITenantRepository"/>.</param>
		/// <param name="bodyStore">An <see cref="IBodyStore"/>.</param>
		/// <param name="requestCoordinator">An <see cref="IRequestCoordinator{TRequest}"/>.</param>
		/// <param name="responseWriter">An <see cref="IRelayTargetResponseWriter{TResponse}"/>.</param>
		/// <param name="responseCoordinator">The <see cref="IResponseCoordinator{TResponse}"/>.</param>
		/// <param name="relayContext">An <see cref="IRelayContext{TRequest,TResponse}"/>.</param>
		/// <param name="tenantDispatcher">An <see cref="ITenantDispatcher{TRequest}"/>.</param>
		/// <param name="connectorTransport">An <see cref="IConnectorTransport{TResponse}"/>.</param>
		/// <param name="relayServerOptions">An <see cref="IOptions{TOptions}"/>.</param>
		/// <param name="clientRequestInterceptors">An enumeration of <see cref="IClientRequestInterceptor{TRequest,TResponse}"/>.</param>
		/// <param name="targetResponseInterceptors">An enumeration of <see cref="ITargetResponseInterceptor{TRequest,TResponse}"/>.</param>
		public RelayMiddleware(ILogger<RelayMiddleware<TRequest, TResponse>> logger, IRelayClientRequestFactory<TRequest> requestFactory,
			ITenantRepository tenantRepository, IBodyStore bodyStore, IRequestCoordinator<TRequest> requestCoordinator,
			IRelayTargetResponseWriter<TResponse> responseWriter, IResponseCoordinator<TResponse> responseCoordinator,
			IRelayContext<TRequest, TResponse> relayContext, ITenantDispatcher<TRequest> tenantDispatcher,
			IConnectorTransport<TResponse> connectorTransport, IOptions<RelayServerOptions> relayServerOptions,
			IEnumerable<IClientRequestInterceptor<TRequest, TResponse>> clientRequestInterceptors,
			IEnumerable<ITargetResponseInterceptor<TRequest, TResponse>> targetResponseInterceptors)
		{
			if (relayServerOptions == null) throw new ArgumentNullException(nameof(relayServerOptions));
			if (tenantDispatcher == null) throw new ArgumentNullException(nameof(tenantDispatcher));
			if (connectorTransport == null) throw new ArgumentNullException(nameof(connectorTransport));

			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
			_tenantRepository = tenantRepository ?? throw new ArgumentNullException(nameof(tenantRepository));
			_bodyStore = bodyStore ?? throw new ArgumentNullException(nameof(bodyStore));
			_requestCoordinator = requestCoordinator ?? throw new ArgumentNullException(nameof(requestCoordinator));
			_responseWriter = responseWriter ?? throw new ArgumentNullException(nameof(responseWriter));
			_responseCoordinator = responseCoordinator ?? throw new ArgumentNullException(nameof(responseCoordinator));
			_relayContext = relayContext ?? throw new ArgumentNullException(nameof(relayContext));
			_clientRequestInterceptors = clientRequestInterceptors ?? Array.Empty<IClientRequestInterceptor<TRequest, TResponse>>();
			_targetResponseInterceptors = targetResponseInterceptors ?? Array.Empty<ITargetResponseInterceptor<TRequest, TResponse>>();

			_requestExpiration = relayServerOptions.Value.RequestExpiration;
			_maximumBodySize = Math.Min(tenantDispatcher.BinarySizeThreshold.GetValueOrDefault(),
				connectorTransport.BinarySizeThreshold.GetValueOrDefault());
		}

		/// <inheritdoc />
		public async Task InvokeAsync(HttpContext context, RequestDelegate next)
		{
			var tenantName = context.Request.Path.Value.Split('/').Skip(1).FirstOrDefault();
			if (string.IsNullOrEmpty(tenantName))
			{
				_logger.LogWarning("Invalid request received {Path}{Query}", context.Request.Path, context.Request.QueryString);
				await next.Invoke(context);
				return;
			}

			var tenant = await _tenantRepository.LoadTenantByNameAsync(tenantName);
			if (tenant == null)
			{
				_logger.LogWarning("Unknown tenant in request received {Path}{Query}", context.Request.Path, context.Request.QueryString);
				await next.Invoke(context);
				return;
			}

			using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
			if (_requestExpiration != null)
			{
				cts.CancelAfter(_requestExpiration.Value);
			}

			_relayContext.ResponseDisposables.Add(_responseCoordinator.RegisterRequest(_relayContext.RequestId));

			try
			{
				context.Request.EnableBuffering();
				await context.Request.Body.DrainAsync(cts.Token);

				_relayContext.ClientRequest = await _requestFactory.CreateAsync(tenant.Id, _relayContext.RequestId, context.Request, cts.Token);
				_logger.LogTrace("Parsed request {@Request}", _relayContext.ClientRequest);

				await InterceptClientRequestAsync(cts.Token);

				if (_relayContext.TargetResponse == null || _relayContext.ForceConnectorDelivery)
				{
					await DeliverToConnectorAsync(cts.Token);

					if (_relayContext.TargetResponse == null)
					{
						await WaitForConnectorResponseAsync(cts.Token);
					}
				}

				_logger.LogTrace("Received response for request {RequestId}", _relayContext.RequestId);

				await InterceptTargetResponseAsync(cts.Token);

				await _responseWriter.WriteAsync(_relayContext.TargetResponse, context.Response, cts.Token);
			}
			catch (TransportException)
			{
				await WriteErrorResponse(HttpStatusCode.ServiceUnavailable, context.Response, cts.Token);
			}
			catch (OperationCanceledException)
			{
				if (context.RequestAborted.IsCancellationRequested)
				{
					_logger.LogDebug("Client aborted request {RequestId}", _relayContext.RequestId);
				}
				else
				{
					_logger.LogWarning("Request {RequestId} expired", _relayContext.RequestId);
					await WriteErrorResponse(HttpStatusCode.RequestTimeout, context.Response, cts.Token);
				}
			}
		}

		private async Task InterceptClientRequestAsync(CancellationToken cancellationToken)
		{
			_logger.LogDebug("Executing client request interceptors for request {RequestId}", _relayContext.RequestId);

			var bodyContent = _relayContext.HttpContext.Request.Body;

			foreach (var interceptor in _clientRequestInterceptors)
			{
				_logger.LogTrace("Executing interceptor {Interceptor} for request {RequestId}", interceptor.GetType().FullName,
					_relayContext.RequestId);
				await interceptor.OnRequestReceivedAsync(_relayContext, cancellationToken);

				if (_relayContext.ClientRequest.BodyContent != null && bodyContent != _relayContext.ClientRequest.BodyContent)
				{
					// an interceptor changed the body content - need to dispose it properly
					_relayContext.ResponseDisposables.Add(_relayContext.ClientRequest.BodyContent);
					bodyContent = _relayContext.ClientRequest.BodyContent;
				}
			}
		}

		private async Task DeliverToConnectorAsync(CancellationToken cancellationToken)
		{
			if (_relayContext.ClientRequest.BodyContent != null &&
				await TryInlineBodyContentAsync(_relayContext.ClientRequest, cancellationToken))
			{
				_relayContext.ResponseDisposables.Add(_relayContext.ClientRequest.BodyContent);
			}

			await _requestCoordinator.DeliverRequestAsync(_relayContext.ClientRequest, cancellationToken);
		}

		private async Task WaitForConnectorResponseAsync(CancellationToken cancellationToken)
		{
			var (response, disposable) = await _responseCoordinator.GetResponseAsync(_relayContext.RequestId, cancellationToken);
			_relayContext.TargetResponse = response;

			if (disposable != null)
			{
				_relayContext.ResponseDisposables.Add(disposable);
			}
		}

		private async Task InterceptTargetResponseAsync(CancellationToken cancellationToken)
		{
			_logger.LogDebug("Executing target response interceptors for request {RequestId}", _relayContext.RequestId);

			foreach (var interceptor in _targetResponseInterceptors)
			{
				_logger.LogTrace("Executing interceptor {Interceptor} for request {RequestId}", interceptor.GetType().FullName,
					_relayContext.RequestId);
				await interceptor.OnResponseReceivedAsync(_relayContext, cancellationToken);
			}
		}

		private async Task<bool> TryInlineBodyContentAsync(TRequest request, CancellationToken cancellationToken)
		{
			if (request.BodySize > _maximumBodySize)
			{
				_logger.LogInformation(
					"Outsourcing from request {BodySize} bytes because of a maximum of {BinarySizeThreshold} for request {RequestId}",
					request.BodySize, _maximumBodySize, request.RequestId);
				request.BodySize = await _bodyStore.StoreRequestBodyAsync(request.RequestId, request.BodyContent, cancellationToken);
				request.BodyContent = null;
				_logger.LogDebug("Outsourced from request {BodySize} bytes for request {RequestId}", request.BodySize, request.RequestId);
				return false;
			}

			request.BodyContent = await request.BodyContent.CopyToMemoryStreamAsync(cancellationToken);
			_logger.LogDebug("Inlined from request {BodySize} bytes for request {RequestId}", request.BodySize, request.RequestId);
			return true;
		}

		private Task WriteErrorResponse(HttpStatusCode httpStatusCode, HttpResponse response, CancellationToken cancellationToken)
			=> _responseWriter.WriteAsync(_relayContext.ClientRequest.CreateResponse<TResponse>(httpStatusCode), response, cancellationToken);
	}
}
