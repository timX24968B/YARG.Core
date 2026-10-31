using System;

namespace YARG.Core.Engine.RealGuitar
{
    /// <summary>
    /// Port of the local-maximum candidate selection in FUN_0149f035.
    /// </summary>
    public static class RocksmithNoteCandidates
    {
        public const float NOISE_FLOOR = 5f;

        /// <summary>
        /// Finds strict local maxima in the 128-bin note-level array and
        /// returns the strongest candidates, ordered by descending level.
        /// This mirrors FUN_0149f035: a candidate must be greater than both
        /// adjacent semitone bins.  When <paramref name="useNoiseFloor"/> is
        /// true (Rocksmith InputMode 1) it must also clear the 5.0 floor.
        /// </summary>
        public static int FindLocalMaxima(ReadOnlySpan<float> levels,
            int noteLo, int noteHi, bool useNoiseFloor,
            Span<int> candidateNotes, Span<float> candidateLevels)
        {
            // The source candidate loop reads the right neighbor while
            // scanning noteHi, but the usable Real Guitar range ends at MIDI
            // 127.  Treat the array boundary as silence so note 127 remains a
            // valid candidate without requiring a 129-entry level buffer.
            if (noteLo < 1 || noteHi >= levels.Length || noteLo > noteHi)
            {
                throw new ArgumentOutOfRangeException(nameof(noteLo));
            }

            if (candidateNotes.Length != candidateLevels.Length)
            {
                throw new ArgumentException(
                    "Candidate note and level buffers must have equal length.");
            }

            // FUN_0149f035 first accumulates every local maximum (up to 128),
            // then FUN_014a3dd0 sorts by level and the frame keeps the top 20.
            Span<int> foundNotes = stackalloc int[128];
            Span<float> foundLevels = stackalloc float[128];
            int foundCount = 0;
            for (int note = noteLo; note <= noteHi; note++)
            {
                float value = levels[note];
                if (float.IsNaN(value) || value <= 0f)
                {
                    continue;
                }

                float right = note + 1 < levels.Length ? levels[note + 1] : 0f;
                if (levels[note - 1] >= value || right >= value)
                {
                    continue;
                }

                if (useNoiseFloor && value <= NOISE_FLOOR)
                {
                    continue;
                }

                if (foundCount == foundNotes.Length)
                {
                    break;
                }

                foundNotes[foundCount] = note;
                foundLevels[foundCount] = value;
                foundCount++;
            }

            for (int i = 1; i < foundCount; i++)
            {
                int note = foundNotes[i];
                float level = foundLevels[i];
                int j = i - 1;
                while (j >= 0 && foundLevels[j] < level)
                {
                    foundNotes[j + 1] = foundNotes[j];
                    foundLevels[j + 1] = foundLevels[j];
                    j--;
                }

                foundNotes[j + 1] = note;
                foundLevels[j + 1] = level;
            }

            int count = Math.Min(foundCount, candidateNotes.Length);
            for (int i = 0; i < count; i++)
            {
                candidateNotes[i] = foundNotes[i];
                candidateLevels[i] = foundLevels[i];
            }

            return count;
        }
    }
}
