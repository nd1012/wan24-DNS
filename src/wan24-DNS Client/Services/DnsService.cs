using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using wan24.Core;
using wan24.DNS.Config;

namespace wan24.DNS.Services
{
    /// <summary>
    /// DNS service
    /// </summary>
    public sealed class DnsService : HostedServiceBase
    {
        /// <summary>
        /// Servers
        /// </summary>
        private readonly ConcurrentDictionary<UdpClient, Task> Servers = new();
        /// <summary>
        /// Pending queries
        /// </summary>
        private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]>> PendingQueries = new();
        /// <summary>
        /// Resolver
        /// </summary>
        private ClientWebSocket? Resolver = null;
        /// <summary>
        /// Resolver task
        /// </summary>
        private Task? ResolverTask = null;

        /// <summary>
        /// Constructor
        /// </summary>
        public DnsService() : base() => Name = "DNS service";

        /// <inheritdoc/>
        protected override async Task WorkerAsync()
        {
            try
            {
                // Connect the resolver
                Resolver = new();
                await Resolver.ConnectAsync(AppSettings.Current.Resolver, CancelToken).DynamicContext();
                Logging.WriteTrace($"{this} connected to resolver {AppSettings.Current.Resolver}");
                using (RentedArrayStructSimple<byte> buffer = new(Encoding.UTF8.GetByteCount(AppSettings.Current.ResolverAuthToken), clean: false))
                {
                    Encoding.UTF8.GetBytes(AppSettings.Current.ResolverAuthToken, buffer.Span);
                    await Resolver.SendAsync(buffer.Memory, WebSocketMessageType.Text, endOfMessage: true, CancelToken).DynamicContext();
                    Logging.WriteTrace($"{this} authenticated to resolver {AppSettings.Current.Resolver}");
                }
                ResolverTask = HandleResolvedAsync();
                // Start listening
                IPEndPoint endPoint;
                foreach (string ep in AppSettings.Current.EndPoints)
                {
                    Logging.WriteInfo($"Start listening at endpoint {ep}");
                    endPoint = IPEndPoint.Parse(ep);
                    UdpClient client = new(endPoint)
                    {
                        DontFragment = false,
                        EnableBroadcast = false
                    };
                    client.Client.ReceiveBufferSize = 65507;
                    client.Client.SendBufferSize = 65507;
                    Servers[client] = HandleQueriesAsync(client, endPoint);
                }
                // Wait for stopping
                Logging.WriteTrace($"{this} waiting for cancellation");
                await CancelToken.WaitHandle.WaitAsync().DynamicContext();
                Logging.WriteTrace($"{this} canceled");
            }
            finally
            {
                // Stop listening
                Logging.WriteTrace($"{this} stopping listening");
                foreach (KeyValuePair<UdpClient, Task> kvp in Servers)
                {
                    try
                    {
                        kvp.Key.Close();
                    }
                    catch
                    {
                    }
                    kvp.Key.Dispose();
                    try
                    {
                        await kvp.Value.DynamicContext();
                    }
                    catch
                    {
                    }
                }
                Servers.Clear();
                // Cancel all pending queries
                Logging.WriteTrace($"{this} canceling all pending queries");
                foreach (TaskCompletionSource<byte[]> tcs in PendingQueries.Values)
                    tcs.TrySetCanceled();
                // Disconnect the resolver
                if (Resolver is ClientWebSocket resolver)
                {
                    Logging.WriteTrace($"{this} disconnecting resolver");
                    Resolver = null;
                    // Try a clean disconnection
                    if (resolver.State == WebSocketState.Open)
                        try
                        {
                            await resolver.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken: default).DynamicContext();
                        }
                        catch
                        {
                        }
                    resolver.Dispose();
                    // Wait for the resolver task to finish
                    if (ResolverTask is Task resolverTask)
                    {
                        Logging.WriteTrace($"{this} waiting for resolver task");
                        ResolverTask = null;
                        try
                        {
                            await resolverTask.DynamicContext();
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        /// <inheritdoc/>
        protected override Task AfterStopAsync(CancellationToken cancellationToken)
        {
            Logging.WriteTrace($"{this} stopped");
            if (!PendingQueries.IsEmpty) Logging.WriteWarning($"{PendingQueries.Count} pending queries have not been removed from {this}!");
            if (Resolver is not null)
            {
                Logging.WriteWarning($"Resolver of {this} wasn't cleared!");
                if (ResolverTask is not null) Logging.WriteWarning($"Resolver task status is {ResolverTask.Status} ({this})");
            }
            return base.AfterStopAsync(cancellationToken);
        }

        /// <summary>
        /// Receive the next DNS query
        /// </summary>
        /// <param name="server">UDP server</param>
        /// <param name="endPoint">Endpoint</param>
        private async Task HandleQueriesAsync(UdpClient server, IPEndPoint endPoint)
        {
            Logging.WriteTrace($"{this} handling DNS queries at {endPoint}");
            // Receive the next DNS query packet
            UdpReceiveResult packet;
            try
            {
                packet = await server.ReceiveAsync(CancelToken).DynamicContext();
                Logging.WriteTrace($"{this} got DNS query ({packet.Buffer.Length} byte) at {endPoint}");
            }
            catch (OperationCanceledException ex)
            {
                try
                {
                    if (ex.CancellationToken != CancelToken)
                    {
                        Logging.WriteWarning($"UDP server {endPoint} ({this}) canceled during receiving UDP packets: {ex}");
                    }
                    else
                    {
                        Logging.WriteTrace($"UDP server {endPoint} ({this}) canceled");
                    }
                    Servers.TryRemove(server, out _);
                    server.Close();
                }
                catch
                {
                }
                server.Dispose();
                if (Servers.IsEmpty && StopTask is null)
                {
                    if (ex.CancellationToken != CancelToken)
                    {
                        Logging.WriteWarning($"{this} is going to stop 'cause there are no listening UDP servers left");
                    }
                    else
                    {
                        Logging.WriteDebug($"{this} is going to stop 'cause there are no listening UDP servers left");
                    }
                    _ = StopAsync();
                }
                return;
            }
            catch (Exception ex)
            {
                try
                {
                    Logging.WriteWarning($"UDP server {endPoint} ({this}) failed to receive UDP packets: {ex}");
                    Servers.TryRemove(server, out _);
                    server.Close();
                }
                catch
                {
                }
                server.Dispose();
                if (Servers.IsEmpty && StopTask is null)
                {
                    Logging.WriteWarning($"{this} is going to stop 'cause there are no listening UDP servers left");
                    _ = StopAsync();
                }
                return;
            }
            // Get ready for the next DNS query
            Servers[server] = HandleQueriesAsync(server, endPoint);
            // Prepare asynchronous DNS query processing
            TaskCompletionSource<byte[]> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int tcsHashCode = tcs.GetHashCode();
            Logging.WriteTrace($"{this} handling DNS query at {endPoint} as #{tcsHashCode}");
            PendingQueries[tcsHashCode] = tcs;
            try
            {
                // Send the DNS query message to the server
                using (MemoryPoolStream msg = new())
                {
                    // Create the message
                    using (RentedArrayStructSimple<byte> buffer = new(len: sizeof(int), clean: false))
                    {
                        tcs.GetHashCode().GetBytes(buffer.Span);
                        msg.Write(buffer.Span);
                    }
                    msg.Write(packet.Buffer);
                    // Send the message
                    using (RentedArrayStructSimple<byte> buffer = new(len: (int)msg.Length, clean: false))
                    {
                        msg.Position = 0;
                        msg.Read(buffer.Span);
                        try
                        {
                            Logging.WriteTrace($"{this} forwarding DNS query at {endPoint} #{tcsHashCode} to resolver");
                            await Resolver!.SendAsync(buffer.Memory, WebSocketMessageType.Binary, endOfMessage: true, CancelToken).DynamicContext();
                        }
                        catch (Exception ex)
                        {
                            Logging.WriteError($"{this} failed to send DNS query at {endPoint} to {AppSettings.Current.Resolver}: {ex}");
                            return;
                        }
                    }
                }
                // Forward the server response to the querying client
                Logging.WriteTrace($"{this} at {endPoint} replying received DNS response");
                await server.SendAsync(await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1)).DynamicContext(), packet.RemoteEndPoint, CancelToken).DynamicContext();
            }
            catch (OperationCanceledException ex)
            {
                if (ex.CancellationToken != CancelToken)
                    Logging.WriteError($"Canceled during handling UDP packet from {packet.RemoteEndPoint} to {endPoint} ({this}): {ex}");
            }
            catch (TimeoutException ex)
            {
                Logging.WriteDebug($"Timeout during handling UDP packet from {packet.RemoteEndPoint} to {endPoint} ({this}): {ex}");
            }
            catch (Exception ex)
            {
                Logging.WriteError($"Error during handling UDP packet from {packet.RemoteEndPoint} to {endPoint} ({this}): {ex}");
            }
            finally
            {
                PendingQueries.TryRemove(tcsHashCode, out _);
            }
        }

        /// <summary>
        /// Handle resolved DNS query responses
        /// </summary>
        private async Task HandleResolvedAsync()
        {
            Logging.WriteTrace($"{this} handling resolver responses");
            using RentedArrayStructSimple<byte> receiveBuffer = new(Math.Max(short.MaxValue, Settings.BufferSize), clean: false);
            using RentedArrayStructSimple<byte> intBuffer = new(len: sizeof(int), clean: false);
            using MemoryPoolStream packet = new();
            ValueWebSocketReceiveResult response;
            int tcsHashCode;
            try
            {
                while (EnsureNotCanceled(throwOnCancellation: false))
                {
                    // Receive the next message
                    response = await Resolver!.ReceiveAsync(receiveBuffer.Memory, CancelToken).DynamicContext();
                    if (response.MessageType != WebSocketMessageType.Binary)
                    {
                        Logging.WriteWarning($"{this} resolver sent {response.MessageType} - stopping handler");
                        return;
                    }
                    Logging.WriteTrace($"{this} got response message {response.MessageType} ({response.Count} byte) from resolver");
                    // Parse the response
                    using (MemoryStream msg = new(receiveBuffer.Array))
                    {
                        if (msg.Read(intBuffer.Span) != intBuffer.Length) throw new InvalidDataException("Failed to read task hash code");
                        tcsHashCode = intBuffer.Span.ToInt();
                        msg.CopyTo(packet);
                    }
                    // Set the DNS query result
                    if (PendingQueries.TryGetValue(tcsHashCode, out TaskCompletionSource<byte[]>? tcs))
                    {
                        Logging.WriteTrace($"{this} received {packet.Length} byte response for #{tcsHashCode}");
                        tcs.TrySetResult(packet.ToArray());
                    }
                    else
                    {
                        Logging.WriteTrace($"{this} discarding received {packet.Length} byte response for #{tcsHashCode}");
                    }
                    packet.SetLength(0);
                }
            }
            catch (OperationCanceledException ex)
            {
                if (ex.CancellationToken != CancelToken)
                    Logging.WriteError($"{this} resolver canceled: {ex}");
            }
            catch (Exception ex)
            {
                Logging.WriteError($"{this} resolver failed with an exception: {ex}");
            }
            finally
            {
                if (Resolver is ClientWebSocket resolver)
                {
                    Resolver = null;
                    try
                    {
                        await resolver.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None).DynamicContext();
                    }
                    catch (Exception ex)
                    {
                        Logging.WriteWarning($"{this} failed to disconnect from the resolver: {ex}");
                    }
                    resolver.Dispose();
                }
                ResolverTask = null;
                if (StopTask is null) _ = StopAsync();
            }
        }
    }
}
