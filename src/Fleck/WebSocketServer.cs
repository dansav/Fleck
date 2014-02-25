using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Fleck
{
    public class WebSocketServer : IWebSocketServer
    {
        private const int DefaultListenPort = 8181;

        private readonly bool _secure;
        private readonly IPAddress _listenHostAddress;

        private Action<IWebSocketConnection> _config;
        
        public WebSocketServer(bool secure, IPAddress host, int port)
        {
            _secure = secure;
            Port = port;
            HostName = host.ToString();
            _listenHostAddress = host;

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
            ListenerSocket = new SocketWrapper(socket);
            SupportedSubProtocols = new string[0];
        }

        public static WebSocketServer Create(Uri location)
        {
            bool secure;
            switch (location.Scheme)
            {
                case "wss":
                    secure = true;
                    break;
                case "ws":
                    secure = false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("location", "Uri contains invalid scheme, supported schemes are 'ws' and 'wss'");
            }

            var host = Dns.GetHostAddresses(location.Host).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ?? IPAddress.Loopback;
            return new WebSocketServer(secure, host, location.Port > 0 ? location.Port : DefaultListenPort);
        }

        public ISocket ListenerSocket { get; set; }
        public string HostName { get; private set; }
        public int Port { get; private set; }
        public X509Certificate2 Certificate { get; set; }
        public IEnumerable<string> SupportedSubProtocols { get; set; }

        public bool IsSecure
        {
            get { return _secure && Certificate != null; }
        }

        public void Dispose()
        {
            ListenerSocket.Dispose();
        }

        public void Start(Action<IWebSocketConnection> config)
        {
            if (TryBindListenSocket())
            {
                StartListen(config);
            }
        }

        public bool TryBindListenSocket(int port = 0)
        {
            if (port > 0)
                Port = port;

            var ipLocal = new IPEndPoint(_listenHostAddress, Port);

            try
            {
                ListenerSocket.Bind(ipLocal);
                return true;
            }
            catch (SocketException)
            {
                ListenerSocket.Close();
                return false;
            }
        }

        public void StartListen(Action<IWebSocketConnection> config)
        {
            ListenerSocket.Listen(100);
            FleckLog.Info("Server started at " + HostName);
            if (_secure)
            {
                if (Certificate == null)
                {
                    FleckLog.Info("Scheme cannot be 'wss' without a Certificate");
                    return;
                }
            }
            ListenForClients();
            _config = config;
        }


        private async void ListenForClients()
        {
            ISocket clientSocket = null;
            try
            {
                clientSocket = await ListenerSocket.AcceptAsync();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                FleckLog.Info("Listener socket is closed", ex);
            }

            OnClientConnect(clientSocket);
        }

        private void OnClientConnect(ISocket clientSocket)
        {
            FleckLog.Debug(String.Format("Client connected from {0}:{1}", clientSocket.RemoteIpAddress,
                clientSocket.RemotePort));
            ListenForClients();

            WebSocketConnection connection = null;

            connection = new WebSocketConnection(
                clientSocket,
                _config,
                bytes => RequestParser.Parse(bytes, _secure ? "wss" : "ws"),
                request => HandlerFactory.BuildHandler(request,
                    message => connection.OnMessage(message),
                    connection.Close,
                    data => connection.OnBinary(data),
                    data => connection.OnPing(data),
                    data => connection.OnPong(data)),
                clientProtocols => SubProtocolNegotiator.Negotiate(SupportedSubProtocols, clientProtocols));

            if (IsSecure)
            {
                FleckLog.Debug("Authenticating Secure Connection");
                clientSocket.Authenticate(
                    Certificate,
                    connection.StartReceiving,
                    e => FleckLog.Warn("Failed to Authenticate", e));
            }
            else
            {
                connection.StartReceiving();
            }
        }
    }
}