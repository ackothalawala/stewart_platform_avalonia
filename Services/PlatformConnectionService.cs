using System;
using System.IO.Ports;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace stewart_platform.Services
{
    public sealed class PlatformConnectionService : IDisposable
    {
        private SerialPort? serial;
        private ClientWebSocket? socket;
        private CancellationTokenSource? receiverCancel;
        public event Action<string>? LineReceived;
        public static string[] AvailablePorts() => SerialPort.GetPortNames();
        public bool IsConnected => (serial?.IsOpen == true) || socket?.State == WebSocketState.Open;

        public void ConnectSerial(string port, int baudRate)
        {
            Disconnect();
            serial = new SerialPort(port, baudRate) { ReadTimeout = 200 };
            serial.DataReceived += SerialDataReceived;
            try { serial.Open(); }
            catch { serial.DataReceived -= SerialDataReceived; serial.Dispose(); serial = null; throw; }
        }

        public async Task ConnectWebSocketAsync(string url)
        {
            Disconnect();
            var newSocket = new ClientWebSocket();
            try { await newSocket.ConnectAsync(new Uri(url), CancellationToken.None); }
            catch { newSocket.Dispose(); throw; }
            socket = newSocket;
            receiverCancel = new CancellationTokenSource();
            _ = ReceiveWebSocketAsync(newSocket, receiverCancel.Token);
        }

        public async Task SendAsync(string payload)
        {
            if (serial?.IsOpen == true)
            {
                serial.Write(payload + "\n");
                return;
            }
            if (socket?.State == WebSocketState.Open)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                    CancellationToken.None);
                return;
            }
            throw new InvalidOperationException("Platform is not connected.");
        }

        private void SerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                // ReadLine can time out if this was a partial packet; next event can continue.
                if (serial?.IsOpen == true) LineReceived?.Invoke(serial.ReadLine());
            }
            catch (TimeoutException) { }
            catch (InvalidOperationException) { }
            catch (System.IO.IOException) { }
        }

        private async Task ReceiveWebSocketAsync(ClientWebSocket client, CancellationToken token)
        {
            var buffer = new byte[1024];
            var message = new StringBuilder();
            try
            {
                while (!token.IsCancellationRequested && client.State == WebSocketState.Open)
                {
                    var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text) continue;
                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage)
                    {
                        foreach (var line in message.ToString().Split('\n'))
                            if (!string.IsNullOrWhiteSpace(line)) LineReceived?.Invoke(line);
                        message.Clear();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }

        public void Disconnect()
        {
            receiverCancel?.Cancel();
            receiverCancel?.Dispose();
            receiverCancel = null;
            if (serial != null)
            {
                serial.DataReceived -= SerialDataReceived;
                try { serial.Close(); } catch { }
                serial.Dispose();
                serial = null;
            }
            if (socket != null)
            {
                socket.Abort();
                socket.Dispose();
                socket = null;
            }
        }
        public void Dispose() => Disconnect();
    }
}
