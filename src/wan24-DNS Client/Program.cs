using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using wan24.Core;
using wan24.DNS.Config;
using wan24.DNS.Services;

await Bootstrap.Async(typeof(Program).Assembly).DynamicContext();
CliArguments param = new(args);
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
await AppSettings.ApplyAsync(builder.Environment.IsDevelopment()).DynamicContext();
builder.Logging.SetMinimumLevel(
        builder.Environment.IsDevelopment()
            ? LogLevel.Trace
            : LogLevel.Information
    )
    .AddProvider(new LoggerProvider(Logging.Logger!));
builder.Services.AddHostedService<DnsService>();
if (param["test"]) builder.Services.AddHostedService<TestService>();
using IHost host = builder.Build();
Logging.WriteInfo("Starting http DNS proxy client");
await host.RunAsync().DynamicContext();
Logging.WriteInfo("Stopped http DNS proxy client");
