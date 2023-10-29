using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using wan24.Core;
using wan24.DNS.Config;
using wan24.DNS.Middleware;
using wan24.DNS.Services;

await Bootstrap.Async(typeof(Program).Assembly).DynamicContext();
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
await AppSettings.ApplyAsync(builder.Environment.IsDevelopment()).DynamicContext();
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Logging.SetMinimumLevel(
        builder.Environment.IsDevelopment()
            ? LogLevel.Trace
            : AppSettings.Current.LogLevel
    )
    .AddProvider(new LoggerProvider(Logging.Logger!));
DnsService dnsService = new();
builder.Services.AddHostedService(serviceProvider => dnsService)
    .AddSingleton(dnsService);
using WebApplication app = builder.Build();
app.UseExceptionHandler(app => app.Run(context =>
    {
        //TODO .NET 8: Use an IExceptionHandler
        Logging.WriteError(
            context.Features.Get<IExceptionHandlerFeature>() is IExceptionHandlerFeature ehf
                ? $"Peer \"{context.Connection.RemoteIpAddress}\" endpoint \"{ehf.Endpoint}\" request path \"{ehf.Path}\" handling caused an exception: {ehf.Error}"
                : $"App pipeline catched an exception during peer \"{context.Connection.RemoteIpAddress}\" endpoint \"{context.GetEndpoint()}\" request path \"{context.Request.Path}\" handling, but no {nameof(IExceptionHandlerFeature)} is available :("
            );
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
        return Task.CompletedTask;
    }))
    .UseForwardedHeaders(new()
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    })
    .UseWebSockets(new()
    {
        KeepAliveInterval = TimeSpan.FromMinutes(2)
    })
    .UseMiddleware<WebSocketMiddleware>();
Logging.WriteInfo("Starting http DNS proxy server");
await app.RunAsync().DynamicContext();
Logging.WriteInfo("Stopped http DNS proxy server");
