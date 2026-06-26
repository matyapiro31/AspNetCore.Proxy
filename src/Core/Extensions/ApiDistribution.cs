using AspNetCore.Proxy.Builders;
using AspNetCore.Proxy.Endpoints;
using AspNetCore.Proxy.Options;
using Microsoft.AspNetCore.Builder;
using System;
using System.Collections.Generic;

namespace AspNetCore.Proxy
{
    /// <summary>
    /// Describes the distribution strategy used when routing requests to a cluster of backend servers.
    /// </summary>
    public enum DistributionStrategy
    {
        /// <summary>Cycle through servers in order (default).</summary>
        RoundRobin,
        /// <summary>Pick a server at random on each request.</summary>
        Random,
    }

    /// <summary>
    /// Defines a set of backend servers for a single API route segment.
    /// </summary>
    public sealed class ApiRouteConfig
    {
        /// <summary>Route pattern (e.g. <c>"api/users/{**rest}"</c>).</summary>
        public string Route { get; }

        /// <summary>Upstream server base URLs.</summary>
        public string[] Endpoints { get; }

        /// <summary>Load-balancing strategy applied across <see cref="Endpoints"/>.</summary>
        public DistributionStrategy Strategy { get; }

        /// <summary>Optional HTTP proxy options for this route.</summary>
        public Action<IHttpProxyOptionsBuilder> HttpOptions { get; }

        /// <param name="route">Route pattern.</param>
        /// <param name="endpoints">Upstream server base URLs.</param>
        /// <param name="strategy">Load-balancing strategy.</param>
        /// <param name="httpOptions">Optional HTTP proxy options.</param>
        public ApiRouteConfig(
            string route,
            string[] endpoints,
            DistributionStrategy strategy = DistributionStrategy.RoundRobin,
            Action<IHttpProxyOptionsBuilder> httpOptions = null)
        {
            if (string.IsNullOrEmpty(route))
                throw new ArgumentException("Route must not be null or empty.", nameof(route));
            if (endpoints == null || endpoints.Length == 0)
                throw new ArgumentException("At least one endpoint must be provided.", nameof(endpoints));

            Route = route;
            Endpoints = endpoints;
            Strategy = strategy;
            HttpOptions = httpOptions;
        }
    }

    /// <summary>
    /// Extension methods for per-API server distribution.
    /// </summary>
    public static class ApiDistributionExtensions
    {
        /// <summary>
        /// Adds proxy middleware that distributes each API route to its own cluster of backend servers.
        /// </summary>
        /// <param name="app">The ASP.NET <see cref="IApplicationBuilder"/>.</param>
        /// <param name="routes">
        /// A mapping of route patterns to upstream server arrays.
        /// Each route is handled independently with round-robin load balancing.
        /// <example>
        /// <code>
        /// app.UseApiDistribution(new Dictionary&lt;string, string[]&gt; {
        ///     { "api/users/{**rest}",  new[] { "http://users-1:5000",  "http://users-2:5000"  } },
        ///     { "api/orders/{**rest}", new[] { "http://orders-1:5000", "http://orders-2:5000" } },
        /// });
        /// </code>
        /// </example>
        /// </param>
        /// <returns>The current <see cref="IApplicationBuilder"/> instance.</returns>
        public static IApplicationBuilder UseApiDistribution(
            this IApplicationBuilder app,
            IDictionary<string, string[]> routes)
        {
            if (routes == null) throw new ArgumentNullException(nameof(routes));

            var configs = new List<ApiRouteConfig>();
            foreach (var kv in routes)
                configs.Add(new ApiRouteConfig(kv.Key, kv.Value));

            return app.UseApiDistribution(configs);
        }

        /// <summary>
        /// Adds proxy middleware that distributes each API route to its own cluster of backend servers,
        /// using detailed <see cref="ApiRouteConfig"/> entries for per-route strategy and options.
        /// </summary>
        /// <param name="app">The ASP.NET <see cref="IApplicationBuilder"/>.</param>
        /// <param name="routeConfigs">Per-route distribution configurations.</param>
        /// <returns>The current <see cref="IApplicationBuilder"/> instance.</returns>
        public static IApplicationBuilder UseApiDistribution(
            this IApplicationBuilder app,
            IEnumerable<ApiRouteConfig> routeConfigs)
        {
            if (routeConfigs == null) throw new ArgumentNullException(nameof(routeConfigs));

            return app.UseProxies(proxies =>
            {
                foreach (var config in routeConfigs)
                {
                    var endpointComputer = BuildEndpointComputer(config);
                    proxies.Map(config.Route, proxy =>
                        proxy.UseHttp(endpointComputer, config.HttpOptions));
                }
            });
        }

        private static EndpointComputerToString BuildEndpointComputer(ApiRouteConfig config)
        {
            EndpointComputerToString baseComputer = config.Strategy switch
            {
                DistributionStrategy.Random => Endpoints.RandomRobin.Of(config.Endpoints),
                _ => Endpoints.RoundRobin.Of(config.Endpoints),
            };

            // Append the remaining path and query string so upstreams receive the full request path.
            return (context, args) =>
            {
                var base_ = baseComputer(context, args).TrimEnd('/');
                return $"{base_}{context.Request.Path}{context.Request.QueryString}";
            };
        }
    }
}
