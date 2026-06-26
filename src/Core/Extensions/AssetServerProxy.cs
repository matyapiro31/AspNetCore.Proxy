using AspNetCore.Proxy.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace AspNetCore.Proxy
{
    /// <summary>
    /// Configuration for a content-type-based backend selector.
    /// Maps file extension groups to upstream server base URLs.
    /// </summary>
    public sealed class AssetRouteRule
    {
        /// <summary>File extensions handled by this rule (e.g. <c>".png"</c>, <c>".jpg"</c>).</summary>
        public IReadOnlyList<string> Extensions { get; }

        /// <summary>Upstream server base URLs for this rule. Round-robin is used when multiple are given.</summary>
        public string[] Endpoints { get; }

        /// <param name="endpoints">Upstream server base URLs.</param>
        /// <param name="extensions">File extensions to match (leading dot required).</param>
        public AssetRouteRule(string[] endpoints, params string[] extensions)
        {
            if (endpoints == null || endpoints.Length == 0)
                throw new ArgumentException("At least one endpoint must be provided.", nameof(endpoints));
            if (extensions == null || extensions.Length == 0)
                throw new ArgumentException("At least one extension must be provided.", nameof(extensions));

            Endpoints = endpoints;
            Extensions = Array.AsReadOnly(extensions);
        }
    }

    /// <summary>
    /// Options for the asset/data server proxy.
    /// </summary>
    public sealed class AssetProxyOptions
    {
        /// <summary>
        /// When non-null, this value is written as the <c>Cache-Control</c> header on every proxied response.
        /// Example: <c>"public, max-age=86400"</c>.
        /// </summary>
        public string CacheControlOverride { get; set; }

        /// <summary>
        /// When non-null, this value is written as <c>Access-Control-Allow-Origin</c> on every response.
        /// Use <c>"*"</c> for public CDN assets.
        /// </summary>
        public string CorsOrigin { get; set; }

        /// <summary>
        /// When <c>true</c> (default), conditional request headers (<c>If-None-Match</c>,
        /// <c>If-Modified-Since</c>) are forwarded to the upstream so it can return 304.
        /// </summary>
        public bool ForwardConditionalHeaders { get; set; } = true;

        /// <summary>
        /// When <c>true</c> (default), range request headers (<c>Range</c>, <c>If-Range</c>)
        /// are forwarded to the upstream to support partial content (206) for large files.
        /// </summary>
        public bool ForwardRangeHeaders { get; set; } = true;

        /// <summary>
        /// A fallback upstream base URL used when no <see cref="AssetRouteRule"/> matches the
        /// requested file extension. Required when rules do not cover all extensions.
        /// </summary>
        public string FallbackEndpoint { get; set; }

        /// <summary>
        /// An optional delegate invoked after the upstream response is received.
        /// Use this to add custom response headers or logging.
        /// </summary>
        public Func<HttpContext, HttpResponseMessage, Task> AfterReceive { get; set; }
    }

    /// <summary>
    /// Extension methods for proxying requests to data and asset file servers,
    /// with support for caching, range requests, CORS, and content-type-based routing.
    /// </summary>
    public static class AssetServerProxyExtensions
    {
        /// <summary>
        /// Adds a proxy that routes requests under <paramref name="routePrefix"/> to backend
        /// asset/data servers, with per-extension server selection and enhanced caching headers.
        /// </summary>
        /// <param name="app">The ASP.NET <see cref="IApplicationBuilder"/>.</param>
        /// <param name="routePrefix">
        /// The route prefix to handle, e.g. <c>"assets/{**path}"</c>.
        /// The <c>{**path}</c> catch-all is appended to the upstream base URL.
        /// </param>
        /// <param name="rules">
        /// Per-extension routing rules. The first rule whose extensions contain the requested
        /// file's extension wins.
        /// </param>
        /// <param name="options">Asset proxy options (caching, CORS, range support).</param>
        /// <returns>The current <see cref="IApplicationBuilder"/> instance.</returns>
        public static IApplicationBuilder UseAssetProxy(
            this IApplicationBuilder app,
            string routePrefix,
            IEnumerable<AssetRouteRule> rules,
            AssetProxyOptions options = null)
        {
            if (string.IsNullOrEmpty(routePrefix))
                throw new ArgumentException("routePrefix must not be null or empty.", nameof(routePrefix));

            var ruleList = (rules ?? Enumerable.Empty<AssetRouteRule>()).ToList();
            var roundRobinCounters = new Dictionary<AssetRouteRule, int>();
            foreach (var rule in ruleList)
                roundRobinCounters[rule] = 0;

            options ??= new AssetProxyOptions();

            return app.UseProxies(proxies =>
                proxies.Map(routePrefix, proxy =>
                    proxy.UseHttp(
                        (context, args) =>
                        {
                            var path = args != null && args.TryGetValue("path", out var p) ? p?.ToString() : context.Request.Path.Value;
                            var ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();

                            string baseUrl = null;

                            foreach (var rule in ruleList)
                            {
                                if (rule.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                                {
                                    var idx = roundRobinCounters[rule];
                                    baseUrl = rule.Endpoints[idx % rule.Endpoints.Length];
                                    roundRobinCounters[rule] = idx + 1;
                                    break;
                                }
                            }

                            baseUrl ??= options.FallbackEndpoint
                                ?? throw new InvalidOperationException(
                                    $"No AssetRouteRule matched extension '{ext}' and no FallbackEndpoint is configured.");

                            return new ValueTask<string>($"{baseUrl.TrimEnd('/')}/{path?.TrimStart('/')}");
                        },
                        httpOptions => httpOptions
                            .WithShouldAddForwardedHeaders(true)
                            .WithBeforeSend((ctx, req) =>
                            {
                                if (!options.ForwardConditionalHeaders)
                                {
                                    req.Headers.Remove(HeaderNames.IfNoneMatch);
                                    req.Headers.Remove(HeaderNames.IfModifiedSince);
                                }

                                if (!options.ForwardRangeHeaders)
                                {
                                    req.Headers.Remove(HeaderNames.Range);
                                    req.Headers.Remove(HeaderNames.IfRange);
                                }

                                return Task.CompletedTask;
                            })
                            .WithAfterReceive(async (ctx, res) =>
                            {
                                if (options.CacheControlOverride != null)
                                    res.Headers.Remove(HeaderNames.CacheControl);

                                if (options.AfterReceive != null)
                                    await options.AfterReceive(ctx, res).ConfigureAwait(false);
                            })
                            .WithHandleFailure(async (ctx, ex) =>
                            {
                                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                                await ctx.Response.WriteAsync(
                                    $"Asset proxy error: {ex.Message}").ConfigureAwait(false);
                            })
                    )
                )
            );
        }
    }
}
