using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Globalization;
using System.IO.Ports;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace stewart_platform
{
    public partial class MainWindow : Window
    {
        private RobotConfig config = new RobotConfig();
        private StewartPlatform platform;

        // Hardware Comms
        private SerialPort? arduinoPort;
        private ClientWebSocket? wsClient;
        private bool isConnected = false;
        private bool isWifiMode = false;

        // Timers
        private DispatcherTimer movementTimer;
        private DispatcherTimer statusTimer;

        // FIX: Open-loop send timer — was completely missing.
        // Without this, moving sliders in open-loop mode never sends anything to ESP32.
        private DispatcherTimer sendTimer;

        // Open-loop targets
        private float[] targetValues = new float[6];
        private bool isInternalUpdate = false;

        // Closed-loop state
        private bool isClosedLoop = false;
        private float target_roll = 0, target_pitch = 0, target_yaw = 0;
        private float current_roll = 0, current_pitch = 0, current_yaw = 0;
        private const float POSITION_TOLERANCE = 0.5f;

        public MainWindow()
        {
            InitializeComponent();

            platform = new StewartPlatform(config);
            platform.CalculatePose(0, 0, 0, 0, 0, 0);

            var visualizer = this.FindControl<PlatformView3D>("Visualizer");
            if (visualizer != null) { visualizer.Platform = platform; visualizer.Redraw(); }

            // 3D view refresh — 30 Hz
            movementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            movementTimer.Tick += MovementTimer_Tick;
            movementTimer.Start();

            // Status / error display — 10 Hz
            statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            statusTimer.Tick += StatusTimer_Tick;
            statusTimer.Start();

            // FIX: Open-loop servo send — 20 Hz
            // Sends pre-computed IK angles to ESP32 while in open-loop mode.
            sendTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            sendTimer.Tick += SendTimer_Tick;

            UpdateModeUI();
        }

        // ─────────────────────────────────────────────────────────────
        // 3D VIEW REFRESH
        // ─────────────────────────────────────────────────────────────
        private void MovementTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (!isClosedLoop)
                    platform.CalculatePose(
                        targetValues[0], targetValues[1], targetValues[2],
                        targetValues[3], targetValues[4], targetValues[5]);
                else
                    platform.CalculatePose(0, 0, 0,
                        target_roll * (MathF.PI / 180f),
                        target_pitch * (MathF.PI / 180f),
                        target_yaw * (MathF.PI / 180f));

                this.FindControl<PlatformView3D>("Visualizer")?.Redraw();
                UpdateServoUI();
            }
            catch { }
        }

        private void StatusTimer_Tick(object? sender, EventArgs e)
        {
            UpdateErrorDisplay();
            UpdateSensorDisplay();
        }

        // ─────────────────────────────────────────────────────────────
        // OPEN-LOOP SEND TIMER
        // Sends IK-computed servo angles to ESP32 at 20 Hz in open-loop mode.
        // FIX: This was completely absent — sliders had no effect on real servos.
        // ─────────────────────────────────────────────────────────────
        private async void SendTimer_Tick(object? sender, EventArgs e)
        {
            if (!isConnected || isClosedLoop) return;
            try
            {
                // Format: "v0,v1,v2,v3,v4,v5" — integers = angle_degrees × 100
                string data = string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5}",
                    (int)(platform.GetAlphaDegree(0) * 100f),
                    (int)(platform.GetAlphaDegree(1) * 100f),
                    (int)(platform.GetAlphaDegree(2) * 100f),
                    (int)(platform.GetAlphaDegree(3) * 100f),
                    (int)(platform.GetAlphaDegree(4) * 100f),
                    (int)(platform.GetAlphaDegree(5) * 100f));

                if (isWifiMode && wsClient?.State == WebSocketState.Open)
                {
                    var bytes = Encoding.UTF8.GetBytes(data);
                    await wsClient.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, true, CancellationToken.None);
                }
                else if (!isWifiMode && arduinoPort != null && arduinoPort.IsOpen)
                {
                    arduinoPort.Write(data + "\n");
                }
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // UI UPDATE HELPERS
        // ─────────────────────────────────────────────────────────────
        private void UpdateServoUI()
        {
            string[] names = { "TxtServo0","TxtServo1","TxtServo2",
                                "TxtServo3","TxtServo4","TxtServo5" };
            for (int i = 0; i < 6; i++)
            {
                var tb = this.FindControl<TextBlock>(names[i]);
                if (tb != null) tb.Text = $"Servo {i}: {platform.GetAlphaDegree(i):F1}°";
            }
        }

        private void UpdateErrorDisplay()
        {
            var er = this.FindControl<TextBlock>("TxtErrRoll");
            var ep = this.FindControl<TextBlock>("TxtErrPitch");
            var ey = this.FindControl<TextBlock>("TxtErrYaw");
            var ls = this.FindControl<TextBlock>("TxtLoopStatus");

            if (!isClosedLoop)
            {
                if (er != null) { er.Text = "—"; er.Foreground = Brushes.Gray; }
                if (ep != null) { ep.Text = "—"; ep.Foreground = Brushes.Gray; }
                if (ey != null) { ey.Text = "—"; ey.Foreground = Brushes.Gray; }
                if (ls != null) { ls.Text = "—"; ls.Foreground = Brushes.Gray; }
                return;
            }

            float errR = target_roll - current_roll;
            float errP = target_pitch - current_pitch;
            float errY = target_yaw - current_yaw;

            IBrush good = new SolidColorBrush(Color.Parse("#55FF99"));
            IBrush bad = new SolidColorBrush(Color.Parse("#FFAA33"));

            if (er != null) { er.Text = $"{errR:+0.0;-0.0}°"; er.Foreground = MathF.Abs(errR) < POSITION_TOLERANCE ? good : bad; }
            if (ep != null) { ep.Text = $"{errP:+0.0;-0.0}°"; ep.Foreground = MathF.Abs(errP) < POSITION_TOLERANCE ? good : bad; }
            if (ey != null) { ey.Text = $"{errY:+0.0;-0.0}°"; ey.Foreground = MathF.Abs(errY) < POSITION_TOLERANCE ? good : bad; }

            bool locked = MathF.Abs(errR) < POSITION_TOLERANCE &&
                          MathF.Abs(errP) < POSITION_TOLERANCE &&
                          MathF.Abs(errY) < POSITION_TOLERANCE;

            if (ls != null)
            {
                ls.Text = locked ? "✓ LOCKED" : "▶ MOVING";
                ls.Foreground = locked ? good : bad;
            }
        }

        private void UpdateSensorDisplay()
        {
            var sr = this.FindControl<TextBlock>("TxtSensorRoll");
            var sp = this.FindControl<TextBlock>("TxtSensorPitch");
            var sy = this.FindControl<TextBlock>("TxtSensorYaw");

            if (sr != null) sr.Text = $"{current_roll:F1}°";
            if (sp != null) sp.Text = $"{current_pitch:F1}°";
            if (sy != null) sy.Text = $"{current_yaw:F1}°";
        }

        // ─────────────────────────────────────────────────────────────
        // SEND COMMAND (TARGET / MODE strings)
        // ─────────────────────────────────────────────────────────────
        private void SendCommand(string command)
        {
            if (!isConnected) return;
            try
            {
                if (isWifiMode && wsClient?.State == WebSocketState.Open)
                {
                    var bytes = Encoding.UTF8.GetBytes(command + "\n");
                    _ = wsClient.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, true, CancellationToken.None);
                }
                else if (!isWifiMode && arduinoPort != null && arduinoPort.IsOpen)
                {
                    arduinoPort.Write(command + "\n");
                }
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // SLIDER CHANGED
        // ─────────────────────────────────────────────────────────────
        public void Slider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (isInternalUpdate || sender is not Slider slider) return;
            float value = (float)slider.Value;

            if (isClosedLoop)
            {
                switch (slider.Name)
                {
                    case "SldRotX": target_roll = value; SetTextBoxSilent("InpRotX", value.ToString("F1")); break;
                    case "SldRotY": target_pitch = value; SetTextBoxSilent("InpRotY", value.ToString("F1")); break;
                    case "SldRotZ": target_yaw = value; SetTextBoxSilent("InpRotZ", value.ToString("F1")); break;
                }
                SendCommand(string.Format(CultureInfo.InvariantCulture,
                    "TARGET:{0:F2},{1:F2},{2:F2}", target_roll, target_pitch, target_yaw));
            }
            else
            {
                switch (slider.Name)
                {
                    case "SldPosX": targetValues[0] = value; SetTextBoxSilent("InpPosX", value.ToString("F1")); break;
                    case "SldPosY": targetValues[1] = value; SetTextBoxSilent("InpPosY", value.ToString("F1")); break;
                    case "SldPosZ": targetValues[2] = value; SetTextBoxSilent("InpPosZ", value.ToString("F1")); break;
                    case "SldRotX": targetValues[3] = value * (MathF.PI / 180f); SetTextBoxSilent("InpRotX", value.ToString("F1")); break;
                    case "SldRotY": targetValues[4] = value * (MathF.PI / 180f); SetTextBoxSilent("InpRotY", value.ToString("F1")); break;
                    case "SldRotZ": targetValues[5] = value * (MathF.PI / 180f); SetTextBoxSilent("InpRotZ", value.ToString("F1")); break;
                }
                // sendTimer handles the actual open-loop packet at 20 Hz
            }
        }

        // ─────────────────────────────────────────────────────────────
        // MODE TOGGLE
        // ─────────────────────────────────────────────────────────────
        public void BtnModeToggle_Click(object? sender, RoutedEventArgs e)
        {
            isClosedLoop = !isClosedLoop;
            UpdateModeUI();
            if (isConnected)
                SendCommand(isClosedLoop ? "MODE:CLOSED" : "MODE:OPEN");
        }

        private void UpdateModeUI()
        {
            Dispatcher.UIThread.Post(() =>
            {
                var btn = this.FindControl<Button>("BtnModeToggle");
                var status = this.FindControl<TextBlock>("TxtModeStatus");
                var hdr = this.FindControl<TextBlock>("HeaderRotation");
                var panPos = this.FindControl<Border>("PanelPosition");

                if (isClosedLoop)
                {
                    if (btn != null) { btn.Content = "🔒 CLOSED LOOP"; btn.Background = new SolidColorBrush(Color.Parse("#2E4E7E")); }
                    if (status != null) status.Text = "Automatic positioning — MPU active";
                    if (hdr != null) hdr.Text = "Target Rotation (Deg) — Closed Loop";
                    if (panPos != null) panPos.Opacity = 0.35;
                }
                else
                {
                    if (btn != null) { btn.Content = "⚙ OPEN LOOP"; btn.Background = new SolidColorBrush(Color.Parse("#3A4A3A")); }
                    if (status != null) status.Text = "Servos follow sliders directly";
                    if (hdr != null) hdr.Text = "Rotation (Deg) — Open Loop";
                    if (panPos != null) panPos.Opacity = 1.0;
                }
            });
        }

        // ─────────────────────────────────────────────────────────────
        // RESET
        // ─────────────────────────────────────────────────────────────
        public async void BtnReset_Click(object? sender, RoutedEventArgs e)
        {
            if (isInternalUpdate) return;
            isInternalUpdate = true;

            if (isClosedLoop)
            {
                float sr = target_roll, sp2 = target_pitch, sy = target_yaw;

                for (int step = 1; step <= 30; step++)
                {
                    float ease = 1f - MathF.Pow(1f - (float)step / 30f, 3);
                    float cr = sr * (1 - ease);
                    float cp = sp2 * (1 - ease);
                    float cy = sy * (1 - ease);

                    var rx = this.FindControl<Slider>("SldRotX");
                    var ry = this.FindControl<Slider>("SldRotY");
                    var rz = this.FindControl<Slider>("SldRotZ");
                    if (rx != null) rx.Value = cr;
                    if (ry != null) ry.Value = cp;
                    if (rz != null) rz.Value = cy;

                    SetTextBoxSilent("InpRotX", cr.ToString("F1"));
                    SetTextBoxSilent("InpRotY", cp.ToString("F1"));
                    SetTextBoxSilent("InpRotZ", cy.ToString("F1"));

                    if (step % 3 == 0 && isConnected)
                        SendCommand(string.Format(CultureInfo.InvariantCulture,
                            "TARGET:{0:F2},{1:F2},{2:F2}", cr, cp, cy));

                    await Task.Delay(16);
                }

                target_roll = target_pitch = target_yaw = 0;
                var fx = this.FindControl<Slider>("SldRotX");
                var fy = this.FindControl<Slider>("SldRotY");
                var fz = this.FindControl<Slider>("SldRotZ");
                if (fx != null) fx.Value = 0;
                if (fy != null) fy.Value = 0;
                if (fz != null) fz.Value = 0;
                SetTextBoxSilent("InpRotX", "0"); SetTextBoxSilent("InpRotY", "0"); SetTextBoxSilent("InpRotZ", "0");
                if (isConnected) SendCommand("TARGET:0.00,0.00,0.00");
            }
            else
            {
                var px = this.FindControl<Slider>("SldPosX");
                var py = this.FindControl<Slider>("SldPosY");
                var pz = this.FindControl<Slider>("SldPosZ");
                var rx = this.FindControl<Slider>("SldRotX");
                var ry = this.FindControl<Slider>("SldRotY");
                var rz = this.FindControl<Slider>("SldRotZ");
                if (px == null || rx == null) { isInternalUpdate = false; return; }

                float[] start = {
                    (float)px.Value, (float)py!.Value, (float)pz!.Value,
                    (float)rx.Value, (float)ry!.Value, (float)rz!.Value
                };

                for (int step = 1; step <= 30; step++)
                {
                    float ease = 1f - MathF.Pow(1f - (float)step / 30f, 3);
                    float cx = start[0] * (1 - ease), cy2 = start[1] * (1 - ease), cz = start[2] * (1 - ease);
                    float crx = start[3] * (1 - ease), cry = start[4] * (1 - ease), crz = start[5] * (1 - ease);

                    px.Value = cx; py.Value = cy2; pz.Value = cz;
                    rx.Value = crx; ry.Value = cry; rz.Value = crz;

                    SetTextBoxSilent("InpPosX", cx.ToString("F1")); SetTextBoxSilent("InpPosY", cy2.ToString("F1")); SetTextBoxSilent("InpPosZ", cz.ToString("F1"));
                    SetTextBoxSilent("InpRotX", crx.ToString("F1")); SetTextBoxSilent("InpRotY", cry.ToString("F1")); SetTextBoxSilent("InpRotZ", crz.ToString("F1"));

                    targetValues[0] = cx; targetValues[1] = cy2; targetValues[2] = cz;
                    targetValues[3] = crx * (MathF.PI / 180f);
                    targetValues[4] = cry * (MathF.PI / 180f);
                    targetValues[5] = crz * (MathF.PI / 180f);
                    await Task.Delay(16);
                }

                for (int i = 0; i < 6; i++) targetValues[i] = 0;
                px.Value = 0; py.Value = 0; pz.Value = 0; rx.Value = 0; ry.Value = 0; rz.Value = 0;
                SetTextBoxSilent("InpPosX", "0"); SetTextBoxSilent("InpPosY", "0"); SetTextBoxSilent("InpPosZ", "0");
                SetTextBoxSilent("InpRotX", "0"); SetTextBoxSilent("InpRotY", "0"); SetTextBoxSilent("InpRotZ", "0");
            }

            isInternalUpdate = false;
        }

        // ─────────────────────────────────────────────────────────────
        // SET BUTTONS
        // ─────────────────────────────────────────────────────────────
        public void BtnSetRot_Click(object? sender, RoutedEventArgs e)
        {
            isInternalUpdate = true;
            try
            {
                ParseAndApplyRotInput("InpRotX", "SldRotX", ref target_roll, ref targetValues[3]);
                ParseAndApplyRotInput("InpRotY", "SldRotY", ref target_pitch, ref targetValues[4]);
                ParseAndApplyRotInput("InpRotZ", "SldRotZ", ref target_yaw, ref targetValues[5]);

                if (isClosedLoop && isConnected)
                    SendCommand(string.Format(CultureInfo.InvariantCulture,
                        "TARGET:{0:F2},{1:F2},{2:F2}", target_roll, target_pitch, target_yaw));
            }
            finally { isInternalUpdate = false; }
        }

        private void ParseAndApplyRotInput(string inputName, string sliderName,
                                            ref float closedTarget, ref float openTarget)
        {
            var inp = this.FindControl<TextBox>(inputName);
            var sld = this.FindControl<Slider>(sliderName);
            if (inp == null || sld == null) return;
            if (!float.TryParse(inp.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float val)) return;
            sld.Value = val;
            closedTarget = val;
            openTarget = val * (MathF.PI / 180f);
        }

        public void BtnSetPos_Click(object? sender, RoutedEventArgs e)
        {
            isInternalUpdate = true;
            try
            {
                ParseAndApplyPosInput("InpPosX", "SldPosX", ref targetValues[0]);
                ParseAndApplyPosInput("InpPosY", "SldPosY", ref targetValues[1]);
                ParseAndApplyPosInput("InpPosZ", "SldPosZ", ref targetValues[2]);
            }
            finally { isInternalUpdate = false; }
        }

        private void ParseAndApplyPosInput(string inputName, string sliderName, ref float target)
        {
            var inp = this.FindControl<TextBox>(inputName);
            var sld = this.FindControl<Slider>(sliderName);
            if (inp == null || sld == null) return;
            if (!float.TryParse(inp.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float val)) return;
            sld.Value = val;
            target = val;
        }

        private void SetTextBoxSilent(string name, string text)
        {
            var tb = this.FindControl<TextBox>(name);
            if (tb != null) tb.Text = text;
        }

        // ─────────────────────────────────────────────────────────────
        // INCOMING DATA
        // FIX: Parse floats with InvariantCulture so decimal point is
        //      always '.' regardless of the OS locale.
        // FIX: Temperature field removed — packet is now "FB:r,p,y,mode"
        // ─────────────────────────────────────────────────────────────
        private void ProcessIncomingData(string data)
        {
            try
            {
                data = data.Trim();
                if (string.IsNullOrEmpty(data) || !data.StartsWith("FB:")) return;

                var parts = data.Substring(3).Split(',');
                if (parts.Length < 3) return;

                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out current_roll);
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out current_pitch);
                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out current_yaw);
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // CONNECTION
        // ─────────────────────────────────────────────────────────────
        public void BtnConnect_Click(object? sender, RoutedEventArgs e)
        {
            var btn = this.FindControl<Button>("BtnConnect");
            if (btn?.Content?.ToString() == "Disconnect")
                Disconnect();
            else
            {
                var modal = this.FindControl<Grid>("ModalOverlay");
                if (modal != null) modal.IsVisible = true;
                RefreshPorts();
            }
        }

        private void Disconnect()
        {
            isConnected = false;
            sendTimer.Stop();
            var btn = this.FindControl<Button>("BtnConnect");
            if (btn != null) btn.Content = "Connect Platform";
            try { arduinoPort?.Close(); wsClient?.Abort(); } catch { }
            var ts = this.FindControl<TextBlock>("TxtStatus");
            if (ts != null) { ts.Text = "Disconnected"; ts.Foreground = Brushes.Red; }
        }

        public void BtnModalCancel_Click(object? sender, RoutedEventArgs e)
        {
            var modal = this.FindControl<Grid>("ModalOverlay");
            if (modal != null) modal.IsVisible = false;
        }

        public void BtnRefreshPorts_Click(object? sender, RoutedEventArgs e) => RefreshPorts();

        private void RefreshPorts()
        {
            var ports = SerialPort.GetPortNames();
            var cu = this.FindControl<ComboBox>("ComboUsbPort");
            var cd = this.FindControl<ComboBox>("ComboDonglePort");
            if (cu != null) { cu.ItemsSource = ports; if (cu.ItemCount > 0) cu.SelectedIndex = 0; }
            if (cd != null) { cd.ItemsSource = ports; if (cd.ItemCount > 0) cd.SelectedIndex = 0; }
        }

        public async void BtnModalConnect_Click(object? sender, RoutedEventArgs e)
        {
            bool wifi = this.FindControl<RadioButton>("RadioWifi")?.IsChecked == true;
            bool usb = this.FindControl<RadioButton>("RadioUsb")?.IsChecked == true;
            isWifiMode = wifi;
            var ts = this.FindControl<TextBlock>("TxtStatus");

            try
            {
                if (wifi)
                {
                    string url = this.FindControl<TextBox>("TxtWifiUrl")?.Text ?? "ws://192.168.4.1:81";
                    wsClient = new ClientWebSocket();
                    await wsClient.ConnectAsync(new Uri(url), CancellationToken.None);
                    _ = Task.Run(ReceiveLoop);
                }
                else if (usb)
                {
                    string? port = this.FindControl<ComboBox>("ComboUsbPort")?.SelectedItem as string;
                    if (string.IsNullOrEmpty(port)) throw new Exception("No COM port selected.");
                    arduinoPort = new SerialPort(port, 115200);
                    arduinoPort.DataReceived += SerialPort_DataReceived;
                    arduinoPort.Open();
                }
                else
                {
                    string? port = this.FindControl<ComboBox>("ComboDonglePort")?.SelectedItem as string;
                    if (string.IsNullOrEmpty(port)) throw new Exception("No dongle port selected.");
                    arduinoPort = new SerialPort(port, 115200);
                    arduinoPort.DataReceived += SerialPort_DataReceived;
                    arduinoPort.Open();
                    isWifiMode = false;
                }

                isConnected = true;
                sendTimer.Start();      // Start open-loop send timer on connect

                if (ts != null) { ts.Text = wifi ? "Connected (Wi-Fi)" : "Connected (USB Serial)"; ts.Foreground = Brushes.Green; }
                var btn = this.FindControl<Button>("BtnConnect");
                if (btn != null) btn.Content = "Disconnect";

                await Task.Delay(500);  // Give ESP32 time to be ready
                SendCommand(isClosedLoop ? "MODE:CLOSED" : "MODE:OPEN");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Connect] {ex.Message}");
                if (ts != null) { ts.Text = "Connection Failed!"; ts.Foreground = Brushes.Red; }
            }
            finally
            {
                var modal = this.FindControl<Grid>("ModalOverlay");
                if (modal != null) modal.IsVisible = false;
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try { ProcessIncomingData(arduinoPort!.ReadLine()); } catch { }
        }

        private async Task ReceiveLoop()
        {
            var buf = new byte[1024];
            while (wsClient != null && wsClient.State == WebSocketState.Open)
            {
                try
                {
                    var result = await wsClient.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Text)
                        ProcessIncomingData(Encoding.UTF8.GetString(buf, 0, result.Count));
                }
                catch { break; }
            }
        }
    }
}