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
        // ?? Config & Engine ???????????????????????????????????????????
        RobotConfig config = new RobotConfig();
        StewartPlatform platform;

        // ?? Hardware Comms ????????????????????????????????????????????
        SerialPort? arduinoPort;
        ClientWebSocket? wsClient;
        bool isConnected = false;
        bool isWifiMode = false;

        DispatcherTimer sendTimer;
        DispatcherTimer movementTimer;

        // ?? Open-loop slider targets ??????????????????????????????????
        private float[] targetValues = new float[6];
        private bool isInternalUpdate = false;

        // ?????????????????????????????????????????????????????????????
        //  CLOSED-LOOP STATE
        // ?????????????????????????????????????????????????????????????
        private bool isClosedLoop = false;

        // Setpoints (degrees) — what the sliders set in closed-loop mode
        private float sp_roll = 0f;
        private float sp_pitch = 0f;
        private float sp_yaw = 0f;

        // Last MPU readings received from ESP32 feedback
        private float fb_roll = 0f;
        private float fb_pitch = 0f;
        private float fb_yaw = 0f;

        // PID gain send debounce — only send after slider stops for 300ms
        private DispatcherTimer pidSendDebounce;

        // ?? Constructor ???????????????????????????????????????????????
        public MainWindow()
        {
            InitializeComponent();

            platform = new StewartPlatform(config);
            platform.CalculatePose(0, 0, 0, 0, 0, 0);

            var visualizer = this.FindControl<PlatformView3D>("Visualizer");
            if (visualizer != null) { visualizer.Platform = platform; visualizer.Redraw(); }

            // Movement / 3D refresh timer — 50 Hz
            movementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            movementTimer.Tick += MovementTimer_Tick;
            movementTimer.Start();

            // Send timer — 20 Hz
            sendTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            sendTimer.Tick += SendTimer_Tick;

            // PID gain debounce — fires 300 ms after last slider touch
            pidSendDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            pidSendDebounce.Tick += (s, e) => { pidSendDebounce.Stop(); SendPidGains(); };

            // Start with PID panel visible but greyed out
            UpdateModeUI();
        }

        // ?????????????????????????????????????????????????????????????
        //  MOVEMENT TIMER — update IK + 3D view
        // ?????????????????????????????????????????????????????????????
        private void MovementTimer_Tick(object? sender, EventArgs e)
        {
            if (!isClosedLoop)
            {
                // Open loop: drive IK from slider values as before
                platform.CalculatePose(
                    targetValues[0], targetValues[1], targetValues[2],
                    targetValues[3], targetValues[4], targetValues[5]);
            }
            else
            {
                // Closed loop: drive IK from current setpoints so the 3D
                // view shows where we're *trying* to go, not the raw MPU
                platform.CalculatePose(0, 0, 0,
                    sp_roll * (MathF.PI / 180f),
                    sp_pitch * (MathF.PI / 180f),
                    sp_yaw * (MathF.PI / 180f));

                // Update PID error display
                UpdatePidErrorUI();
            }

            this.FindControl<PlatformView3D>("Visualizer")?.Redraw();
            UpdateServoUI();
        }

        private void UpdateServoUI()
        {
            string[] names = { "TxtServo0","TxtServo1","TxtServo2",
                                "TxtServo3","TxtServo4","TxtServo5" };
            for (int i = 0; i < 6; i++)
            {
                var tb = this.FindControl<TextBlock>(names[i]);
                if (tb != null) tb.Text = $"Servo {i}: {platform.GetAlphaDegree(i):F2}°";
            }
        }

        private void UpdatePidErrorUI()
        {
            var er = this.FindControl<TextBlock>("TxtErrRoll");
            var ep = this.FindControl<TextBlock>("TxtErrPitch");
            var ey = this.FindControl<TextBlock>("TxtErrYaw");

            float errR = sp_roll - fb_roll;
            float errP = sp_pitch - fb_pitch;
            float errY = sp_yaw - fb_yaw;

            if (er != null) er.Text = $"{errR:+0.0;-0.0}°";
            if (ep != null) ep.Text = $"{errP:+0.0;-0.0}°";
            if (ey != null) ey.Text = $"{errY:+0.0;-0.0}°";

            // Green when settled (<0.5°), orange otherwise
            IBrush settled = new SolidColorBrush(Color.Parse("#55FF99"));
            IBrush unsettled = new SolidColorBrush(Color.Parse("#FFAA33"));
            if (er != null) er.Foreground = MathF.Abs(errR) < 0.5f ? settled : unsettled;
            if (ep != null) ep.Foreground = MathF.Abs(errP) < 0.5f ? settled : unsettled;
            if (ey != null) ey.Foreground = MathF.Abs(errY) < 0.5f ? settled : unsettled;
        }

        // ?????????????????????????????????????????????????????????????
        //  MODE TOGGLE
        // ?????????????????????????????????????????????????????????????
        public void BtnModeToggle_Click(object? sender, RoutedEventArgs e)
        {
            isClosedLoop = !isClosedLoop;
            UpdateModeUI();

            if (isConnected)
            {
                string cmd = isClosedLoop ? "MODE:CLOSED" : "MODE:OPEN";
                SendRawString(cmd);

                if (isClosedLoop)
                {
                    // Seed setpoints from current slider positions so there's
                    // no sudden jump when entering closed loop
                    var sldRx = this.FindControl<Slider>("SldRotX");
                    var sldRy = this.FindControl<Slider>("SldRotY");
                    var sldRz = this.FindControl<Slider>("SldRotZ");
                    sp_roll = sldRx != null ? (float)sldRx.Value : 0f;
                    sp_pitch = sldRy != null ? (float)sldRy.Value : 0f;
                    sp_yaw = sldRz != null ? (float)sldRz.Value : 0f;
                    SendSetpoint();
                    SendPidGains();
                }
            }
        }

        private void UpdateModeUI()
        {
            var btn = this.FindControl<Button>("BtnModeToggle");
            var status = this.FindControl<TextBlock>("TxtModeStatus");
            var hdr = this.FindControl<TextBlock>("HeaderRotation");
            var panPos = this.FindControl<Border>("PanelPosition");
            var panPid = this.FindControl<Border>("PanelPID");

            if (isClosedLoop)
            {
                if (btn != null) { btn.Content = "??  CLOSED LOOP"; btn.Background = new SolidColorBrush(Color.Parse("#2E4E7E")); }
                if (status != null) status.Text = "MPU feedback active — sliders set target angles";
                if (hdr != null) hdr.Text = "Setpoint (Deg)  —  Closed Loop";
                if (panPos != null) panPos.Opacity = 0.35;   // grey out XYZ translation
                if (panPid != null) panPid.Opacity = 1.0;
            }
            else
            {
                if (btn != null) { btn.Content = "?  OPEN LOOP"; btn.Background = new SolidColorBrush(Color.Parse("#3A4A3A")); }
                if (status != null) status.Text = "Servos follow sliders directly";
                if (hdr != null) hdr.Text = "Rotation (Deg)  —  Open Loop";
                if (panPos != null) panPos.Opacity = 1.0;
                if (panPid != null) panPid.Opacity = 0.45;  // grey out PID panel

                // Clear error display
                var er = this.FindControl<TextBlock>("TxtErrRoll");
                var ep = this.FindControl<TextBlock>("TxtErrPitch");
                var ey = this.FindControl<TextBlock>("TxtErrYaw");
                if (er != null) { er.Text = "—"; er.Foreground = Brushes.Gray; }
                if (ep != null) { ep.Text = "—"; ep.Foreground = Brushes.Gray; }
                if (ey != null) { ey.Text = "—"; ey.Foreground = Brushes.Gray; }
            }
        }

        // ?????????????????????????????????????????????????????????????
        //  SLIDER CHANGED
        // ?????????????????????????????????????????????????????????????
        public void Slider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (isInternalUpdate) return;
            if (sender is not Slider slider) return;
            float value = (float)slider.Value;

            if (isClosedLoop)
            {
                // In closed-loop mode the rotation sliders set the PID setpoint
                switch (slider.Name)
                {
                    case "SldRotX": sp_roll = value; SetTextBoxSilent("InpRotX", value.ToString("F1")); break;
                    case "SldRotY": sp_pitch = value; SetTextBoxSilent("InpRotY", value.ToString("F1")); break;
                    case "SldRotZ": sp_yaw = value; SetTextBoxSilent("InpRotZ", value.ToString("F1")); break;
                }
                if (isConnected) SendSetpoint();
            }
            else
            {
                // Open-loop: original behaviour
                switch (slider.Name)
                {
                    case "SldPosX": targetValues[0] = value; SetTextBoxSilent("InpPosX", value.ToString("F1")); break;
                    case "SldPosY": targetValues[1] = value; SetTextBoxSilent("InpPosY", value.ToString("F1")); break;
                    case "SldPosZ": targetValues[2] = value; SetTextBoxSilent("InpPosZ", value.ToString("F1")); break;
                    case "SldRotX": targetValues[3] = value * (MathF.PI / 180f); SetTextBoxSilent("InpRotX", value.ToString("F1")); break;
                    case "SldRotY": targetValues[4] = value * (MathF.PI / 180f); SetTextBoxSilent("InpRotY", value.ToString("F1")); break;
                    case "SldRotZ": targetValues[5] = value * (MathF.PI / 180f); SetTextBoxSilent("InpRotZ", value.ToString("F1")); break;
                }
            }
        }

        // ?????????????????????????????????????????????????????????????
        //  PID SLIDERS CHANGED
        // ?????????????????????????????????????????????????????????????
        public void PidSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (sender is not Slider s) return;

            // Update the live readout label next to each slider
            string label = s.Name switch
            {
                "SldKpRoll" => "TxtKpRoll",
                "SldKiRoll" => "TxtKiRoll",
                "SldKdRoll" => "TxtKdRoll",
                "SldKpPitch" => "TxtKpPitch",
                "SldKiPitch" => "TxtKiPitch",
                "SldKdPitch" => "TxtKdPitch",
                "SldKpYaw" => "TxtKpYaw",
                "SldKiYaw" => "TxtKiYaw",
                "SldKdYaw" => "TxtKdYaw",
                _ => ""
            };
            if (!string.IsNullOrEmpty(label))
            {
                var tb = this.FindControl<TextBlock>(label);
                if (tb != null) tb.Text = s.Value.ToString("F2");
            }

            // Debounce: don't flood the ESP32 — send 300ms after slider stops
            pidSendDebounce.Stop();
            pidSendDebounce.Start();
        }

        // ?????????????????????????????????????????????????????????????
        //  SEND HELPERS
        // ?????????????????????????????????????????????????????????????

        // Send PID setpoint to ESP32
        private void SendSetpoint()
        {
            string pkt = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "SP:{0:F2},{1:F2},{2:F2}", sp_roll, sp_pitch, sp_yaw);
            SendRawString(pkt);
        }

        // Send all 9 PID gains to ESP32
        private void SendPidGains()
        {
            float Get(string name) => (float)(this.FindControl<Slider>(name)?.Value ?? 0);

            string pkt = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "PID:{0:F3},{1:F3},{2:F3},{3:F3},{4:F3},{5:F3},{6:F3},{7:F3},{8:F3}",
                Get("SldKpRoll"), Get("SldKiRoll"), Get("SldKdRoll"),
                Get("SldKpPitch"), Get("SldKiPitch"), Get("SldKdPitch"),
                Get("SldKpYaw"), Get("SldKiYaw"), Get("SldKdYaw"));
            SendRawString(pkt);
        }

        // Fire-and-forget raw string over whichever transport is active
        private async void SendRawString(string data)
        {
            if (!isConnected) return;
            try
            {
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

        // ?????????????????????????????????????????????????????????????
        //  SEND TIMER — open-loop servo packets only
        // ?????????????????????????????????????????????????????????????
        private async void SendTimer_Tick(object? sender, EventArgs e)
        {
            if (!isConnected || isClosedLoop) return;

            try
            {
                if (isWifiMode && wsClient?.State == WebSocketState.Open)
                {
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
                    int[] vals = new int[6];
                    for (int i = 0; i < 6; i++)
                        vals[i] = (int)(platform.GetAlphaDegree(i) * 100f);
                    string intPart = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5}",
                        vals[0], vals[1], vals[2], vals[3], vals[4], vals[5]);
                    arduinoPort.Write(new byte[] { 0x6A, 0x6A }, 0, 2);
                    arduinoPort.Write(intPart + "\n");
                }
            }
            catch { }
        }

        // ?????????????????????????????????????????????????????????????
        //  INCOMING SENSOR DATA  (extended with mode flag from ESP32)
        //  Packet: "FB:roll,pitch,yaw,temp,mode"
        //  mode: 0=open, 1=closed
        // ?????????????????????????????????????????????????????????????
        private void ProcessIncomingSensorData(string data)
        {
            try
            {
                data = data.Trim();
                if (string.IsNullOrWhiteSpace(data) || !data.StartsWith("FB:")) return;

                string[] parts = data.Substring(3).Split(',');
                if (parts.Length < 4) return;

                if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float r)) fb_roll = r;
                if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float p)) fb_pitch = p;
                if (float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float y)) fb_yaw = y;

                Dispatcher.UIThread.Post(() =>
                {
                    var tr = this.FindControl<TextBlock>("TxtSensorRoll");
                    var tp = this.FindControl<TextBlock>("TxtSensorPitch");
                    var ty = this.FindControl<TextBlock>("TxtSensorYaw");
                    var tt = this.FindControl<TextBlock>("TxtSensorTemp");

                    if (tr != null) tr.Text = $"Roll: {parts[0]}°";
                    if (tp != null) tp.Text = $"Pitch: {parts[1]}°";
                    if (ty != null) ty.Text = $"Yaw: {parts[2]}°";
                    if (tt != null) tt.Text = $"Temp: {parts[3]}°C";
                });
            }
            catch { }
        }

        // ?????????????????????????????????????????????????????????????
        //  SET POSITION / ROTATION FROM TEXT BOX
        // ?????????????????????????????????????????????????????????????
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

        public void BtnSetRot_Click(object? sender, RoutedEventArgs e)
        {
            isInternalUpdate = true;
            try
            {
                if (isClosedLoop)
                {
                    // In closed loop, text boxes set the setpoint directly
                    var inpRx = this.FindControl<TextBox>("InpRotX");
                    var inpRy = this.FindControl<TextBox>("InpRotY");
                    var inpRz = this.FindControl<TextBox>("InpRotZ");
                    var sldRx = this.FindControl<Slider>("SldRotX");
                    var sldRy = this.FindControl<Slider>("SldRotY");
                    var sldRz = this.FindControl<Slider>("SldRotZ");

                    if (inpRx != null && float.TryParse(inpRx.Text, out float rx)) { sp_roll = rx; if (sldRx != null) sldRx.Value = rx; }
                    if (inpRy != null && float.TryParse(inpRy.Text, out float ry)) { sp_pitch = ry; if (sldRy != null) sldRy.Value = ry; }
                    if (inpRz != null && float.TryParse(inpRz.Text, out float rz)) { sp_yaw = rz; if (sldRz != null) sldRz.Value = rz; }
                    if (isConnected) SendSetpoint();
                }
                else
                {
                    TryApplyTextToSlider("InpRotX", "SldRotX", ref targetValues[3], true);
                    TryApplyTextToSlider("InpRotY", "SldRotY", ref targetValues[4], true);
                    TryApplyTextToSlider("InpRotZ", "SldRotZ", ref targetValues[5], true);
                }
            }
            finally { isInternalUpdate = false; }
        }

        private void TryApplyTextToSlider(string inputName, string sliderName,
                                           ref float target, bool toRadians)
        {
            var inp = this.FindControl<TextBox>(inputName);
            var sld = this.FindControl<Slider>(sliderName);
            if (inp != null && sld != null && float.TryParse(inp.Text, out float val))
            {
                sld.Value = val;
                target = toRadians ? val * (MathF.PI / 180f) : val;
            }
        }

        private void SetTextBoxSilent(string name, string text)
        {
            var tb = this.FindControl<TextBox>(name);
            if (tb != null) tb.Text = text;
        }

        // ?????????????????????????????????????????????????????????????
        //  RESET — animated return to home
        // ?????????????????????????????????????????????????????????????
        public async void BtnReset_Click(object? sender, RoutedEventArgs e)
        {
            if (isInternalUpdate) return;
            isInternalUpdate = true;

            // If in closed loop, reset the setpoints to zero and let PID drive back
            if (isClosedLoop)
            {
                sp_roll = sp_pitch = sp_yaw = 0f;
                var sldRx = this.FindControl<Slider>("SldRotX");
                var sldRy = this.FindControl<Slider>("SldRotY");
                var sldRz = this.FindControl<Slider>("SldRotZ");
                if (sldRx != null) sldRx.Value = 0;
                if (sldRy != null) sldRy.Value = 0;
                if (sldRz != null) sldRz.Value = 0;
                SetTextBoxSilent("InpRotX", "0.0");
                SetTextBoxSilent("InpRotY", "0.0");
                SetTextBoxSilent("InpRotZ", "0.0");
                if (isConnected) SendSetpoint();
                isInternalUpdate = false;
                return;
            }

            // Open-loop animated reset
            var sldPosX = this.FindControl<Slider>("SldPosX");
            var sldPosY = this.FindControl<Slider>("SldPosY");
            var sldPosZ = this.FindControl<Slider>("SldPosZ");
            var sldRotX = this.FindControl<Slider>("SldRotX");
            var sldRotY = this.FindControl<Slider>("SldRotY");
            var sldRotZ = this.FindControl<Slider>("SldRotZ");
            if (sldPosX == null || sldRotX == null) { isInternalUpdate = false; return; }

            float[] start = {
                (float)sldPosX.Value, (float)sldPosY!.Value, (float)sldPosZ!.Value,
                (float)sldRotX.Value, (float)sldRotY!.Value, (float)sldRotZ!.Value
            };

            for (int step = 1; step <= 30; step++)
            {
                float ease = 1f - MathF.Pow(1f - (float)step / 30f, 3);
                float cx = start[0] * (1 - ease), cy = start[1] * (1 - ease), cz = start[2] * (1 - ease);
                float crx = start[3] * (1 - ease), cry = start[4] * (1 - ease), crz = start[5] * (1 - ease);

                sldPosX.Value = cx; sldPosY.Value = cy; sldPosZ.Value = cz;
                sldRotX.Value = crx; sldRotY.Value = cry; sldRotZ.Value = crz;
                SetTextBoxSilent("InpPosX", cx.ToString("F1"));
                SetTextBoxSilent("InpPosY", cy.ToString("F1"));
                SetTextBoxSilent("InpPosZ", cz.ToString("F1"));
                SetTextBoxSilent("InpRotX", crx.ToString("F1"));
                SetTextBoxSilent("InpRotY", cry.ToString("F1"));
                SetTextBoxSilent("InpRotZ", crz.ToString("F1"));
                targetValues[0] = cx; targetValues[1] = cy; targetValues[2] = cz;
                targetValues[3] = crx * (MathF.PI / 180f);
                targetValues[4] = cry * (MathF.PI / 180f);
                targetValues[5] = crz * (MathF.PI / 180f);
                await Task.Delay(15);
            }

            for (int i = 0; i < 6; i++) targetValues[i] = 0f;
            sldPosX.Value = 0; sldPosY.Value = 0; sldPosZ.Value = 0;
            sldRotX.Value = 0; sldRotY.Value = 0; sldRotZ.Value = 0;
            SetTextBoxSilent("InpPosX", "0.0"); SetTextBoxSilent("InpPosY", "0.0"); SetTextBoxSilent("InpPosZ", "0.0");
            SetTextBoxSilent("InpRotX", "0.0"); SetTextBoxSilent("InpRotY", "0.0"); SetTextBoxSilent("InpRotZ", "0.0");
            isInternalUpdate = false;
        }

        // ?????????????????????????????????????????????????????????????
        //  CONNECTION
        // ?????????????????????????????????????????????????????????????
        public void BtnConnect_Click(object? sender, RoutedEventArgs e)
        {
            var btn = this.FindControl<Button>("BtnConnect");
            if (btn?.Content?.ToString() == "Disconnect")
            {
                isConnected = false;
                sendTimer.Stop();
                btn.Content = "Connect Platform";
                if (arduinoPort != null && arduinoPort.IsOpen) arduinoPort.Close();
                if (wsClient != null && wsClient.State == WebSocketState.Open) wsClient.Abort();
                var ts = this.FindControl<TextBlock>("TxtStatus");
                if (ts != null) { ts.Text = "Disconnected"; ts.Foreground = Brushes.Red; }
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

        public void BtnRefreshPorts_Click(object? sender, RoutedEventArgs e) => RefreshPorts();

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
                sendTimer.Start();
                if (ts != null) { ts.Text = wifi ? "Connected (Wi-Fi)" : "Connected (USB Serial)"; ts.Foreground = Brushes.Green; }
                var btn = this.FindControl<Button>("BtnConnect");
                if (btn != null) btn.Content = "Disconnect";

                // Sync current mode to ESP32 immediately on connect
                SendRawString(isClosedLoop ? "MODE:CLOSED" : "MODE:OPEN");
                if (isClosedLoop) { SendSetpoint(); SendPidGains(); }
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

        private void RefreshPorts()
        {
            var ports = SerialPort.GetPortNames();
            var cu = this.FindControl<ComboBox>("ComboUsbPort");
            var cd = this.FindControl<ComboBox>("ComboDonglePort");
            if (cu != null) { cu.ItemsSource = ports; if (cu.ItemCount > 0) cu.SelectedIndex = 0; }
            if (cd != null) { cd.ItemsSource = ports; if (cd.ItemCount > 0) cd.SelectedIndex = 0; }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try { ProcessIncomingSensorData(arduinoPort!.ReadLine()); } catch { }
        }

        private async Task ReceiveLoop()
        {
            var buf = new byte[1024];
            while (wsClient != null && wsClient.State == WebSocketState.Open)
            {
                try
                {
                    var result = await wsClient.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                        await wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    else
                        ProcessIncomingSensorData(Encoding.UTF8.GetString(buf, 0, result.Count));
                }
                catch { break; }
            }
        }
    }
}