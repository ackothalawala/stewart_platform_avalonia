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

    }
}
