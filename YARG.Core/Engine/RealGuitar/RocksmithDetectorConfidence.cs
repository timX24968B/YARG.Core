using System;

namespace YARG.Core.Engine.RealGuitar
{
    /// <summary>
    /// Pure ports of the per-frame confidence and attack state recovered from
    /// Rocksmith's FUN_0149e1ba.  Kept separate from the Unity audio analyzer so
    /// the exact arithmetic and gates can be unit tested.
    /// </summary>
    public static class RocksmithDetectorConfidence
    {
        public const float GATE_DB = -55f;
        public const float LEVEL_QUALITY_GATE = 50f;
        public const float ENVELOPE_FLOOR_RATIO = 0.35f;
        public const float STRONG_ATTACK_RISE = 30f;
        public const int STRONG_HISTORY = 5;

        /// <summary>
        /// Initial values written by the Rocksmith detector initializer
        /// (FUN_0149b848) at +0xdd8/+0xddc.
        /// </summary>
        public const float INITIAL_MAX_DB = -96f;
        public const float INITIAL_MIN_DB = 0f;

        /// <summary>
        /// Updates the detector's slow max/min level envelope and returns the
        /// two confidence percentages written to frame +0x750/+0x754.
        /// </summary>
        public static void UpdateLevelEnvelope(ref float maxDb, ref float minDb,
            float levelDb, double deltaSeconds, out float quality,
            out float floorQuality)
        {
            if (double.IsFinite(deltaSeconds) && deltaSeconds > 0.0 &&
                deltaSeconds < 1.0)
            {
                float delta = (float) deltaSeconds;
                maxDb -= delta;
                minDb += delta;

                // FUN_0149e1ba only folds the current frame into the envelope
                // for non-positive levels above the -100 dB sentinel.
                if (levelDb <= 0f && levelDb > -100f)
                {
                    if (minDb > levelDb)
                    {
                        minDb = levelDb;
                    }

                    if (maxDb < levelDb)
                    {
                        maxDb = levelDb;
                    }
                }
            }

            float span = maxDb - minDb;
            quality = span > 0f
                ? (levelDb - minDb) / span * 100f
                : 0f;

            if (maxDb >= GATE_DB)
            {
                float floor = minDb + span * ENVELOPE_FLOOR_RATIO;
                float floorSpan = maxDb - floor;
                floorQuality = floorSpan > 0f
                    ? (levelDb - floor) / floorSpan * 100f
                    : 0f;
                floorQuality = Math.Clamp(floorQuality, 0f, 100f);
            }
            else
            {
                floorQuality = 0f;
            }
        }

        /// <summary>
        /// Frame gate used by FUN_0149f035 and FUN_014a14d9:
        /// +0xdd8 &gt;= -55 and frame +0x750 &gt;= 50.
        /// </summary>
        public static bool IsFrameLive(float maxDb, float quality) =>
            maxDb >= GATE_DB && quality >= LEVEL_QUALITY_GATE;

        /// <summary>
        /// FUN_0149d207's normal onset: the block must be above the gate and
        /// must follow a silent frame (or be the first frame).  A still-ringing
        /// chord therefore does not retrigger +0x795.
        /// </summary>
        public static bool IsNormalOnset(float currentDb, float previousDb,
            bool hasPreviousFrame) =>
            currentDb > GATE_DB &&
            (!hasPreviousFrame ||
                (previousDb <= GATE_DB && currentDb > previousDb));

        /// <summary>
        /// Returns the rise of the current phase-aware spectral flux above the
        /// median of the five-frame flux history.  FUN_0149e1ba computes this
        /// value at detector +0x1278 and sets +0x799 when it crosses 30 after
        /// the previous frame was at or below 30.
        /// </summary>
        public static float UpdateStrongFluxRise(float flux,
            Span<float> history, ref int historyIndex, ref float previousRise,
            out bool strongAttack)
        {
            if (history.Length != STRONG_HISTORY)
            {
                throw new ArgumentException(
                    $"Strong-attack history must have {STRONG_HISTORY} entries.",
                    nameof(history));
            }

            history[historyIndex] = flux;
            historyIndex = (historyIndex + 1) % STRONG_HISTORY;

            Span<float> sorted = stackalloc float[STRONG_HISTORY];
            history.CopyTo(sorted);
            for (int i = 1; i < sorted.Length; i++)
            {
                float value = sorted[i];
                int j = i - 1;
                while (j >= 0 && sorted[j] > value)
                {
                    sorted[j + 1] = sorted[j];
                    j--;
                }
                sorted[j + 1] = value;
            }

            float median = sorted[STRONG_HISTORY / 2];
            float rise = flux - median;
            strongAttack = rise > STRONG_ATTACK_RISE &&
                previousRise < STRONG_ATTACK_RISE;
            previousRise = rise;
            return rise;
        }
    }
}
