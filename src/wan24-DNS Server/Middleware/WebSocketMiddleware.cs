using System.Net.WebSockets;
using System.Text;
using wan24.Core;
using wan24.DNS.Config;
using wan24.DNS.Services;

namespace wan24.DNS.Middleware
{
    /// <summary>
    /// WebSocket middleware
    /// </summary>
    public sealed class WebSocketMiddleware
    {
        /// <summary>
        /// Service
        /// </summary>
        private readonly DnsService Service;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="next">Next middleware</param>
        /// <param name="service">Service</param>
#pragma warning disable IDE0060 // Remove unused parameter
        public WebSocketMiddleware(RequestDelegate next, DnsService service) => Service = service;
#pragma warning restore IDE0060 // Remove unused parameter

        /// <summary>
        /// Invoke
        /// </summary>
        /// <param name="context">Context</param>
        public async Task Invoke(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                Logging.WriteDebug($"Invalid non-WebSocket request from {context.Connection.RemoteIpAddress}");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            Logging.WriteTrace($"Handling WebSocket connection from {context.Connection.RemoteIpAddress}");
            using WebSocket client = await context.WebSockets.AcceptWebSocketAsync().WaitAsync(TimeSpan.FromSeconds(1)).DynamicContext();
            try
            {
                Logging.WriteTrace($"WebSocket connection from {context.Connection.RemoteIpAddress} accepted");
                // Receive the authentication token
                string token;
                using (RentedArrayStructSimple<byte> buffer = new(Math.Max(short.MaxValue, Core.Settings.BufferSize), clean: false))
                {
                    Logging.WriteTrace($"Authenticating WebSocket connection from {context.Connection.RemoteIpAddress}");
                    ValueWebSocketReceiveResult auth = await client.ReceiveAsync(buffer.Memory, context.RequestAborted).AsTask().WaitAsync(TimeSpan.FromSeconds(1))
                        .DynamicContext();
                    if (auth.MessageType != WebSocketMessageType.Text)
                    {
                        Logging.WriteTrace($"Invalid WebSocket authentication message {auth.MessageType} from {context.Connection.RemoteIpAddress} received - closing");
                        await client.CloseAsync(WebSocketCloseStatus.ProtocolError, statusDescription: null, context.RequestAborted).WaitAsync(TimeSpan.FromSeconds(1))
                            .DynamicContext();
                        return;
                    }
                    token = Encoding.UTF8.GetString(buffer.Span[..auth.Count]);
                }
                // Authenticate
                Logging.WriteTrace($"Received WebSocket authentication token \"{token}\" from {context.Connection.RemoteIpAddress}");
                if (!AppSettings.Current.AuthToken.Contains(token))
                {
                    Logging.WriteTrace($"Invalid WebSocket authentication from {context.Connection.RemoteIpAddress} received - closing");
                    await client.CloseAsync(WebSocketCloseStatus.PolicyViolation, statusDescription: null, context.RequestAborted).WaitAsync(TimeSpan.FromSeconds(1))
                        .DynamicContext();
                    return;
                }
                Logging.WriteTrace($"WebSocket connection from {context.Connection.RemoteIpAddress} authenticated");
                // Run the DNS client using the DNS service
                DnsService.Client dnsClient = new(Service, new(context.Connection.RemoteIpAddress!, context.Connection.RemotePort), client, token, context.RequestAborted);
                await Service.AddClientAsync(dnsClient).DynamicContext();
                await dnsClient.RunQueryHandler().DynamicContext();
                Logging.WriteTrace($"WebSocket connection from {context.Connection.RemoteIpAddress} handling done");
            }
            catch (OperationCanceledException)
            {
                Logging.WriteTrace($"WebSocket connection from {context.Connection.RemoteIpAddress} closing due cancellation");
            }
            catch (TimeoutException)
            {
                Logging.WriteDebug($"WebSocket connection from {context.Connection.RemoteIpAddress} closing due timeout");
            }
            catch (Exception ex)
            {
                Logging.WriteError($"WebSocket middleware exception: {ex}");
                throw;
            }
        }
    }
}
