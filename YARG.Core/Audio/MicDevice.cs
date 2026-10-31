using System;
using System.Collections.Generic;
using System.Text;

namespace YARG.Core.Audio
{
    public readonly struct MicOutputFrame
    {
        /// <summary>
        /// The time of the input. This is NOT relative!
        /// </summary>
        public readonly double Time;

        /// <summary>
        /// Whether or not this output frame is a mic hit, or a sing output.
        /// </summary>
        public readonly bool IsHit;

        /// <summary>
        /// Pitch (in hertz) of the microphone.
        /// </summary>
        public readonly float Pitch;

        /// <summary>
        /// Volume (in dB) of the microphone.
        /// </summary>
        public readonly float Volume;

        /// <summary>
        /// MIDI notes 0-63 detected in the current analysis window.
        /// </summary>
        public readonly ulong NoteMaskLow;

        /// <summary>
        /// MIDI notes 64-127 detected in the current analysis window.
        /// </summary>
        public readonly ulong NoteMaskHigh;

        /// <summary>
        /// MIDI notes 0-63 that are strong enough to be used as a chord
        /// confidence mask.  This is stricter than <see cref="NoteMaskLow"/>.
        /// </summary>
        public readonly ulong StrongNoteMaskLow;

        /// <summary>
        /// MIDI notes 64-127 that are strong enough to be used as a chord
        /// confidence mask.  This is stricter than <see cref="NoteMaskHigh"/>.
        /// </summary>
        public readonly ulong StrongNoteMaskHigh;

        /// <summary>
        /// Packed top-N candidate MIDI notes, one byte each.
        /// </summary>
        public readonly ulong CandidateMidiPacked;

        /// <summary>
        /// Packed top-N candidate levels, one byte each (0-255).
        /// </summary>
        public readonly ulong CandidateLevelPacked;

        /// <summary>Second packed candidate MIDI group (candidates 8-15).</summary>
        public readonly ulong CandidateMidiPacked2;

        /// <summary>Second packed candidate level group (candidates 8-15).</summary>
        public readonly ulong CandidateLevelPacked2;

        /// <summary>Number of valid candidates in the packed fields.</summary>
        public readonly int CandidateCount;

        /// <summary>Onset MIDI note for an attack frame, or zero.</summary>
        public readonly int OnsetNote;

        /// <summary>Whether the onset was a strong attack.</summary>
        public readonly bool StrongAttack;

        /// <summary>Whether the analysis frame was input-clipped.</summary>
        public readonly bool Clipped;

        /// <summary>Detected chord root MIDI note, or zero.</summary>
        public readonly int ChordRoot;

        /// <summary>Whether the stable chord mask changed in this frame.</summary>
        public readonly bool ChordChanged;

        /// <summary>Whether this is still in the new-chord window.</summary>
        public readonly bool ChordNew;

        /// <summary>Age of the current chord state in detector frames.</summary>
        public readonly int ChordAgeFrames;

        /// <summary>
        /// Rocksmith frame quality bits (+0x7b6/+0x7c4).  These are confidence
        /// hints for a changed/held chord, not note identities.
        /// </summary>
        public readonly ChordQualityFlags QualityFlags;

        /// <summary>Refined fractional pitch for each candidate note.</summary>
        public readonly float[]? CandidatePitches;
        public readonly int[]? CandidateNotes;

        /// <summary>Refined fractional root pitch for the arpeggio query.</summary>
        public readonly float RootPitch;
        public readonly float PitchTolerance;

        /// <summary>
        /// Monotonic detector frame identifier (+0x748 equivalent).
        /// </summary>
        public readonly int FrameId;

        /// <summary>Slow detector level envelope (+0xdd8 equivalent).</summary>
        public readonly float DetectorLevelDb;

        /// <summary>Detector confidence score (+0xd38 equivalent).</summary>
        public readonly float DetectorQuality;

        /// <summary>
        /// Immediate detector active state used by the source
        /// +0xd7d/+0xd81 fret-hand/ignore query.
        /// </summary>
        public readonly bool ImmediateState;

        /// <summary>
        /// Full 128-bin note-level histogram (0-255 relative to the frame
        /// maximum), or null for legacy/analysis frames that do not provide
        /// one.  This is the data Rocksmith's chord matcher consumes.
        /// </summary>
        public readonly byte[]? NoteLevels;

        /// <summary>
        /// Unquantized 128-bin detector histogram for exact matching, using
        /// the same 0-255 relative scale as <see cref="NoteLevels"/>.
        /// </summary>
        public readonly float[]? NoteLevelsFloat;

        /// <summary>Whether this chord frame carried a detector onset.</summary>
        public readonly bool Onset;

        /// <summary>Full-window sample peak for the detector frame.</summary>
        public readonly float Peak;

        /// <summary>
        /// Whether this frame carries a polyphonic chord mask.  Pitch frames
        /// and vocal hit frames leave this false; only the Real Guitar
        /// detector's mask frames should update or clear the engine's mask.
        /// </summary>
        public readonly bool IsChordFrame;

        /// <summary>
        /// Gets the pitch as a MIDI note.
        /// </summary>
        public float PitchAsMidiNote => 12f * MathF.Log(Pitch / 440f, 2f) + 69f;

        public MicOutputFrame(double time, bool isHit, float pitch, float volume)
            : this(time, isHit, pitch, volume, 0, 0)
        {
        }

        public MicOutputFrame(double time, bool isHit, float pitch, float volume,
            ulong noteMaskLow, ulong noteMaskHigh, bool isChordFrame = false,
            ulong strongNoteMaskLow = 0, ulong strongNoteMaskHigh = 0,
            ulong candidateMidiPacked = 0, ulong candidateLevelPacked = 0,
            ulong candidateMidiPacked2 = 0, ulong candidateLevelPacked2 = 0,
            int candidateCount = 0, int onsetNote = 0, bool strongAttack = false,
            bool clipped = false, int chordRoot = 0, bool chordChanged = false,
            bool chordNew = false, int chordAgeFrames = 0, byte[]? noteLevels = null,
            bool onset = false, float peak = 0f, float[]? noteLevelsFloat = null,
            ChordQualityFlags qualityFlags = ChordQualityFlags.None,
            float[]? candidatePitches = null, int[]? candidateNotes = null,
            float rootPitch = 0f, float pitchTolerance = 0.05f,
            int frameId = 0, float detectorLevelDb = 0f,
            float detectorQuality = 100f, bool immediateState = false)
        {
            Time = time;
            IsHit = isHit;
            Pitch = pitch;
            Volume = volume;
            NoteMaskLow = noteMaskLow;
            NoteMaskHigh = noteMaskHigh;
            IsChordFrame = isChordFrame;
            StrongNoteMaskLow = strongNoteMaskLow;
            StrongNoteMaskHigh = strongNoteMaskHigh;
            CandidateMidiPacked = candidateMidiPacked;
            CandidateLevelPacked = candidateLevelPacked;
            CandidateMidiPacked2 = candidateMidiPacked2;
            CandidateLevelPacked2 = candidateLevelPacked2;
            CandidateCount = candidateCount;
            OnsetNote = onsetNote;
            StrongAttack = strongAttack;
            Clipped = clipped;
            ChordRoot = chordRoot;
            ChordChanged = chordChanged;
            ChordNew = chordNew;
            ChordAgeFrames = chordAgeFrames;
            QualityFlags = qualityFlags;
            CandidatePitches = candidatePitches;
            CandidateNotes = candidateNotes;
            RootPitch = rootPitch;
            PitchTolerance = pitchTolerance;
            FrameId = frameId;
            DetectorLevelDb = detectorLevelDb;
            DetectorQuality = detectorQuality;
            ImmediateState = immediateState;
            NoteLevels = noteLevels;
            NoteLevelsFloat = noteLevelsFloat;
            Onset = onset;
            Peak = peak;
        }
    }

