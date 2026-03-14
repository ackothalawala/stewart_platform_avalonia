using Avalonia.Controls;
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
        RobotConfig config = new RobotConfig();
        StewartPlatform platform;

        SerialPort? arduinoPort;
        ClientWebSocket? wsClient;
        bool isConnected = false;
        bool isWifiMode = false;

        DispatcherTimer sendTimer;
        DispatcherTimer movementTimer;

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
            UpdateUIText();
        }

        private void UpdateUIText()
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

        public void OnSliderValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (isInternalUpdate) return;

            if (sender is Slider slider)
            {
                float value = (float)slider.Value;

                switch (slider.Name)
                {
                    case "SliderX":
                        targetValues[0] = value;
                        var txtX = this.FindControl<TextBlock>("TxtX");
                        if (txtX != null) txtX.Text = value.ToString("F1");
                        break;
                    case "SliderY":
                        targetValues[1] = value;
                        var txtY = this.FindControl<TextBlock>("TxtY");
                        if (txtY != null) txtY.Text = value.ToString("F1");
                        break;
                    case "SliderZ":
                        targetValues[2] = value;
                        var txtZ = this.FindControl<TextBlock>("TxtZ");
                        if (txtZ != null) txtZ.Text = value.ToString("F1");
                        break;
                    case "SliderRx":
                        targetValues[3] = value * (MathF.PI / 180f);
                        var txtRx = this.FindControl<TextBlock>("TxtRx");
                        if (txtRx != null) txtRx.Text = value.ToString("F1");
                        break;
                    case "SliderRy":
                        targetValues[4] = value * (MathF.PI / 180f);
                        var txtRy = this.FindControl<TextBlock>("TxtRy");
                        if (txtRy != null) txtRy.Text = value.ToString("F1");
                        break;
                    case "SliderRz":
                        targetValues[5] = value * (MathF.PI / 180f);
                        var txtRz = this.FindControl<TextBlock>("TxtRz");
                        if (txtRz != null) txtRz.Text = value.ToString("F1");
                        break;
                }
            }
        }

        public void BtnReset_Click(object? sender, RoutedEventArgs e)
        {
            isInternalUpdate = true;
            for (int i = 0; i < 6; i++) targetValues[i] = 0f;

            var sliderX = this.FindControl<Slider>("SliderX"); if (sliderX != null) sliderX.Value = 0;
            var sliderY = this.FindControl<Slider>("SliderY"); if (sliderY != null) sliderY.Value = 0;
            var sliderZ = this.FindControl<Slider>("SliderZ"); if (sliderZ != null) sliderZ.Value = 0;
            var sliderRx = this.FindControl<Slider>("SliderRx"); if (sliderRx != null) sliderRx.Value = 0;
            var sliderRy = this.FindControl<Slider>("SliderRy"); if (sliderRy != null) sliderRy.Value = 0;
            var sliderRz = this.FindControl<Slider>("SliderRz"); if (sliderRz != null) sliderRz.Value = 0;

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
                btnConnect.Background = Brush.Parse("#2196F3");

                if (arduinoPort != null && arduinoPort.IsOpen) arduinoPort.Close();
                if (wsClient != null && wsClient.State == WebSocketState.Open) wsClient.Abort();

                var txtStatus = this.FindControl<TextBlock>("TxtStatus");
                if (txtStatus != null)
                {
                    txtStatus.Text = "Disconnected";
                    txtStatus.Foreground = Brush.Parse("#F44336");
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

        public async void BtnModalConnect_Click(object? sender, RoutedEventArgs e)
        {
            var radioWifi = this.FindControl<RadioButton>("RadioWifi");
            isWifiMode = radioWifi?.IsChecked == true;
            var txtStatus = this.FindControl<TextBlock>("TxtStatus");

            try
            {
                if (isWifiMode)
                {
                    var txtWifiUrl = this.FindControl<TextBox>("TxtWifiUrl");
                    string url = txtWifiUrl?.Text ?? "ws://192.168.4.1:81";

                    wsClient = new ClientWebSocket();
                    await wsClient.ConnectAsync(new Uri(url), CancellationToken.None);
                    // Add websocket receive loop here later if ESP32 sends MPU data back
                }
                else
                {
                    var comboDongle = this.FindControl<ComboBox>("ComboDonglePort");
                    string? portName = comboDongle?.SelectedItem?.ToString();

                    if (!string.IsNullOrEmpty(portName))
                    {
                        arduinoPort = new SerialPort(portName, 115200);
                        arduinoPort.Open();
                        arduinoPort.DataReceived += (s, ev) =>
                        {
                            try
                            {
                                string incomingData = arduinoPort.ReadLine();
                                ProcessIncomingMpuData(incomingData);
                            }
                            catch { }
                        };
                    }
                }

                isConnected = true;
                sendTimer.Start();

                if (txtStatus != null)
                {
                    txtStatus.Text = isWifiMode ? "Connected (Wi-Fi)" : "Connected (RF Dongle)";
                    txtStatus.Foreground = Brush.Parse("#4CAF50");
                }

                var btnConnect = this.FindControl<Button>("BtnConnect");
                if (btnConnect != null)
                {
                    btnConnect.Content = "Disconnect";
                    btnConnect.Background = Brush.Parse("#F44336"); // Turn red when connected to imply "Stop"
                }
            }
            catch (Exception)
            {
                if (txtStatus != null)
                {
                    txtStatus.Text = "Connection Failed!";
                    txtStatus.Foreground = Brush.Parse("#F44336");
                }
            }
            finally
            {
                var modal = this.FindControl<Grid>("ModalOverlay");
                if (modal != null) modal.IsVisible = false;
            }
        }

        public void BtnRefreshPorts_Click(object? sender, RoutedEventArgs e)
        {
            RefreshPorts();
        }

        private void RefreshPorts()
        {
            var combo = this.FindControl<ComboBox>("ComboDonglePort");
            if (combo != null)
            {
                combo.ItemsSource = SerialPort.GetPortNames();
                if (combo.ItemCount > 0) combo.SelectedIndex = 0;
            }
        }

        private void ProcessIncomingMpuData(string dataLine)
        {
            // Expected format from Arduino: "MPU:Pitch,Roll,Yaw,Ax,Ay,Az"
            try
            {
                if (!string.IsNullOrWhiteSpace(dataLine) && dataLine.StartsWith("MPU:"))
                {
                    string[] values = dataLine.Substring(4).Trim().Split(',');
                    if (values.Length >= 6)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            var txtPitch = this.FindControl<TextBlock>("TxtMpuPitch");
                            if (txtPitch != null) txtPitch.Text = $"Pitch: {values[0]}°";

                            var txtRoll = this.FindControl<TextBlock>("TxtMpuRoll");
                            if (txtRoll != null) txtRoll.Text = $"Roll: {values[1]}°";

                            var txtYaw = this.FindControl<TextBlock>("TxtMpuYaw");
                            if (txtYaw != null) txtYaw.Text = $"Yaw: {values[2]}°";

                            var txtAx = this.FindControl<TextBlock>("TxtMpuAx");
                            if (txtAx != null) txtAx.Text = $"Ax: {values[3]}";

                            var txtAy = this.FindControl<TextBlock>("TxtMpuAy");
                            if (txtAy != null) txtAy.Text = $"Ay: {values[4]}";

                            var txtAz = this.FindControl<TextBlock>("TxtMpuAz");
                            if (txtAz != null) txtAz.Text = $"Az: {values[5]}";
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MPU Parse Error: {ex.Message}");
            }
        }

        private async void SendTimer_Tick(object? sender, EventArgs e)
        {
            if (!isConnected) return;

            string data = $"<{platform.GetAlphaDegree(0):F1},{platform.GetAlphaDegree(1):F1}," +
                          $"{platform.GetAlphaDegree(2):F1},{platform.GetAlphaDegree(3):F1}," +
                          $"{platform.GetAlphaDegree(4):F1},{platform.GetAlphaDegree(5):F1}>";

            if (isWifiMode && wsClient?.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(data);
                await wsClient.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            else if (!isWifiMode && arduinoPort != null && arduinoPort.IsOpen)
            {
                arduinoPort.WriteLine(data);
            }
        }
    }
}