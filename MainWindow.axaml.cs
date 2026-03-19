using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.IO.Ports;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace stewart_platform
{
    public partial class MainWindow : Window
    {
        // 1. Configuration & Engine
        RobotConfig config = new RobotConfig();
        StewartPlatform platform;

        // 2. Hardware Comms
        SerialPort? arduinoPort;
        ClientWebSocket? wsClient;
        bool isConnected = false;
        bool isWifiMode = false;

        DispatcherTimer sendTimer;
        DispatcherTimer movementTimer;

        // 3. Smooth Movement Engine
        private float[] targetValues = new float[6];
        private bool isInternalUpdate = false;

        public MainWindow()
        {
            InitializeComponent();

            platform = new StewartPlatform(config);
            platform.CalculatePose(0, 0, 0, 0, 0, 0);

            var visualizer = this.FindControl<PlatformView3D>("Visualizer");
            if (visualizer != null)
            {
                visualizer.Platform = platform;
                visualizer.Redraw();
            }

            movementTimer = new DispatcherTimer();
            movementTimer.Interval = TimeSpan.FromMilliseconds(20);
            movementTimer.Tick += MovementTimer_Tick;
            movementTimer.Start();

            sendTimer = new DispatcherTimer();
            sendTimer.Interval = TimeSpan.FromMilliseconds(50);
            sendTimer.Tick += SendTimer_Tick;
        }

        private void MovementTimer_Tick(object? sender, EventArgs e)
        {
            platform.CalculatePose(
                targetValues[0], targetValues[1], targetValues[2],
                targetValues[3], targetValues[4], targetValues[5]
            );

            var visualizer = this.FindControl<PlatformView3D>("Visualizer");
            visualizer?.Redraw();

            UpdateUI();
        }

        private void UpdateUI()
        {
            var txt0 = this.FindControl<TextBlock>("TxtServo0");
            if (txt0 != null) txt0.Text = $"Servo 0: {platform.GetAlphaDegree(0):F2}°";

            var txt1 = this.FindControl<TextBlock>("TxtServo1");
            if (txt1 != null) txt1.Text = $"Servo 1: {platform.GetAlphaDegree(1):F2}°";

            var txt2 = this.FindControl<TextBlock>("TxtServo2");
            if (txt2 != null) txt2.Text = $"Servo 2: {platform.GetAlphaDegree(2):F2}°";

            var txt3 = this.FindControl<TextBlock>("TxtServo3");
            if (txt3 != null) txt3.Text = $"Servo 3: {platform.GetAlphaDegree(3):F2}°";

            var txt4 = this.FindControl<TextBlock>("TxtServo4");
            if (txt4 != null) txt4.Text = $"Servo 4: {platform.GetAlphaDegree(4):F2}°";

            var txt5 = this.FindControl<TextBlock>("TxtServo5");
            if (txt5 != null) txt5.Text = $"Servo 5: {platform.GetAlphaDegree(5):F2}°";
        }

        public void Slider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (isInternalUpdate) return;

            if (sender is Slider slider)
            {
                float value = (float)slider.Value;

                switch (slider.Name)
                {
                    case "SldPosX":
                        targetValues[0] = value;
                        var inpX = this.FindControl<TextBox>("InpPosX");
                        if (inpX != null) inpX.Text = value.ToString("F1");
                        break;
                    case "SldPosY":
                        targetValues[1] = value;
                        var inpY = this.FindControl<TextBox>("InpPosY");
                        if (inpY != null) inpY.Text = value.ToString("F1");
                        break;
                    case "SldPosZ":
                        targetValues[2] = value;
                        var inpZ = this.FindControl<TextBox>("InpPosZ");
                        if (inpZ != null) inpZ.Text = value.ToString("F1");
                        break;
                    case "SldRotX":
                        targetValues[3] = value * (MathF.PI / 180f);
                        var inpRx = this.FindControl<TextBox>("InpRotX");
                        if (inpRx != null) inpRx.Text = value.ToString("F1");
                        break;
                    case "SldRotY":
                        targetValues[4] = value * (MathF.PI / 180f);
                        var inpRy = this.FindControl<TextBox>("InpRotY");
                        if (inpRy != null) inpRy.Text = value.ToString("F1");
                        break;
                    case "SldRotZ":
                        targetValues[5] = value * (MathF.PI / 180f);
                        var inpRz = this.FindControl<TextBox>("InpRotZ");
                        if (inpRz != null) inpRz.Text = value.ToString("F1");
                        break;
                }
            }
        }

        public void BtnSetPos_Click(object? sender, RoutedEventArgs e)
        {
            isInternalUpdate = true;
            try
            {
                var inpX = this.FindControl<TextBox>("InpPosX");
                var inpY = this.FindControl<TextBox>("InpPosY");
                var inpZ = this.FindControl<TextBox>("InpPosZ");

                if (inpX != null && float.TryParse(inpX.Text, out float x)) { targetValues[0] = x; var sld = this.FindControl<Slider>("SldPosX"); if (sld != null) sld.Value = x; }
                if (inpY != null && float.TryParse(inpY.Text, out float y)) { targetValues[1] = y; var sld = this.FindControl<Slider>("SldPosY"); if (sld != null) sld.Value = y; }
                if (inpZ != null && float.TryParse(inpZ.Text, out float z)) { targetValues[2] = z; var sld = this.FindControl<Slider>("SldPosZ"); if (sld != null) sld.Value = z; }
            }
            catch { }
            isInternalUpdate = false;
        }

        public void BtnSetRot_Click(object? sender, RoutedEventArgs e)
        {
            isInternalUpdate = true;
            try
            {
                var inpRx = this.FindControl<TextBox>("InpRotX");
                var inpRy = this.FindControl<TextBox>("InpRotY");
                var inpRz = this.FindControl<TextBox>("InpRotZ");

                if (inpRx != null && float.TryParse(inpRx.Text, out float rx)) { targetValues[3] = rx * (MathF.PI / 180f); var sld = this.FindControl<Slider>("SldRotX"); if (sld != null) sld.Value = rx; }
                if (inpRy != null && float.TryParse(inpRy.Text, out float ry)) { targetValues[4] = ry * (MathF.PI / 180f); var sld = this.FindControl<Slider>("SldRotY"); if (sld != null) sld.Value = ry; }
                if (inpRz != null && float.TryParse(inpRz.Text, out float rz)) { targetValues[5] = rz * (MathF.PI / 180f); var sld = this.FindControl<Slider>("SldRotZ"); if (sld != null) sld.Value = rz; }
            }
            catch { }
            isInternalUpdate = false;
        }

        public async void BtnReset_Click(object? sender, RoutedEventArgs e)
        {
            if (isInternalUpdate) return;
            isInternalUpdate = true;

            var sldPosX = this.FindControl<Slider>("SldPosX");
            var sldPosY = this.FindControl<Slider>("SldPosY");
            var sldPosZ = this.FindControl<Slider>("SldPosZ");
            var sldRotX = this.FindControl<Slider>("SldRotX");
            var sldRotY = this.FindControl<Slider>("SldRotY");
            var sldRotZ = this.FindControl<Slider>("SldRotZ");

            var inpPosX = this.FindControl<TextBox>("InpPosX");
            var inpPosY = this.FindControl<TextBox>("InpPosY");
            var inpPosZ = this.FindControl<TextBox>("InpPosZ");
            var inpRotX = this.FindControl<TextBox>("InpRotX");
            var inpRotY = this.FindControl<TextBox>("InpRotY");
            var inpRotZ = this.FindControl<TextBox>("InpRotZ");

            if (sldPosX == null || sldPosY == null || sldPosZ == null ||
                sldRotX == null || sldRotY == null || sldRotZ == null)
            {
                isInternalUpdate = false;
                return;
            }

            float[] startValues = new float[]
            {
                (float)sldPosX.Value, (float)sldPosY.Value, (float)sldPosZ.Value,
                (float)sldRotX.Value, (float)sldRotY.Value, (float)sldRotZ.Value
            };

            int steps = 30;
            int delayMs = 15;

            for (int step = 1; step <= steps; step++)
            {
                float progress = (float)step / steps;
                float ease = 1.0f - MathF.Pow(1.0f - progress, 3);

                float currentX = startValues[0] * (1 - ease);
                float currentY = startValues[1] * (1 - ease);
                float currentZ = startValues[2] * (1 - ease);
                float currentRx = startValues[3] * (1 - ease);
                float currentRy = startValues[4] * (1 - ease);
                float currentRz = startValues[5] * (1 - ease);

                sldPosX.Value = currentX;
                sldPosY.Value = currentY;
                sldPosZ.Value = currentZ;
                sldRotX.Value = currentRx;
                sldRotY.Value = currentRy;
                sldRotZ.Value = currentRz;

                if (inpPosX != null) inpPosX.Text = currentX.ToString("F1");
                if (inpPosY != null) inpPosY.Text = currentY.ToString("F1");
                if (inpPosZ != null) inpPosZ.Text = currentZ.ToString("F1");
                if (inpRotX != null) inpRotX.Text = currentRx.ToString("F1");
                if (inpRotY != null) inpRotY.Text = currentRy.ToString("F1");
                if (inpRotZ != null) inpRotZ.Text = currentRz.ToString("F1");

                targetValues[0] = currentX;
                targetValues[1] = currentY;
                targetValues[2] = currentZ;
                targetValues[3] = currentRx * (MathF.PI / 180f);
                targetValues[4] = currentRy * (MathF.PI / 180f);
                targetValues[5] = currentRz * (MathF.PI / 180f);

                await Task.Delay(delayMs);
            }

            sldPosX.Value = 0; sldPosY.Value = 0; sldPosZ.Value = 0;
            sldRotX.Value = 0; sldRotY.Value = 0; sldRotZ.Value = 0;

            if (inpPosX != null) inpPosX.Text = "0.0";
            if (inpPosY != null) inpPosY.Text = "0.0";
            if (inpPosZ != null) inpPosZ.Text = "0.0";
            if (inpRotX != null) inpRotX.Text = "0.0";
            if (inpRotY != null) inpRotY.Text = "0.0";
            if (inpRotZ != null) inpRotZ.Text = "0.0";

            for (int i = 0; i < 6; i++) targetValues[i] = 0f;

            isInternalUpdate = false;
        }

        public void BtnConnect_Click(object? sender, RoutedEventArgs e)
        {
            var btnConnect = this.FindControl<Button>("BtnConnect");
            if (btnConnect?.Content?.ToString() == "Disconnect")
            {
                isConnected = false;
                sendTimer.Stop();
                btnConnect.Content = "Connect Platform";

                if (arduinoPort != null && arduinoPort.IsOpen) arduinoPort.Close();
                if (wsClient != null && wsClient.State == WebSocketState.Open) wsClient.Abort();

                var txtStatus = this.FindControl<TextBlock>("TxtStatus");
                if (txtStatus != null)
                {
                    txtStatus.Text = "Disconnected";
                    txtStatus.Foreground = Avalonia.Media.Brushes.Red;
                }
            }
            else
            {
                var modal = this.FindControl<Grid>("ModalOverlay");
                if (modal != null) modal.IsVisible = true;
                RefreshPorts();
            }
        }

        public void BtnModalCancel_Click(object? sender, RoutedEventArgs e)
        {
            var modal = this.FindControl<Grid>("ModalOverlay");
            if (modal != null) modal.IsVisible = false;
        }

        public void BtnRefreshPorts_Click(object? sender, RoutedEventArgs e)
        {
            RefreshPorts();
        }

        // Restored EXACTLY to match your old WPF app logic
        public async void BtnModalConnect_Click(object? sender, RoutedEventArgs e)
        {
            var radioWifi = this.FindControl<RadioButton>("RadioWifi");
            var radioUsb = this.FindControl<RadioButton>("RadioUsb");

            bool isWifi = radioWifi?.IsChecked == true;
            bool isUsb = radioUsb?.IsChecked == true;

            isWifiMode = isWifi;

            var txtStatus = this.FindControl<TextBlock>("TxtStatus");

            try
            {
                if (isWifi)
                {
                    var txtWifiUrl = this.FindControl<TextBox>("TxtWifiUrl");
                    string url = txtWifiUrl?.Text ?? "ws://192.168.4.1:81";

                    wsClient = new ClientWebSocket();
                    await wsClient.ConnectAsync(new Uri(url), CancellationToken.None);

                    _ = Task.Run(() => ReceiveLoop());
                }
                else if (isUsb)
                {
                    var comboSerial = this.FindControl<ComboBox>("ComboUsbPort");
                    string? portName = comboSerial?.SelectedItem as string;

                    if (string.IsNullOrEmpty(portName)) throw new Exception("No COM port selected.");

                    arduinoPort = new SerialPort(portName, 115200);
                    arduinoPort.DataReceived += SerialPort_DataReceived;
                    arduinoPort.Open();
                }

                isConnected = true;
                sendTimer.Start();

                if (txtStatus != null)
                {
                    if (isWifi) txtStatus.Text = "Connected (Wi-Fi)";
                    else if (isUsb) txtStatus.Text = "Connected (USB Serial)";

                    txtStatus.Foreground = Avalonia.Media.Brushes.Green;
                }

                var btnConnect = this.FindControl<Button>("BtnConnect");
                if (btnConnect != null) btnConnect.Content = "Disconnect";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Connection Error: {ex.Message}");
                if (txtStatus != null)
                {
                    txtStatus.Text = "Connection Failed!";
                    txtStatus.Foreground = Avalonia.Media.Brushes.Red;
                }
            }
            finally
            {
                var modal = this.FindControl<Grid>("ModalOverlay");
                if (modal != null) modal.IsVisible = false;
            }
        }

        private void RefreshPorts()
        {
            var ports = SerialPort.GetPortNames();

            var comboUsb = this.FindControl<ComboBox>("ComboUsbPort");
            if (comboUsb != null)
            {
                comboUsb.ItemsSource = ports;
                if (comboUsb.ItemCount > 0) comboUsb.SelectedIndex = 0;
            }
        }

        // Restored to pure single-line read (Doesn't block the buffer like a while loop)
        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                string data = arduinoPort!.ReadLine();
                ProcessIncomingSensorData(data);
            }
            catch { }
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[1024];
            while (wsClient != null && wsClient.State == WebSocketState.Open)
            {
                try
                {
                    var result = await wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    }
                    else
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        ProcessIncomingSensorData(message);
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        private void ProcessIncomingSensorData(string data)
        {
            try
            {
                // .Trim() actively removes invisible \r and \n characters
                data = data.Trim();
                if (!string.IsNullOrWhiteSpace(data) && data.StartsWith("FB:"))
                {
                    string[] parts = data.Substring(3).Split(',');
                    if (parts.Length >= 4)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            var txtRoll = this.FindControl<TextBlock>("TxtSensorRoll");
                            if (txtRoll != null) txtRoll.Text = $"Roll: {parts[0]}°";

                            var txtPitch = this.FindControl<TextBlock>("TxtSensorPitch");
                            if (txtPitch != null) txtPitch.Text = $"Pitch: {parts[1]}°";

                            var txtYaw = this.FindControl<TextBlock>("TxtSensorYaw");
                            if (txtYaw != null) txtYaw.Text = $"Yaw: {parts[2]}°";

                            var txtTemp = this.FindControl<TextBlock>("TxtSensorTemp");
                            if (txtTemp != null) txtTemp.Text = $"Temp: {parts[3]}°C";
                        });
                    }
                }
            }
            catch { }
        }

        // --- CRITICAL FIXES APPLIED HERE ---
        private async void SendTimer_Tick(object? sender, EventArgs e)
        {
            if (!isConnected) return;

            // Ensures numbers are strictly formatted with periods (e.g. 12.5) to prevent Arduino parsing failures.
            string data = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "<{0:F1},{1:F1},{2:F1},{3:F1},{4:F1},{5:F1}>",
                platform.GetAlphaDegree(0), platform.GetAlphaDegree(1), platform.GetAlphaDegree(2),
                platform.GetAlphaDegree(3), platform.GetAlphaDegree(4), platform.GetAlphaDegree(5));

            try
            {
                if (isWifiMode && wsClient?.State == WebSocketState.Open)
                {
                    // For WebSockets: Send purely the packet string. (NO explicit \n here, or ESP32 packet reader fails)
                    var bytes = Encoding.UTF8.GetBytes(data);
                    await wsClient.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                else if (!isWifiMode && arduinoPort != null && arduinoPort.IsOpen)
                {
                    // For Serial: Write() safely appends \n WITHOUT the fatal \r that .NET Core WriteLine() injects.
                    arduinoPort.Write(data + "\n");
                }
            }
            catch
            {
                // Ensures UI sliders never freeze during random buffer drops
            }
        }
    }
}