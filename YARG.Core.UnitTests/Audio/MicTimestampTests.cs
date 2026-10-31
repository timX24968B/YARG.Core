using NUnit.Framework;
using YARG.Core.Audio;

namespace YARG.Core.UnitTests.Audio
{
    [TestFixture]
    public class MicTimestampTests
    {
        [Test]
        public void AnalysisEndTimeSubtractsSplitStreamBacklog()
        {
            double end = MicTimestamp.GetAnalysisEndTime(5.0, 512, 48_000);

            Assert.That(end, Is.EqualTo(5.0 - 512.0 / 48_000.0)
                .Within(1e-12));
        }

        [Test]
        public void AnalysisEndTimeClampsNegativeBacklog()
        {
            double end = MicTimestamp.GetAnalysisEndTime(2.0, -4, 48_000);

            Assert.That(end, Is.EqualTo(2.0));
        }

        [Test]
        public void ReadFramePositionUsesAnalysisBacklogNotRecordingWaiting()
        {
            const int channels = 2;
            const long frames = 4_800;
            long captureBytes = frames * sizeof(float) * channels;

            long readFrames = MicTimestamp.GetReadFramePosition(
                captureBytes, channels, 480);

            Assert.That(readFrames, Is.EqualTo(frames - 480));
        }

        [Test]
        public void ReadFramePositionRejectsInvalidInputs()
        {
            Assert.That(MicTimestamp.GetReadFramePosition(-1, 1, 0), Is.EqualTo(-1));
            Assert.That(MicTimestamp.GetReadFramePosition(1_000, 0, 0), Is.EqualTo(-1));
        }

        [Test]
        public void ReadFramePositionClampsToZero()
        {
            long readFrames = MicTimestamp.GetReadFramePosition(400, 1, 1_000);

            Assert.That(readFrames, Is.Zero);
        }
    }
}
