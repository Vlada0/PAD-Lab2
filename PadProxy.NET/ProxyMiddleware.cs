﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PadProxy.NET
{
	public class ProxyMiddleware
	{
		private static readonly HttpClient _httpClient = new HttpClient();
		private readonly RequestDelegate _nextMiddleware;
		private readonly IDistributedCache redisCache;
		private readonly LoadBalanceContainer loadBalanceContainer;

		public ProxyMiddleware(
			RequestDelegate nextMiddleware,
			IDistributedCache redisCache,
			LoadBalanceContainer loadBalanceContainer)
		{
			_nextMiddleware = nextMiddleware;
			this.redisCache = redisCache;
			this.loadBalanceContainer = loadBalanceContainer;
		}

		public async Task Invoke(HttpContext context)
		{
			// All the requests are cached by their's URL path
			// When a new requests comes - check whatever it's a repeated one
			if (!(await ProcessCachedResponsePossibility(context)))
			{
				// If no requests found in cache - forward to the server
				var targetUri = BuildTargetUri(context.Request);

				if (targetUri != null)
				{
					var targetRequestMessage = CreateTargetMessage(context, targetUri);

					using (var responseMessage = await _httpClient.SendAsync(targetRequestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted))
					{
						context.Response.StatusCode = (int)responseMessage.StatusCode;

						CopyFromTargetResponseHeaders(context, responseMessage);

						await redisCache.SetAsync(
							context.Request.Path,
							await responseMessage.Content.ReadAsByteArrayAsync(),
							CancellationToken.None);

						await ProcessResponseContent(context, responseMessage);
					}

					return;
				}

				await _nextMiddleware(context);
			}
		}

		// LoadBalancing logic
		// Find the server with the lower active requests number
		private string GetLeastLoadedServer()
		{
			return loadBalanceContainer.GetLeastLoaded();
		}

		private async Task ProcessResponseContent(HttpContext context, HttpResponseMessage responseMessage)
		{
			var content = await responseMessage.Content.ReadAsByteArrayAsync();

			await context.Response.Body.WriteAsync(content);
		}


		private HttpRequestMessage CreateTargetMessage(HttpContext context, Uri targetUri)
		{
			var requestMessage = new HttpRequestMessage();
			CopyFromOriginalRequestContentAndHeaders(context, requestMessage);

			requestMessage.RequestUri = targetUri;
			requestMessage.Headers.Host = targetUri.Host;
			requestMessage.Method = GetMethod(context.Request.Method);

			return requestMessage;
		}

		private void CopyFromOriginalRequestContentAndHeaders(HttpContext context, HttpRequestMessage requestMessage)
		{
			var requestMethod = context.Request.Method;

			if (!HttpMethods.IsGet(requestMethod) &&
				!HttpMethods.IsHead(requestMethod) &&
				!HttpMethods.IsDelete(requestMethod) &&
				!HttpMethods.IsTrace(requestMethod))
			{
				var streamContent = new StreamContent(context.Request.Body);
				requestMessage.Content = streamContent;
			}

			foreach (var header in context.Request.Headers)
			{
				requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
			}
		}

		private void CopyFromTargetResponseHeaders(HttpContext context, HttpResponseMessage responseMessage)
		{
			foreach (var header in responseMessage.Headers)
			{
				context.Response.Headers[header.Key] = header.Value.ToArray();
			}

			foreach (var header in responseMessage.Content.Headers)
			{
				context.Response.Headers[header.Key] = header.Value.ToArray();
			}
			context.Response.Headers.Remove("transfer-encoding");
		}

		private static HttpMethod GetMethod(string method)
		{
			if (HttpMethods.IsDelete(method)) return HttpMethod.Delete;
			if (HttpMethods.IsGet(method)) return HttpMethod.Get;
			if (HttpMethods.IsHead(method)) return HttpMethod.Head;
			if (HttpMethods.IsOptions(method)) return HttpMethod.Options;
			if (HttpMethods.IsPost(method)) return HttpMethod.Post;
			if (HttpMethods.IsPut(method)) return HttpMethod.Put;
			if (HttpMethods.IsTrace(method)) return HttpMethod.Trace;

			return new HttpMethod(method);
		}

		// In case it's a call to the API - get the least loaded server address
		// and create a URL for it's forwarding
		private Uri BuildTargetUri(HttpRequest request)
		{
			Uri targetUri = null;
			PathString remainingPath;

			if (request.Path.StartsWithSegments("/api", out remainingPath))
			{
				targetUri = new Uri(GetLeastLoadedServer() + remainingPath);
			}

			return targetUri;
		}

		// If we find some cached request - return it's response
		private async Task<bool> ProcessCachedResponsePossibility(HttpContext context)
		{
			var cachedRequest = redisCache.GetString(context.Request.Path);

			if (!string.IsNullOrEmpty(cachedRequest))
			{
				context.Response.StatusCode = (int)HttpStatusCode.AlreadyReported;
				await context.Response.WriteAsync(cachedRequest, Encoding.UTF8);

				return true;
			}

			return false;
		}
	}
}