    public readonly struct MicBufferInfo
    {
        public readonly int BufferFrames;
        public readonly double BufferMilliseconds;
        public readonly int SampleRate;
        public readonly int Channels;
        public readonly bool IsAsio;
        public readonly int CushionMilliseconds;
        public readonly int WaitingBytes;
        public readonly long CapturePositionBytes;
        /// <summary>
        /// Frames waiting in the analysis split stream, or -1 when the source
        /// does not expose a split stream.  This is deliberately separate from
        /// <see cref="WaitingBytes"/>, which is the recording channel's
        /// backlog and can differ from the analysis stream's position.
        /// </summary>
        public readonly int AnalysisWaitingFrames;

        public MicBufferInfo(int bufferFrames, double bufferMilliseconds, int sampleRate, int channels, bool isAsio, int cushionMilliseconds, int waitingBytes, long capturePositionBytes = -1, int analysisWaitingFrames = -1)
        {
            BufferFrames = bufferFrames;
            BufferMilliseconds = bufferMilliseconds;
            SampleRate = sampleRate;
            Channels = channels;
            IsAsio = isAsio;
            CushionMilliseconds = cushionMilliseconds;
            WaitingBytes = waitingBytes;
            CapturePositionBytes = capturePositionBytes;
            AnalysisWaitingFrames = analysisWaitingFrames;
        }
    }

