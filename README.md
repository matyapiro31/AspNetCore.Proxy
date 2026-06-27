# AspNetCore.Proxy

[![Actions Status](https://github.com/twitchax/AspNetCore.Proxy/workflows/build/badge.svg)](https://github.com/twitchax/AspNetCore.Proxy/actions)
[![codecov](https://codecov.io/gh/twitchax/AspNetCore.Proxy/branch/master/graph/badge.svg)](https://codecov.io/gh/twitchax/AspNetCore.Proxy)
[![GitHub Release](https://img.shields.io/github/release/twitchax/aspnetcore.proxy.svg)](https://github.com/twitchax/aspnetcore.proxy/releases)
[![NuGet Version](https://img.shields.io/nuget/v/aspnetcore.proxy.svg)](https://www.nuget.org/packages/aspnetcore.proxy/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/aspnetcore.proxy.svg)](https://www.nuget.org/packages/aspnetcore.proxy/)

ASP.NET Core Proxies made easy.

## Information

### Install

```bash
dotnet add package AspNetCore.Proxy
```

### Test

Download the source and run.

```bash
dotnet restore
dotnet test src/Test/AspNetCore.Proxy.Tests.csproj
```

### Compatibility

.NET Standard 2.0, .NET 6.0, .NET 7.0, and .NET 8.0.

### Examples

First, you must add the required services.

```csharp
public void ConfigureServices(IServiceCollection services)
{
    ...
    services.AddProxies();
    ...
}
```

#### Run a Proxy

You can run a proxy over all endpoints by using `RunProxy` in your `Configure` method.

```csharp
app.RunProxy(proxy => proxy.UseHttp("http://google.com"));
```

In addition, you can route this proxy depending on the context.  You can return a `string` or `ValueTask<string>` from the computer.

```csharp
app.RunProxy(proxy => proxy
    .UseHttp((context, args) =>
    {
        if(context.Request.Path.StartsWithSegments("/should/forward/to/favorite"))
            return "http://myfavoriteserver.com";

        return "http://myhttpserver.com";
    })
    .UseWs((context, args) => "ws://mywsserver.com"));
```

#### Routes At Startup

You can define mapped proxy routes in your `Configure` method at startup.

```csharp
app.UseRouting();
app.UseEndpoints(endpoints => endpoints.MapControllers());

app.UseProxies(proxies =>
{
    // Bare string.
    proxies.Map("echo/post", proxy => proxy.UseHttp("https://postman-echo.com/post"));

    // Computed to task.
    proxies.Map("api/comments/task/{postId}", proxy => proxy.UseHttp((_, args) => new ValueTask<string>($"https://jsonplaceholder.typicode.com/comments/{args["postId"]}")));

    // Computed to string.
    proxies.Map("api/comments/string/{postId}", proxy => proxy.UseHttp((_, args) => $"https://jsonplaceholder.typicode.com/comments/{args["postId"]}"));
});
```

#### Route At Startup with Custom HttpClientHandler

ASP.NET Core allows you to configure the behavior of its HTTP client objects by registering a named HttpClient with its own HttpClientHandler, which can then be referred to by name elsewhere.  This can be used to support features such as server certificate custom validation.  The UseProxies setup supports using such a named client:

```csharp
proxies.Map( "/api/v1/...", proxy => proxy.UseHttp( 
    (context, args) => ...,
    builder => builder.WithHttpClientName("myClientName")));
```
...where "myClientName" was previously registered as:
```csharp
services
    .AddHttpClient("myClientName")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler() {
        ServerCertificateCustomValidationCallback = MyValidateCertificateMethod,
        UseDefaultCredentials = true
    });
```

#### Route All Unhandled Requests

You can also route all unhandled requests to another server, e.g.

```csharp
app.UseStatusCodePages(async statusCodeContext =>
{
    var context = statusCodeContext.HttpContext;
    if (context.Response.StatusCode == StatusCodes.Status404NotFound)
    {
        var request = context.Features.Get<IHttpRequestFeature>();
        if (request != null)
        {
            await context.HttpProxyAsync($"https://example.com{request.RawTarget}");
        }
    }
});
```

#### Existing Controller

You can define a proxy over a specific endpoint on an existing `Controller` by leveraging the `ProxyAsync` extension methods.

```csharp
public class MyController : Controller
{
    [Route("api/posts/{postId}")]
    public Task GetPosts(int postId)
    {
        return this.HttpProxyAsync($"https://jsonplaceholder.typicode.com/posts/{postId}");
    }
}
```

> NOTE: The body of the request should not be consumed by the controller (i.e., the controller should not have any `[FromBody]` parameters); 
> otherwise, the proxy operation will fail.  This is due to the fact that the body is read from a `Stream`, and that `Stream` is progressed 
> when the body is read.

You can "catch all" using ASP.NET `**rest` semantics.

```csharp
[Route("api/google/{**rest}")]
public Task ProxyCatchAll(string rest)
{
    // If you don't need the query string, then you can remove this.
    var queryString = this.Request.QueryString.Value;
    return this.HttpProxyAsync($"https://google.com/{rest}{queryString}");
}
```

In addition, you can proxy to WebSocket endpoints.

```csharp
public class MyController : Controller
{
    [Route("ws")]
    public Task OpenWs()
    {
        return this.WsProxyAsync($"wss://mywsendpoint.com/ws");
    }
}
```

#### Uber Example

You can also pass special options that apply when the proxy operation occurs.

```csharp
public class MyController : Controller
{
    private HttpProxyOptions _httpOptions = HttpProxyOptionsBuilder.Instance
        .WithShouldAddForwardedHeaders(false)
        .WithHttpClientName("MyCustomClient")
        .WithIntercept(async context =>
        {
            if(context.Connection.RemotePort == 7777)
            {
                context.Response.StatusCode = 300;
                await context.Response.WriteAsync("I don't like this port, so I am not proxying this request!");
                return true;
            }

            return false;
        })
        .WithBeforeSend((c, hrm) =>
        {
            // Set something that is needed for the downstream endpoint.
            hrm.Headers.Authorization = new AuthenticationHeaderValue("Basic");

            return Task.CompletedTask;
        })
        .WithAfterReceive((c, hrm) =>
        {
            // Alter the content in  some way before sending back to client.
            var newContent = new StringContent("It's all greek...er, Latin...to me!");
            hrm.Content = newContent;

            return Task.CompletedTask;
        })
        .WithHandleFailure(async (c, e) =>
        {
            // Return a custom error response.
            c.Response.StatusCode = 403;
            await c.Response.WriteAsync("Things borked.");
        }).Build();

    private WsProxyOptions _wsOptions = WsProxyOptionsBuilder.Instance
        .WithBufferSize(8192)
        .WithIntercept(context => new ValueTask<bool>(context.WebSockets.WebSocketRequestedProtocols.Contains("interceptedProtocol")))
        .WithDataIntercept((data, direction, type) =>
        {
            if(direction == WsProxyDataDirection.Downstream && System.Text.Encoding.Default.GetString(data.Array).StartsWith("BAD")) 
                data.Array[0] = (byte)'M';

            return Task.CompletedTask;
        })
        .WithBeforeConnect((context, wso) =>
        {
            wso.AddSubProtocol("myRandomProto");
            return Task.CompletedTask;
        })
        .WithHandleFailure(async (context, e) =>
        {
            context.Response.StatusCode = 599;
            await context.Response.WriteAsync("Failure handled.");
        }).Build();
    
    [Route("api/posts/{postId}")]
    public Task GetPosts(int postId)
    {
        return this.ProxyAsync("http://myhttpendpoint.com", "ws://mywsendpoint.com", _httpOptions, _wsOptions);
    }
}
```

---

## Advanced: API Distribution (Per-Route Load Balancing)

Route each API path prefix to its own cluster of backend servers.

```csharp
services.AddProxies();

// Simple: dictionary of route → server list (round-robin)
app.UseApiDistribution(new Dictionary<string, string[]>
{
    { "api/users/{**rest}",  new[] { "http://users-1:5000",  "http://users-2:5000"  } },
    { "api/orders/{**rest}", new[] { "http://orders-1:5000", "http://orders-2:5000" } },
});

// Detailed: per-route strategy and options
app.UseApiDistribution(new[]
{
    new ApiRouteConfig(
        route:     "api/search/{**rest}",
        endpoints: new[] { "http://search-1:5000", "http://search-2:5000", "http://search-3:5000" },
        strategy:  DistributionStrategy.Random),

    new ApiRouteConfig(
        route:     "api/heavy/{**rest}",
        endpoints: new[] { "http://heavy-1:5000", "http://heavy-2:5000" },
        strategy:  DistributionStrategy.RoundRobin),
});
```

### Weighted Round Robin

When some servers have more capacity, assign higher weights.

```csharp
// 3/4 of traffic → big-server, 1/4 → small-server
proxies.Map("api/data/{**rest}", proxy => proxy.UseHttp(
    WeightedRoundRobin.Of(
        ("http://big-server:5000",   3),
        ("http://small-server:5000", 1)
    )
));
```

---

## Advanced: Asset / Data File Server Proxy

Proxy static assets and data files with per-extension backend routing,
cache-control header override, CORS injection, and Range passthrough.

```csharp
app.UseAssetProxy(
    routePrefix: "assets/{**path}",
    rules: new[]
    {
        // Images → CDN cluster
        new AssetRouteRule(
            new[] { "http://cdn-1:8080", "http://cdn-2:8080" },
            ".png", ".jpg", ".gif", ".svg", ".webp"),

        // JSON / XML data → data servers
        new AssetRouteRule(
            new[] { "http://data-1:5000", "http://data-2:5000" },
            ".json", ".xml"),

        // Fonts → dedicated font server
        new AssetRouteRule(
            new[] { "http://font-server:8080" },
            ".woff", ".woff2", ".ttf"),
    },
    options: new AssetProxyOptions
    {
        CacheControlOverride      = "public, max-age=86400",
        CorsOrigin                = "*",
        ForwardRangeHeaders       = true,   // large file partial content
        ForwardConditionalHeaders = true,   // ETag / 304 support
        FallbackEndpoint          = "http://default-server:5000",
    }
);
```

---

## Advanced: Resumable Download Proxy

Transparently supports pause/resume downloads even when the upstream server
**does not** implement HTTP Range requests.

- If upstream returns `206 Partial Content` → forwarded as-is.
- If upstream returns `200 OK` → proxy buffers the full body, slices the
  requested byte range, and returns `206 Partial Content` to the client.
- Subsequent range requests for the same URL are served from an optional
  in-process cache, avoiding repeated upstream downloads.

```csharp
app.UseResumableProxy(
    routePrefix:      "downloads/{**path}",
    upstreamBaseUrls: new[] { "http://file-server-1:8080", "http://file-server-2:8080" },
    options: new ResumableProxyOptions
    {
        MaxCacheSizeBytes        = 500 * 1024 * 1024,  // total cache: 500 MiB
        MaxCachableFileSizeBytes =  50 * 1024 * 1024,  // per-file limit: 50 MiB
        CacheTtl                 = TimeSpan.FromMinutes(30),
        CorsOrigin               = "*",
    }
);
```

**Behavior summary:**

| Upstream response | Proxy action |
|---|---|
| `206 Partial Content` | Forward as-is |
| `200 OK` (no Range support) | Buffer → slice → return `206` |
| Cache hit (same URL) | Serve from cache, no upstream call |
| Range out of bounds | `416 Range Not Satisfiable` |
| Upstream error | `502 Bad Gateway` |

---

## License

```
The MIT License (MIT)

Copyright (c) 2017 Aaron Roney

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
