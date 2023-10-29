using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using wan24.Core;

namespace wan24.DNS.Services
{
    // Client
    public sealed partial class DnsService
    {
        /// <summary>
        /// Client
        /// </summary>
        public sealed record class Client : DisposableRecordBase
        {
            /// <summary>
            /// Service
            /// </summary>
            private readonly DnsService Service;
            /// <summary>
            /// WebSocket
            /// </summary>
            private readonly WebSocket Socket;
            /// <summary>
            /// Client cancellation
            /// </summary>
            private readonly CancellationTokenSource ClientCancellation = new();
            /// <summary>
            /// Cancellation
            /// </summary>
            private readonly Cancellations Cancellation;
            /// <summary>
            /// Query handler
            /// </summary>
            private Task? QueryHandler = null;

            /// <summary>
            /// Constructor
            /// </summary>
            /// <param name="service">Service</param>
            /// <param name="endPoint">Remote endpoint</param>
            /// <param name="socket">WebSocket</param>
            /// <param name="auth">Authentication</param>
            /// <param name="cancellationToken">Cancellation token</param>
            public Client(DnsService service, IPEndPoint endPoint, WebSocket socket, string auth, CancellationToken cancellationToken) : base()
            {
                Service = service;
                Cancellation = new(service.CancelToken, cancellationToken, ClientCancellation.Token);
                EndPoint = endPoint;
                Socket = socket;
                Authentication = auth;
            }

            /// <summary>
            /// Authentication
            /// </summary>
            public string Authentication { get; }

            /// <summary>
            /// Remote endpoint
            /// </summary>
            public IPEndPoint EndPoint { get; }

            /// <summary>
            /// Cancellation token
            /// </summary>
            public CancellationToken CancelToken => Cancellation;

            /// <summary>
            /// Is handling queries?
            /// </summary>
            public bool IsHandlingQueries => QueryHandler is not null;

            /// <summary>
            /// Run the query handler
            /// </summary>
            /// <returns>Query handler task</returns>
            public Task RunQueryHandler()
            {
                EnsureUndisposed();
                if (IsHandlingQueries) throw new InvalidOperationException();
                Logging.WriteTrace($"Client {EndPoint} running query handler");
                return QueryHandler = HandleQueriesAsync();
            }

            /// <inheritdoc/>
            protected override void Dispose(bool disposing) => DisposeCore().Wait();

            /// <inheritdoc/>
            protected override async Task DisposeCore()
            {
                Logging.WriteTrace($"Client {EndPoint} disposing");
                if (!ClientCancellation.IsCancellationRequested) ClientCancellation.Cancel();
                if(QueryHandler is not null) await QueryHandler.DynamicContext();
                if (Socket?.State == WebSocketState.Open)
                    try
                    {
                        Logging.WriteTrace($"Client {EndPoint} trying to close WebSocket");
                        await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1))
                            .DynamicContext();
                    }
                    catch
                    {
                        Logging.WriteTrace($"Client {EndPoint} closing WebSocket failed");
                    }
                Cancellation?.Dispose();
                ClientCancellation.Dispose();
            }

