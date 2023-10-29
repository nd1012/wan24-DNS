using System.Collections.Concurrent;
using System.Net;
using wan24.Core;
using wan24.DNS.Config;

namespace wan24.DNS.Services
{
    /// <summary>
    /// DNS service
    /// </summary>
    public sealed partial class DnsService : HostedServiceBase
    {
        /// <summary>
        /// Connected clients
        /// </summary>
        private readonly ConcurrentDictionary<string, Client> Clients = new();

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="settings">Settings</param>
        public DnsService() : base()
        {
            Resolver = IPEndPoint.Parse(AppSettings.Current.Resolver);
            Name = "DNS service";
        }

        /// <summary>
        /// Resolver
        /// </summary>
        public IPEndPoint Resolver { get; }

        /// <summary>
        /// Add a new client
        /// </summary>
        /// <param name="client">Client</param>
        public async Task AddClientAsync(Client client)
        {
            EnsureUndisposed();
            if (!IsRunning || IsStopping) throw new InvalidOperationException();
            if (Clients.TryRemove(client.Authentication, out Client? existing))
            {
                Logging.WriteWarning($"Detected double connection for authentication \"{client.Authentication}\" from {client.EndPoint} - disconnecting {existing.EndPoint}");
                await existing.DisposeAsync().DynamicContext();
            }
            if (Clients.TryAdd(client.Authentication, client))
            {
                client.OnDisposing += HandleClientDisposing;
                Logging.WriteInfo($"Added client {client.EndPoint}");
                return;
            }
            await client.DisposeAsync().DynamicContext();
            Logging.WriteWarning($"Failed to add client {client.EndPoint}");
        }

        /// <inheritdoc/>
        protected override async Task WorkerAsync()
        {
            await CancelToken.WaitHandle.WaitAsync().DynamicContext();
            Logging.WriteDebug($"DNS service {this} worker was cancelled");
            await Clients.Values.DisposeAllAsync().DynamicContext();
            Logging.WriteDebug($"DNS service {this} worker is quitting");
        }

        /// <summary>
        /// Handle a disposing client
        /// </summary>
        /// <param name="sender">Sender</param>
        /// <param name="e">Arguments</param>
        private async void HandleClientDisposing(IDisposableObject sender, EventArgs e)
        {
            sender.OnDisposing -= HandleClientDisposing;
            if (sender is not Client client) return;
            if (Clients.TryRemove(client.Authentication, out Client? existing) && existing != client)
            {
                Logging.WriteWarning($"Found existing client {existing.EndPoint} during disposing {client.EndPoint} - disposing that one, too");
                await existing.DisposeAsync().DynamicContext();
            }
            Logging.WriteInfo($"Removed disposed client {client.EndPoint}");
        }
    }
}
