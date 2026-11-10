using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using stewart_platform.Control;
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
        private const float POSITION_TOLERANCE = 1.0f;
        private enum LevelingPhase { OpenLoop, ReturningToZero, Leveling, ManualReady }
        private LevelingPhase levelingPhase = LevelingPhase.OpenLoop;
        private readonly PlatformLevelController levelController = new PlatformLevelController();
        private readonly float[] appliedValues = new float[6];
        private float heldRollTrim, heldPitchTrim;
        private bool hasFeedback;
        private DateTime lastFeedbackUtc = DateTime.MinValue;
        private DateTime lastControllerUtc = DateTime.MinValue;
        private bool commandsInhibited;
        private bool sending;
        private const float MaxSafeTilt = 8f;
        private readonly DispatcherTimer levelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        private static readonly string[] SliderNames = { "SldPosX", "SldPosY", "SldPosZ", "SldRotX", "SldRotY", "SldRotZ" };
        private static readonly string[] InputNames = { "InpPosX", "InpPosY", "InpPosZ", "InpRotX", "InpRotY", "InpRotZ" };

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
            levelTimer.Tick += LevelTimer_Tick;
            levelTimer.Start();

            UpdateModeUI();
        }

        // ─────────────────────────────────────────────────────────────
        // 3D VIEW REFRESH
        // ─────────────────────────────────────────────────────────────
        private void MovementTimer_Tick(object? sender, EventArgs e)
        {
            if (levelingPhase == LevelingPhase.ReturningToZero)
            {
                if (!FeedbackFresh()) { InhibitCommands("Sensor timeout during zero return"); return; }
                bool done = true;
                isInternalUpdate = true;
                try
                {
                    for (int i = 0; i < 6; i++)
                    {
                        float current = targetValues[i];
                        float step = i < 3 ? 0.5f : 0.1f * MathF.PI / 180f;
                        if (MathF.Abs(current) > step) { targetValues[i] -= MathF.Sign(current) * step; done = false; }
                        else targetValues[i] = 0;
                        SetAxisDisplay(i, i < 3 ? targetValues[i] : targetValues[i] * 180f / MathF.PI);
                    }
                }
                finally { isInternalUpdate = false; }
                ApplyCurrentPose();
                if (done) BeginLeveling();
            }
            else if (levelingPhase != LevelingPhase.Leveling)
                ApplyCurrentPose();
            this.FindControl<PlatformView3D>("Visualizer")?.Redraw();
            UpdateServoUI();
        }

        private void SetAxisDisplay(int index, float value)
        {
            var slider = this.FindControl<Slider>(SliderNames[index]);
            if (slider != null) slider.Value = value;
            SetTextBoxSilent(InputNames[index], value.ToString("F1", CultureInfo.InvariantCulture));
        }

        private void ApplyCurrentPose()
        {
            Array.Copy(targetValues, appliedValues, 6);
            if (isClosedLoop)
            {
                appliedValues[3] += heldRollTrim * (MathF.PI / 180f);
                appliedValues[4] += heldPitchTrim * (MathF.PI / 180f);
            }
            platform.CalculatePose(appliedValues[0], appliedValues[1], appliedValues[2],
                appliedValues[3], appliedValues[4], appliedValues[5]);
        }

        private bool FeedbackFresh() => hasFeedback &&
            (DateTime.UtcNow - lastFeedbackUtc).TotalMilliseconds < 350;

        private bool CanLevel()
        {
            if (!isConnected || commandsInhibited) { SetNotice("Connect the ESP32 before leveling.", true); return false; }
            if (!FeedbackFresh() || MathF.Abs(current_roll) > 45 || MathF.Abs(current_pitch) > 45)
            { SetNotice("Recent valid MPU6050 roll/pitch data is required.", true); return false; }
            return true;
        }

        private void BeginReturnToZero()
        {
            if (!CanLevel()) return;
            isClosedLoop = true;
            levelingPhase = LevelingPhase.ReturningToZero;
            UpdateModeUI();
        }

        private void BeginLeveling()
        {
            levelingPhase = LevelingPhase.Leveling;
            levelController.ResetWithTrim((int)MathF.Round(heldRollTrim), (int)MathF.Round(heldPitchTrim));
            lastControllerUtc = DateTime.UtcNow;
            ApplyCurrentPose();
            UpdateModeUI();
        }

        private void LevelTimer_Tick(object? sender, EventArgs e)
        {
            if (levelingPhase != LevelingPhase.Leveling) return;
            if (!isConnected || !FeedbackFresh())
            { InhibitCommands("Sensor feedback lost. Disconnect and inspect hardware."); return; }
            DateTime now = DateTime.UtcNow;
            double dt = (now - lastControllerUtc).TotalSeconds;
            lastControllerUtc = now;
            try
            {
                var (roll, pitch) = levelController.Update(0, 0, current_roll, current_pitch, dt, now);
                heldRollTrim = (float)Math.Clamp(roll, -MaxSafeTilt, MaxSafeTilt);
                heldPitchTrim = (float)Math.Clamp(pitch, -MaxSafeTilt, MaxSafeTilt);
                ApplyCurrentPose();
                if (levelController.IsStable)
                {
                    levelingPhase = LevelingPhase.ManualReady;
                    UpdateModeUI();
                }
                else if (levelController.IsCorrectionLimited)
                    SetNotice("Correction limit reached. Check sensor direction and geometry.", true);
            }
            catch (Exception ex) { InhibitCommands("Controller error: " + ex.Message); }
        }

        private void InhibitCommands(string reason)
        {
            commandsInhibited = true;
            isClosedLoop = false;
            levelingPhase = LevelingPhase.OpenLoop;
            UpdateModeUI();
            SetNotice(reason + " Commands stopped until reconnect.", true);
        }

        private void SetNotice(string message, bool show)
        {
            var notice = this.FindControl<TextBlock>("TxtLevelingNotice");
            if (notice != null) { notice.Text = message; notice.IsVisible = show; }
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
            if (!isConnected || sending || commandsInhibited) return;
            if (isClosedLoop && !FeedbackFresh())
            { InhibitCommands("Sensor timeout"); return; }
            for (int i = 0; i < 6; i++)
            {
                float angle = platform.GetAlphaDegree(i);
                if (float.IsNaN(angle) || float.IsInfinity(angle) || MathF.Abs(angle) > 90f)
                { if (isClosedLoop) InhibitCommands("Unsafe servo angle"); return; }
            }
            sending = true;
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
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Servo send failed: " + ex.Message); }
            finally { sending = false; }
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
            if (er != null) er.Text = isClosedLoop ? $"{levelController.RollError:+0;-0;0}°" : "—";
            if (ep != null) ep.Text = isClosedLoop ? $"{levelController.PitchError:+0;-0;0}°" : "—";
            if (ey != null) ey.Text = "—"; // MPU6050 yaw is gyro-integrated, not absolute.
            if (ls != null) ls.Text = levelingPhase switch
            {
                LevelingPhase.ReturningToZero => "RETURNING TO ZERO",
                LevelingPhase.Leveling => levelController.IsCorrectionLimited ? "LIMIT REACHED" : "LEVELING…",
                LevelingPhase.ManualReady => "STABLE — MANUAL READY",
                _ => "OPEN LOOP"
            };
        }

        

        private void UpdateSensorDisplay()
        {
            var sr = this.FindControl<TextBlock>("TxtSensorRoll");
            var sp = this.FindControl<TextBlock>("TxtSensorPitch");
            var sy = this.FindControl<TextBlock>("TxtSensorYaw");

            if (sr != null) sr.Text = $"{MathF.Truncate(current_roll):F0}°";
            if (sp != null) sp.Text = $"{MathF.Truncate(current_pitch):F0}°";
            if (sy != null) sy.Text = $"{MathF.Truncate(current_yaw):F0}°";
        }

        // ─────────────────────────────────────────────────────────────
        // SEND COMMAND (TARGET / MODE strings)
        // ─────────────────────────────────────────────────────────────
        // The supplied ESP32 firmware does not process MODE:/TARGET: messages.
        // Servo control always transmits the same six-number IK payload.

        public void Slider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (isInternalUpdate || sender is not Slider slider) return;
            if (levelingPhase is LevelingPhase.Leveling or LevelingPhase.ReturningToZero) return;
            int i = Array.IndexOf(SliderNames, slider.Name);
            if (i < 0) return;
            float value = (float)slider.Value;
            targetValues[i] = i < 3 ? value : value * MathF.PI / 180f;
            SetTextBoxSilent(InputNames[i], value.ToString("F1", CultureInfo.InvariantCulture));
            ApplyCurrentPose();
        }

        

        public void BtnModeToggle_Click(object? sender, RoutedEventArgs e)
        {
            if (isClosedLoop)
            {
                isClosedLoop = false;
                levelingPhase = LevelingPhase.OpenLoop;
                heldRollTrim = heldPitchTrim = 0;
                levelController.Reset();
                ApplyCurrentPose();
                UpdateModeUI();
            }
            else BeginReturnToZero();
        }

        

        private void UpdateModeUI()
        {
            var btn = this.FindControl<Button>("BtnModeToggle");
            var status = this.FindControl<TextBlock>("TxtModeStatus");
            var header = this.FindControl<TextBlock>("HeaderRotation");
            var pos = this.FindControl<Border>("PanelPosition");
            var rot = this.FindControl<Border>("PanelRotation");
            var reset = this.FindControl<Button>("BtnReset");
            var tol = this.FindControl<TextBlock>("TxtTolerance");
            bool busy = levelingPhase is LevelingPhase.ReturningToZero or LevelingPhase.Leveling;
            if (btn != null) btn.Content = isClosedLoop ? "🔒 CLOSED LOOP" : "⚙ OPEN LOOP";
            if (status != null) status.Text = levelingPhase switch
            {
                LevelingPhase.ReturningToZero => "Returning to zero",
                LevelingPhase.Leveling => "Auto leveling using MPU6050",
                LevelingPhase.ManualReady => "Leveled — controls unlocked",
                _ => "Servos follow sliders"
            };
            if (header != null) header.Text = "Rotation (Deg)";
            if (pos != null) { pos.IsEnabled = !busy; pos.Opacity = busy ? .4 : 1; }
            if (rot != null) { rot.IsEnabled = !busy; rot.Opacity = busy ? .4 : 1; }
            if (reset != null) reset.IsEnabled = !busy;
            if (tol != null) tol.Text = "±1° (whole)";
            if (busy) SetNotice(levelingPhase == LevelingPhase.ReturningToZero
                ? "Returning all axes to zero. Please wait…"
                : "Stabilizing platform. Please wait until stable…", true);
            else if (levelingPhase == LevelingPhase.ManualReady)
                SetNotice("Stable. You can now control X/Y/Z and roll/pitch/yaw. Reset to level again.", true);
            else SetNotice("", false);
        }

        

        public void BtnReset_Click(object? sender, RoutedEventArgs e)
        {
            if (levelingPhase is LevelingPhase.ReturningToZero or LevelingPhase.Leveling) return;
            if (isClosedLoop) { BeginReturnToZero(); return; }
            isInternalUpdate = true;
            try
            {
                Array.Clear(targetValues, 0, 6);
                for (int i = 0; i < 6; i++) SetAxisDisplay(i, 0);
            }
            finally { isInternalUpdate = false; }
            ApplyCurrentPose();
        }

        

        public void BtnSetRot_Click(object? sender, RoutedEventArgs e)
        {
            if (levelingPhase is LevelingPhase.ReturningToZero or LevelingPhase.Leveling) return;
            ApplyInputs(3, 6);
        }

        public void BtnSetPos_Click(object? sender, RoutedEventArgs e)
        {
            if (levelingPhase is LevelingPhase.ReturningToZero or LevelingPhase.Leveling) return;
            ApplyInputs(0, 3);
        }

        private void ApplyInputs(int start, int end)
        {
            isInternalUpdate = true;
            try
            {
                for (int i = start; i < end; i++)
                {
                    var input = this.FindControl<TextBox>(InputNames[i]);
                    if (input == null || !float.TryParse(input.Text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float value)) continue;
                    var slider = this.FindControl<Slider>(SliderNames[i]);
                    if (slider != null) value = (float)Math.Clamp(value, slider.Minimum, slider.Maximum);
                    targetValues[i] = i < 3 ? value : value * MathF.PI / 180f;
                    SetAxisDisplay(i, value);
                }
            }
            finally { isInternalUpdate = false; }
            ApplyCurrentPose();
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

                if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float roll) ||
                    !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float pitch) ||
                    !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float yaw) ||
                    !float.IsFinite(roll) || !float.IsFinite(pitch) || !float.IsFinite(yaw)) return;
                Dispatcher.UIThread.Post(() =>
                {
                    current_roll = roll;
                    current_pitch = pitch;
                    current_yaw = yaw;
                    hasFeedback = true;
                    lastFeedbackUtc = DateTime.UtcNow;
                });
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
            isClosedLoop = false;
            levelingPhase = LevelingPhase.OpenLoop;
            heldRollTrim = heldPitchTrim = 0;
            levelController.Reset();
            UpdateModeUI();
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
                commandsInhibited = false;
                hasFeedback = false;
                isClosedLoop = false;
                levelingPhase = LevelingPhase.OpenLoop;
                heldRollTrim = heldPitchTrim = 0;
                levelController.Reset();
                UpdateModeUI();
                sendTimer.Start();      // Start open-loop send timer on connect

                if (ts != null) { ts.Text = wifi ? "Connected (Wi-Fi)" : "Connected (USB Serial)"; ts.Foreground = Brushes.Green; }
                var btn = this.FindControl<Button>("BtnConnect");
                if (btn != null) btn.Content = "Disconnect";

                // Controller is in the desktop app. No MODE:/TARGET: firmware commands.
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