            /// <summary>
            /// Handle DNS queries
            /// </summary>
            private async Task HandleQueriesAsync()
            {
                Logging.WriteTrace($"Query handler {EndPoint} handling DNS queries");
                using RentedArrayStructSimple<byte> receiveBuffer = new(Math.Max(short.MaxValue, Settings.BufferSize), clean: false);
                using RentedArrayStructSimple<byte> intBuffer = new(len: sizeof(int), clean: false);
                using MemoryPoolStream packet = new();
                ValueWebSocketReceiveResult request;
                int tcsHashCode;
                try
                {
                    while (!Cancellation.IsAnyCanceled)
                    {
                        // Receive the next message
                        request = await Socket.ReceiveAsync(receiveBuffer.Memory, CancelToken).DynamicContext();
                        if (request.MessageType != WebSocketMessageType.Binary)
                        {
                            Logging.WriteWarning($"Query handler {EndPoint} resolver sent {request.MessageType} - stopping handler");
                            return;
                        }
                        Logging.WriteTrace($"Query handler {EndPoint} received message {request.MessageType} ({request.Count} byte)");
                        // Parse the query
                        using (MemoryStream responseStream = new(receiveBuffer.Array))
                        using (PartialStream msg = new(responseStream, request.Count))
                        {
                            if (msg.Read(intBuffer.Span) != intBuffer.Length) throw new InvalidDataException("Failed to read task hash code");
                            tcsHashCode = intBuffer.Span.ToInt();
                            msg.CopyTo(packet);
                        }
                        // Process the query
                        ProcessQueryAsync(tcsHashCode, packet.ToArray());
                        packet.SetLength(0);
                    }
                    Logging.WriteTrace($"Query handler {EndPoint} stopped handling DNS queries");
                }
                catch (SocketException ex)
                {
                    Logging.WriteDebug($"Query handler {EndPoint} catched a socket exception: {ex}");
                }
                catch (WebSocketException ex)
                {
                    Logging.WriteDebug($"Query handler {EndPoint} catched a WebSocket exception: {ex}");
                }
                catch (OperationCanceledException ex)
                {
                    if (!Cancellation.IsAnyCanceled)
                        Logging.WriteError($"Query handler {EndPoint} failed: {ex}");
                }
                catch (Exception ex)
                {
                    Logging.WriteError($"Query handler {EndPoint} failed: {ex}");
                }
                if (!IsDisposing)
                {
                    Logging.WriteError($"Query handler {EndPoint} disposing client");
                    _ = DisposeAsync().AsTask();
                }
            }

            /// <summary>
            /// Process a DNS query
            /// </summary>
            /// <param name="tcsHashCode">Task</param>
            /// <param name="dnsQuery">DNS query</param>
            private async void ProcessQueryAsync(int tcsHashCode, byte[] dnsQuery)
            {
                Logging.WriteTrace($"Query processor {EndPoint} processing query #{tcsHashCode}");
                try
                {
                    // Forward the DNS query to the resolver and receive the response
                    using UdpClient server = new()
                    {
                        DontFragment = true,
                        EnableBroadcast = false
                    };
                    Logging.WriteTrace($"Query processor {EndPoint} forwarding query #{tcsHashCode} to {Service.Resolver}");
                    await server.SendAsync(dnsQuery, Service.Resolver, CancelToken).DynamicContext();
                    UdpReceiveResult packet = await server.ReceiveAsync(CancelToken).AsTask().WaitAsync(TimeSpan.FromSeconds(1), CancelToken).DynamicContext();
                    // Create the response message
                    using MemoryPoolStream msg = new();
                    using (RentedArrayStructSimple<byte> buffer = new(len: sizeof(int), clean: false))
                    {
                        tcsHashCode.GetBytes(buffer.Span);
                        msg.Write(buffer.Span);
                    }
                    msg.Write(packet.Buffer);
                    // Send the response message
                    using (RentedArrayStructSimple<byte> buffer = new(len: (int)msg.Length, clean: false))
                    {
                        Logging.WriteTrace($"Query processor {EndPoint} sending DNS response for query #{tcsHashCode}");
                        msg.Position = 0;
                        msg.Read(buffer.Span);
                        try
                        {
                            await Socket.SendAsync(buffer.Memory, WebSocketMessageType.Binary, endOfMessage: true, CancelToken).DynamicContext();
                        }
                        catch (Exception ex)
                        {
                            Logging.WriteError($"Query processor {EndPoint} failed to send DNS response for query #{tcsHashCode} to {EndPoint}: {ex}");
                        }
                    }
                    Logging.WriteTrace($"Query processor {EndPoint} finished processing query #{tcsHashCode}");
                }
                catch (SocketException ex)
                {
                    Logging.WriteDebug($"Query processor {EndPoint} for query #{tcsHashCode} catched a socket exception: {ex}");
                }
                catch (WebSocketException ex)
                {
                    Logging.WriteDebug($"Query processor {EndPoint} for query #{tcsHashCode} catched a WebSocket exception: {ex}");
                }
                catch (OperationCanceledException ex)
                {
                    if (!Cancellation.IsAnyCanceled)
                        Logging.WriteError($"Query processor {EndPoint} failed for query #{tcsHashCode}: {ex}");
                }
                catch (Exception ex)
                {
                    Logging.WriteError($"Query processor {EndPoint} failed for query #{tcsHashCode}: {ex}");
                }
            }
        }
    }
}
