using System;

namespace YARG.Core.Audio
{
    /// <summary>
    /// Timestamp math shared by the microphone analyzer and its tests.
    /// </summary>
    public static class MicTimestamp
    {
        /// <summary>
        /// Returns the end time of the samples just read from the analysis
        /// split stream.  Backlog frames are samples that were captured after
        /// the returned block and must be subtracted from the read clock.
        /// </summary>
        public static double GetAnalysisEndTime(double readTime,
            int analysisBacklogFrames, int sampleRate)
        {
            if (sampleRate <= 0)
            {
                return readTime;
            }

            return readTime - Math.Max(0, analysisBacklogFrames) /
                (double) sampleRate;
        }

        /// <summary>
        /// Converts a hardware capture byte position into the frame position
        /// of the analysis data that has actually been consumed.
        /// </summary>
        public static long GetReadFramePosition(long capturePositionBytes,
            int channels, int analysisBacklogFrames)
        {
            if (capturePositionBytes < 0 || channels <= 0)
            {
                return -1;
            }

            long captureFrames = capturePositionBytes /
                (sizeof(float) * channels);
            return Math.Max(0, captureFrames - Math.Max(0, analysisBacklogFrames));
        }
    }
}
