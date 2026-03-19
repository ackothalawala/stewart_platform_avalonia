using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
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
        // --- Configuration & Engine ---
        RobotConfig config = new RobotConfig();
        StewartPlatform platform;

        // --- Hardware Comms ---
        SerialPort? arduinoPort;
        ClientWebSocket? wsClient;
        bool isConnected = false;
        bool isWifiMode = false;

        DispatcherTimer sendTimer;
        DispatcherTimer movementTimer;

        // --- Smooth Movement ---
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

            // Movement update timer — 50 Hz
            movementTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(20)
            };
            movementTimer.Tick += MovementTimer_Tick;
            movementTimer.Start();

            // Serial/WebSocket send timer — 20 Hz
            sendTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            sendTimer.Tick += SendTimer_Tick;
        }

        // -------------------------------------------------------------------------
        // MOVEMENT TIMER — recalculate kinematics and refresh 3D view
        // -------------------------------------------------------------------------
        private void MovementTimer_Tick(object? sender, EventArgs e)
        {
            platform.CalculatePose(
                targetValues[0], targetValues[1], targetValues[2],
                targetValues[3], targetValues[4], targetValues[5]
            );

            this.FindControl<PlatformView3D>("Visualizer")?.Redraw();
            UpdateUI();
        }

        private void UpdateUI()
        {
            var labels = new string[] { "TxtServo0", "TxtServo1", "TxtServo2", "TxtServo3", "TxtServo4", "TxtServo5" };
            for (int i = 0; i < 6; i++)
            {
                var tb = this.FindControl<TextBlock>(labels[i]);
                if (tb != null) tb.Text = $"Servo {i}: {platform.GetAlphaDegree(i):F2}°";
            }
        }

        // -------------------------------------------------------------------------
        // SLIDER CHANGED
        // -------------------------------------------------------------------------
        public void Slider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (isInternalUpdate) return;
            if (sender is not Slider slider) return;

            float value = (float)slider.Value;

            switch (slider.Name)
            {
                case "SldPosX":
                    targetValues[0] = value;
                    SetTextBoxSilent("InpPosX", value.ToString("F1"));
                    break;
                case "SldPosY":
                    targetValues[1] = value;
                    SetTextBoxSilent("InpPosY", value.ToString("F1"));
                    break;
                case "SldPosZ":
                    targetValues[2] = value;
                    SetTextBoxSilent("InpPosZ", value.ToString("F1"));
                    break;
                case "SldRotX":
                    targetValues[3] = value * (MathF.PI / 180f);
                    SetTextBoxSilent("InpRotX", value.ToString("F1"));
                    break;
                case "SldRotY":
                    targetValues[4] = value * (MathF.PI / 180f);
                    SetTextBoxSilent("InpRotY", value.ToString("F1"));
                    break;
                case "SldRotZ":
                    targetValues[5] = value * (MathF.PI / 180f);
                    SetTextBoxSilent("InpRotZ", value.ToString("F1"));
                    break;
            }
        }

        private void SetTextBoxSilent(string name, string text)
        {
            var tb = this.FindControl<TextBox>(name);
            if (tb != null) tb.Text = text;
        }

        // -------------------------------------------------------------------------
        // SET POSITION from text boxes
        // -------------------------------------------------------------------------
        public void BtnSetPos_Click(object? sender, RoutedEventArgs e)
        {
            isInternalUpdate = true;
            try
            {
                TryApplyTextToSlider("InpPosX", "SldPosX", ref targetValues[0], false);
                TryApplyTextToSlider("InpPosY", "SldPosY", ref targetValues[1], false);
                TryApplyTextToSlider("InpPosZ", "SldPosZ", ref targetValues[2], false);
            }
            finally { isInternalUpdate = false; }
        }

        // -------------------------------------------------------------------------
        // SET ROTATION from text boxes
        // -------------------------------------------------------------------------
        public void BtnSetRot_Click(object? sender, RoutedEventArgs e)
        {
            isInternalUpdate = true;
            try
            {
                TryApplyTextToSlider("InpRotX", "SldRotX", ref targetValues[3], true);
                TryApplyTextToSlider("InpRotY", "SldRotY", ref targetValues[4], true);
                TryApplyTextToSlider("InpRotZ", "SldRotZ", ref targetValues[5], true);
            }
            finally { isInternalUpdate = false; }
        }

        private void TryApplyTextToSlider(string inputName, string sliderName, ref float target, bool toRadians)
        {
            var inp = this.FindControl<TextBox>(inputName);
            var sld = this.FindControl<Slider>(sliderName);
            if (inp != null && sld != null && float.TryParse(inp.Text, out float val))
            {
                sld.Value = val;
                target = toRadians ? val * (MathF.PI / 180f) : val;
            }
        }

        // -------------------------------------------------------------------------
        // RESET — smooth animated return to home
        // -------------------------------------------------------------------------
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

            const int steps = 30;
            const int delayMs = 15;

            for (int step = 1; step <= steps; step++)
            {
                float progress = (float)step / steps;
                float ease = 1.0f - MathF.Pow(1.0f - progress, 3);

                float cx = startValues[0] * (1 - ease);
                float cy = startValues[1] * (1 - ease);
                float cz = startValues[2] * (1 - ease);
                float crx = startValues[3] * (1 - ease);
                float cry = startValues[4] * (1 - ease);
                float crz = startValues[5] * (1 - ease);

                sldPosX.Value = cx; sldPosY.Value = cy; sldPosZ.Value = cz;
                sldRotX.Value = crx; sldRotY.Value = cry; sldRotZ.Value = crz;

                if (inpPosX != null) inpPosX.Text = cx.ToString("F1");
                if (inpPosY != null) inpPosY.Text = cy.ToString("F1");
                if (inpPosZ != null) inpPosZ.Text = cz.ToString("F1");
                if (inpRotX != null) inpRotX.Text = crx.ToString("F1");
                if (inpRotY != null) inpRotY.Text = cry.ToString("F1");
                if (inpRotZ != null) inpRotZ.Text = crz.ToString("F1");

                targetValues[0] = cx;
                targetValues[1] = cy;
                targetValues[2] = cz;
                targetValues[3] = crx * (MathF.PI / 180f);
                targetValues[4] = cry * (MathF.PI / 180f);
                targetValues[5] = crz * (MathF.PI / 180f);

                await Task.Delay(delayMs);
            }

            // Snap to exact zero
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

        // -------------------------------------------------------------------------
        // CONNECT / DISCONNECT button
        // -------------------------------------------------------------------------
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
                    txtStatus.Foreground = Brushes.Red;
                }
            }
            else
            {
                var modal = this.FindControl<Grid>("ModalOverlay");
                if (modal != null) modal.IsVisible = true;
                RefreshPorts();
            }
        }

        // -------------------------------------------------------------------------
        // MODAL buttons
        // -------------------------------------------------------------------------
        public void BtnModalCancel_Click(object? sender, RoutedEventArgs e)
        {
            var modal = this.FindControl<Grid>("ModalOverlay");
            if (modal != null) modal.IsVisible = false;
        }

        public void BtnRefreshPorts_Click(object? sender, RoutedEventArgs e)
        {
            RefreshPorts();
        }

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
                    string url = this.FindControl<TextBox>("TxtWifiUrl")?.Text ?? "ws://192.168.4.1:81";
                    wsClient = new ClientWebSocket();
                    await wsClient.ConnectAsync(new Uri(url), CancellationToken.None);
                    _ = Task.Run(() => ReceiveLoop());
                }
                else if (isUsb)
                {
                    string? portName = this.FindControl<ComboBox>("ComboUsbPort")?.SelectedItem as string;
                    if (string.IsNullOrEmpty(portName)) throw new Exception("No COM port selected.");

                    arduinoPort = new SerialPort(portName, 115200);
                    arduinoPort.DataReceived += SerialPort_DataReceived;
                    arduinoPort.Open();
                }
                else
                {
                    // RF Dongle path — same as USB but uses ComboDonglePort
                    string? portName = this.FindControl<ComboBox>("ComboDonglePort")?.SelectedItem as string;
                    if (string.IsNullOrEmpty(portName)) throw new Exception("No dongle port selected.");

                    arduinoPort = new SerialPort(portName, 115200);
                    arduinoPort.DataReceived += SerialPort_DataReceived;
                    arduinoPort.Open();
                    isWifiMode = false;
                }

                isConnected = true;
                sendTimer.Start();

                if (txtStatus != null)
                {
                    txtStatus.Text = isWifi ? "Connected (Wi-Fi)" : "Connected (USB Serial)";
                    txtStatus.Foreground = Brushes.Green;
                }

                var btnConnect = this.FindControl<Button>("BtnConnect");
                if (btnConnect != null) btnConnect.Content = "Disconnect";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Connection] Error: {ex.Message}");
                if (txtStatus != null)
                {
                    txtStatus.Text = "Connection Failed!";
                    txtStatus.Foreground = Brushes.Red;
                }
            }
            finally
            {
                var modal = this.FindControl<Grid>("ModalOverlay");
                if (modal != null) modal.IsVisible = false;
            }
        }

        // -------------------------------------------------------------------------
        // SERIAL / WEBSOCKET COMMS
        // -------------------------------------------------------------------------
        private void RefreshPorts()
        {
            var ports = SerialPort.GetPortNames();

            var comboUsb = this.FindControl<ComboBox>("ComboUsbPort");
            if (comboUsb != null)
            {
                comboUsb.ItemsSource = ports;
                if (comboUsb.ItemCount > 0) comboUsb.SelectedIndex = 0;
            }

            var comboDongle = this.FindControl<ComboBox>("ComboDonglePort");
            if (comboDongle != null)
            {
                comboDongle.ItemsSource = ports;
                if (comboDongle.ItemCount > 0) comboDongle.SelectedIndex = 0;
            }
        }

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
                catch { break; }
            }
        }

        private void ProcessIncomingSensorData(string data)
        {
            try
            {
                data = data.Trim();
                if (string.IsNullOrWhiteSpace(data) || !data.StartsWith("FB:")) return;

                string[] parts = data.Substring(3).Split(',');
                if (parts.Length < 4) return;

                Dispatcher.UIThread.Post(() =>
                {
                    var r = this.FindControl<TextBlock>("TxtSensorRoll");
                    var p = this.FindControl<TextBlock>("TxtSensorPitch");
                    var y = this.FindControl<TextBlock>("TxtSensorYaw");
                    var t = this.FindControl<TextBlock>("TxtSensorTemp");

                    if (r != null) r.Text = $"Roll: {parts[0]}°";
                    if (p != null) p.Text = $"Pitch: {parts[1]}°";
                    if (y != null) y.Text = $"Yaw: {parts[2]}°";
                    if (t != null) t.Text = $"Temp: {parts[3]}°C";
                });
            }
            catch { }
        }

        private async void SendTimer_Tick(object? sender, EventArgs e)
        {
            if (!isConnected) return;

            try
            {
                if (isWifiMode && wsClient?.State == WebSocketState.Open)
                {
                    // --- WI-FI FORMAT (WebSocket) ---
                    // ESP32 webSocketEvent() uses sscanf "%d,%d,%d,%d,%d,%d"
                    // Values must be integers = angle_degrees * 100
                    // Example:  "1250,-876,1100,0,0,0"
                    string wifiData = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5}",
                        (int)(platform.GetAlphaDegree(0) * 100f),
                        (int)(platform.GetAlphaDegree(1) * 100f),
                        (int)(platform.GetAlphaDegree(2) * 100f),
                        (int)(platform.GetAlphaDegree(3) * 100f),
                        (int)(platform.GetAlphaDegree(4) * 100f),
                        (int)(platform.GetAlphaDegree(5) * 100f));

                    var bytes = Encoding.UTF8.GetBytes(wifiData);
                    await wsClient.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, true, CancellationToken.None);
                }
                else if (!isWifiMode && arduinoPort != null && arduinoPort.IsOpen)
                {
                    // --- USB SERIAL FORMAT ---
                    // ESP32 loop() expects:
                    //   byte 0x6A ('j'), byte 0x6A ('j'),
                    //   then 6x Serial.parseInt() — integers = angle_degrees * 100
                    // The magic bytes are sent as raw bytes, followed by the
                    // ASCII integer string that parseInt() will consume.
                    // Example wire bytes:  j j 1 2 5 0 , - 8 7 6 , ...
                    int[] vals = new int[6];
                    for (int i = 0; i < 6; i++)
                        vals[i] = (int)(platform.GetAlphaDegree(i) * 100f);

                    string intPart = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5}",
                        vals[0], vals[1], vals[2], vals[3], vals[4], vals[5]);

                    // Write the two magic header bytes then the integer payload
                    arduinoPort.Write(new byte[] { 0x6A, 0x6A }, 0, 2);
                    arduinoPort.Write(intPart + "\n");
                }
            }
            catch { /* Drop silently — don't freeze the UI */ }
        }
    }
}