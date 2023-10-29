using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Hosting;
using System.Net;
using wan24.Core;
using wan24.DNS.Config;

namespace wan24.DNS.Services
{
    /// <summary>
    /// DNS test service
    /// </summary>
    public sealed class TestService : HostedServiceBase
    {
        /// <summary>
        /// Lifetime
        /// </summary>
        private readonly IHostApplicationLifetime Lifetime;
        /// <summary>
        /// Is the app stopping?
        /// </summary>
        private bool AppStopping = false;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="appLifetime">Lifetime</param>
        public TestService(IHostApplicationLifetime appLifetime) : base()
        {
            Lifetime = appLifetime;
            appLifetime.ApplicationStopping.Register(() => AppStopping = true);
            Name = "Test service";
        }

        /// <inheritdoc/>
        protected override async Task WorkerAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).DynamicContext();
            Core.Logging.WriteInfo($"Trying to resolve a hostname");
            LookupClient client = new((from ep in AppSettings.Current.EndPoints select IPEndPoint.Parse(ep)).ToArray());
            IDnsQueryResponse response = await client.QueryAsync("wan24.de", QueryType.A);
            foreach (ARecord record in response.Answers.ARecords())
                Core.Logging.WriteInfo($"Resolved to IP address {record.Address}");
        }

        /// <inheritdoc/>
        protected override Task AfterStopAsync(CancellationToken cancellationToken)
        {
            if (!AppStopping) Lifetime.StopApplication();
            return base.AfterStopAsync(cancellationToken);
        }
    }
}