    public abstract class MicDevice : IDisposable
    {
        public const int RECORD_PERIOD_MS = 40;
        public const float UPDATES_PER_SECOND = 1000f / RECORD_PERIOD_MS;

        private bool _disposed;

        public readonly string DisplayName;
        public bool IsMonitoring;
        public bool IsRecordingOutput;

        protected MicDevice(string displayName)
        {
            DisplayName = displayName;
        }

        /// <summary>
        /// Resets the microphone streams and clears all buffers.
        /// </summary>
        /// <returns>0 if successful, otherwise an error code.</returns>
        public abstract int Reset();

        /// <summary>
        /// Enables or disables polyphonic note-mask analysis for this microphone.
        /// </summary>
        public virtual void SetChordAnalysisEnabled(bool enabled)
        {
        }

        /// <summary>
        /// Requests the lowest capture buffer this backend can provide.  Real
        /// Guitar enables this while its chord detector is active; backends
        /// with a fixed or driver-controlled buffer ignore the request.
        /// </summary>
        public virtual void SetLowLatencyInput(bool enabled)
        {
        }

        /// <summary>
        /// Sets the lowest MIDI note accepted as a polyphonic root candidate.
        /// Rocksmith derives this from the lowest open string (+0x11f8), so
        /// alternate tunings must lower it with the rest of the detector.
        /// </summary>
        public virtual void SetRootLowMidi(int rootLowMidi)
        {
        }

        /// <summary>
        /// Whether this microphone supplies its own real-time onset edges
        /// (rather than relying on amplitude/pitch-change fallbacks).
        /// </summary>
        public virtual bool SupportsRealtimeOnsets => false;

        /// <summary>
        /// Enables or disables live monitoring for this microphone.  Monitoring
        /// is intentionally silent until a gameplay player starts using it.
        /// </summary>
        public virtual void SetMonitoringEnabled(bool enabled)
        {
        }

        /// <summary>
        /// Dequeues an output frame from the microphone.
        /// </summary>
        /// <returns>
        /// Whether the dequeue was successful.
        /// </returns>
        public abstract bool DequeueOutputFrame(out MicOutputFrame frame);

        /// <summary>
        /// Clears the output queue.
        /// </summary>
        public abstract void ClearOutputQueue();

        /// <summary>
        /// Set the monitoring level of this Microphone Device.
        /// </summary>
        /// <param name="volume">The volume to set to.</param>
        public abstract void SetMonitoringLevel(float volume);

        public abstract void SetReverbLevel(float wet);

        public abstract MicBufferInfo? GetBufferInfo();

        /// <summary>
        /// Converts this microphone into its serialized form.
        /// </summary>
        public abstract SerializedMic Serialize();

        protected virtual void DisposeManagedResources() { }
        protected virtual void DisposeUnmanagedResources() { }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    DisposeManagedResources();
                }
                DisposeUnmanagedResources();
                _disposed = true;
            }
        }

        ~MicDevice()
        {
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
