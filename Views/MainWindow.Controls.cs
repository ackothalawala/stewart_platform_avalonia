using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Globalization;
using stewart_platform.Services;

namespace stewart_platform
{
    public partial class MainWindow
    {
        private void StatusTimer_Tick(object? sender, EventArgs e)
        {
            UpdateErrorDisplay();
            UpdateSensorDisplay();
        }


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

    }
}
