using System;
using System.Collections.Generic;
using Xunit;

namespace AspNetCore.Proxy.Tests
{
    public class AssetRouteRuleTests
    {
        [Fact]
        public void ThrowsOnEmptyEndpoints()
        {
            Assert.Throws<ArgumentException>(() =>
                new AssetRouteRule(Array.Empty<string>(), ".png"));
        }

        [Fact]
        public void ThrowsOnEmptyExtensions()
        {
            Assert.Throws<ArgumentException>(() =>
                new AssetRouteRule(new[] { "http://cdn" }));
        }

        [Fact]
        public void StoresEndpointsAndExtensions()
        {
            var rule = new AssetRouteRule(
                new[] { "http://cdn-1", "http://cdn-2" },
                ".png", ".jpg", ".gif");

            Assert.Equal(2, rule.Endpoints.Length);
            Assert.Contains(".png", rule.Extensions);
            Assert.Contains(".jpg", rule.Extensions);
        }
    }

    public class AssetProxyOptionsTests
    {
        [Fact]
        public void DefaultsAreSane()
        {
            var opts = new AssetProxyOptions();

            Assert.True(opts.ForwardConditionalHeaders);
            Assert.True(opts.ForwardRangeHeaders);
            Assert.Null(opts.CacheControlOverride);
            Assert.Null(opts.CorsOrigin);
            Assert.Null(opts.FallbackEndpoint);
        }

        [Fact]
        public void CanOverrideCacheControl()
        {
            var opts = new AssetProxyOptions { CacheControlOverride = "public, max-age=3600" };
            Assert.Equal("public, max-age=3600", opts.CacheControlOverride);
        }
    }
}
