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
        private float[] targetValues = new float[6]; // X, Y, Z, Rx, Ry, Rz
        private bool isInternalUpdate = false;

        public MainWindow()
        {
            InitializeComponent();

            // Initialize Math Engine
            platform = new StewartPlatform(config);

            // Force an initial calculation so points aren't sitting at (0,0,0)
            platform.CalculatePose(0, 0, 0, 0, 0, 0);

            // Link to the Silk.NET OpenGL Visualizer
            var visualizer = this.FindControl<PlatformView3D>("Visualizer");
            if (visualizer != null)
            {
                visualizer.Platform = platform;
                visualizer.Redraw();
            }

            // Setup Timers using Avalonia.Threading
            movementTimer = new DispatcherTimer();
            movementTimer.Interval = TimeSpan.FromMilliseconds(20);
            movementTimer.Tick += MovementTimer_Tick;
            movementTimer.Start();

            sendTimer = new DispatcherTimer();
            sendTimer.Interval = TimeSpan.FromMilliseconds(50);
            sendTimer.Tick += SendTimer_Tick;
            // sendTimer.Start(); // Start this only when connected
        }

        // --- Core Movement Logic ---

        private void MovementTimer_Tick(object? sender, EventArgs e)
        {
            // Update the platform pose based on targetValues
            platform.CalculatePose(
                targetValues[0], targetValues[1], targetValues[2],
                targetValues[3], targetValues[4], targetValues[5]
            );

            // Force the 3D visualizer to redraw the wireframe
            var visualizer = this.FindControl<PlatformView3D>("Visualizer");
            visualizer?.Redraw();

            // Update UI Text (Requires TextBlocks named TxtServo0, etc. in AXAML)
            UpdateUIText();
        }

        private void UpdateUIText()
        {
            // Use FindControl to safely update text blocks if they exist in UI
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

        // --- UI Event Handlers ---

        // Called when any of your X, Y, Z, Rx, Ry, Rz sliders change
        public void OnSliderValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (isInternalUpdate) return;

            if (sender is Slider slider)
            {
                // Cast the Avalonia double value to float for the math engine
                float value = (float)slider.Value;

                switch (slider.Name)
                {
                    case "SliderX": targetValues[0] = value; break;
                    case "SliderY": targetValues[1] = value; break;
                    case "SliderZ": targetValues[2] = value; break;
                    case "SliderRx": targetValues[3] = value * (MathF.PI / 180f); break; // Convert deg to rad
                    case "SliderRy": targetValues[4] = value * (MathF.PI / 180f); break;
                    case "SliderRz": targetValues[5] = value * (MathF.PI / 180f); break;
                }
            }
        }

        public void BtnReset_Click(object? sender, RoutedEventArgs e)
        {
            isInternalUpdate = true;

            // Reset math targets
            for (int i = 0; i < 6; i++) targetValues[i] = 0f;

            // Reset Sliders in UI
            var sliderX = this.FindControl<Slider>("SliderX");
            if (sliderX != null) sliderX.Value = 0;
            // Repeat for other sliders...

            isInternalUpdate = false;
        }

        // --- Hardware Comms (Mocked from your original) ---

        private async void SendTimer_Tick(object? sender, EventArgs e)
        {
            if (!isConnected) return;

            // Format your data string
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

        public void BtnConnect_Click(object? sender, RoutedEventArgs e)
        {
            // Your original connection logic goes here.
            // Toggle 'isConnected' and start/stop 'sendTimer' accordingly.
        }
    }
}