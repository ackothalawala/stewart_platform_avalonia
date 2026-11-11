using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using stewart_platform.Control;
using stewart_platform.Services;
using System;

namespace stewart_platform
{
    public partial class MainWindow : Window
    {
        private RobotConfig config = new RobotConfig();
        private StewartPlatform platform;

        // Hardware Comms
        private readonly PlatformConnectionService connection = new PlatformConnectionService();
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
            connection.LineReceived += ProcessIncomingData;

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
    }
}
