using System;

namespace stewart_platform.Control
{
    /// <summary>
    /// Coarse roll/pitch feedback correction for a servo-driven Stewart platform.
    /// Only WHOLE-DEGREE errors are used to decide whether to correct. The IK
    /// and servo PWM calculations are intentionally kept at full precision.
    ///
    /// Example: measured +0.6 -> 0, measured -1.8 -> -1.
    /// After truncation, +/-1 degree counts as acceptable (hold band).
    /// This is truncation toward zero, NOT rounding to the nearest degree.
    /// </summary>
    public sealed class PlatformLevelController
    {
        // Set to -1 only if a verified small correction moves that axis the wrong way.
        public double RollCorrectionSign { get; set; } = 1.0;
        public double PitchCorrectionSign { get; set; } = 1.0;

        // These are whole-degree pose corrections, not servo-shaft increments.
        public int MaxTrimDegrees { get; set; } = 4;
        public int MinimumUpdateIntervalMilliseconds { get; set; } = 320;
        public int RequiredConsistentUpdates { get; set; } = 3;
        public int StabilityHoldMilliseconds { get; set; } = 600;

        private int rollTrim;
        private int pitchTrim;
        private int previousRollError;
        private int previousPitchError;
        private int consecutiveRollErrors;
        private int consecutivePitchErrors;
        private DateTime lastCorrectionUtc = DateTime.MinValue;
        private DateTime stableSinceUtc = DateTime.MinValue;

        public double RollError { get; private set; }
        public double PitchError { get; private set; }
        public bool IsStable { get; private set; }
        public bool IsCorrectionLimited { get; private set; }

        // Kept for compatibility with code that displays these properties.
        public double FilteredRoll { get; private set; }
        public double FilteredPitch { get; private set; }

        public void Reset() => ResetWithTrim(0, 0);

        // Begin a fresh leveling pass without dropping the previous calibrated offset.
        public void ResetWithTrim(int initialRollTrim, int initialPitchTrim)
        {
            rollTrim = Math.Clamp(initialRollTrim, -MaxTrimDegrees, MaxTrimDegrees);
            pitchTrim = Math.Clamp(initialPitchTrim, -MaxTrimDegrees, MaxTrimDegrees);
            previousRollError = previousPitchError = 0;
            consecutiveRollErrors = consecutivePitchErrors = 0;
            lastCorrectionUtc = stableSinceUtc = DateTime.MinValue;
            RollError = PitchError = 0;
            FilteredRoll = FilteredPitch = 0;
            IsStable = IsCorrectionLimited = false;
        }

        /// <summary>
        /// Returns roll and pitch pose requests in whole degrees.
        /// seconds is retained to avoid changing the existing WPF call signature.
        /// </summary>
        public (double Roll, double Pitch) Update(
            double targetRoll, double targetPitch,
            double measuredRoll, double measuredPitch,
            double seconds, DateTime nowUtc)
        {
            if (!double.IsFinite(measuredRoll) || !double.IsFinite(measuredPitch) ||
                !double.IsFinite(targetRoll) || !double.IsFinite(targetPitch))
                throw new ArgumentException("Invalid roll/pitch target or IMU feedback.");

            // IMPORTANT: ignore the fractional part entirely for feedback control.
            int targetR = checked((int)Math.Truncate(targetRoll));
            int targetP = checked((int)Math.Truncate(targetPitch));
            int measuredR = checked((int)Math.Truncate(measuredRoll));
            int measuredP = checked((int)Math.Truncate(measuredPitch));
            FilteredRoll = measuredR;
            FilteredPitch = measuredP;

            int rawErrorR = targetR - measuredR;
            int rawErrorP = targetP - measuredP;

            // One-degree hold band: do not command corrections for -1, 0, +1.
            // Only integer values from the IMU enter this decision.
            int errorR = Math.Abs(rawErrorR) <= 1 ? 0 : rawErrorR;
            int errorP = Math.Abs(rawErrorP) <= 1 ? 0 : rawErrorP;
            RollError = errorR;
            PitchError = errorP;

            // Require several consecutive readings showing the same nonzero
            // whole-degree error before allowing another discrete adjustment.
            UpdateConsistency(errorR, ref previousRollError, ref consecutiveRollErrors);
            UpdateConsistency(errorP, ref previousPitchError, ref consecutivePitchErrors);

            if (lastCorrectionUtc == DateTime.MinValue ||
                (nowUtc - lastCorrectionUtc).TotalMilliseconds >= MinimumUpdateIntervalMilliseconds)
            {
                bool changed = false;
                if (errorR != 0 && consecutiveRollErrors >= RequiredConsistentUpdates)
                {
                    int next = Math.Clamp(rollTrim + Direction(RollCorrectionSign) * Math.Sign(errorR),
                                          -MaxTrimDegrees, MaxTrimDegrees);
                    changed |= next != rollTrim;
                    rollTrim = next;
                    consecutiveRollErrors = 0;
                }
                if (errorP != 0 && consecutivePitchErrors >= RequiredConsistentUpdates)
                {
                    int next = Math.Clamp(pitchTrim + Direction(PitchCorrectionSign) * Math.Sign(errorP),
                                          -MaxTrimDegrees, MaxTrimDegrees);
                    changed |= next != pitchTrim;
                    pitchTrim = next;
                    consecutivePitchErrors = 0;
                }
                if (changed) lastCorrectionUtc = nowUtc;
            }

            bool within = errorR == 0 && errorP == 0;
            if (!within) stableSinceUtc = DateTime.MinValue;
            else if (stableSinceUtc == DateTime.MinValue) stableSinceUtc = nowUtc;

            IsStable = within &&
                (nowUtc - stableSinceUtc).TotalMilliseconds >= StabilityHoldMilliseconds;
            IsCorrectionLimited = !within &&
                ((errorR != 0 && Math.Abs(rollTrim) == MaxTrimDegrees) ||
                 (errorP != 0 && Math.Abs(pitchTrim) == MaxTrimDegrees));

            // No fractional command changes in closed-loop roll/pitch.
            // Leave the current trim in place when measured target is reached.
            return (targetR + rollTrim, targetP + pitchTrim);
        }

        private static int Direction(double sign) => sign < 0 ? -1 : 1;

        private static void UpdateConsistency(int error, ref int previous, ref int count)
        {
            if (error == 0)
            {
                count = 0;
            }
            else if (error == previous)
            {
                count = Math.Min(count + 1, 100);
            }
            else
            {
                count = 1;
            }
            previous = error;
        }
    }
}
