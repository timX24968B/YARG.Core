using System;
using System.Collections.Generic;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;
using YARG.Core.Input;
using YARG.Core.Logging;

namespace YARG.Core.Engine.RealGuitar
{
    /// <summary>
    /// Scores Pro Guitar charts from pitch and onset data supplied by a
    /// microphone or USB guitar interface.
    ///
    /// Chord input uses a chart-aware note mask when the analyzer supplies one.
    /// Attack checks are strict: every unique chart pitch class, including the
    /// fifth, must be present. Sustains use a looser matcher because a chord
    /// voice may decay out of the FFT mask while the chord is held. Without a
    /// mask, the engine falls back to single-pitch matching for compatibility
    /// with older microphone backends.
    /// </summary>
    public class RealGuitarEngine : BaseEngine<ProGuitarNote, GuitarEngineParameters, GuitarStats>
    {
        public const int STRING_COUNT = 6;
        private const int PITCH_SCALE = 10;
        private const float PITCH_TOLERANCE = 0.75f;
        private const double PITCH_HOLD_SECONDS = 0.25;
        private const double ATTACK_LENIENCY_SECONDS = 0.20;
        // The first pitch frame after an audio onset can arrive well after
        // 90 ms (especially on low strings and during pitch-tracker settling).
        // Keep the attack pending long enough for that corrected pitch frame;
        // otherwise the onset note (often the wrong harmonic/root) ghosts the
        // note before the real pitch arrives.
        private const double ATTACK_SETTLE_SECONDS = 0.20;
        // A low E/A fundamental takes longer to resolve than a high string.
        // Rocksmith's chord query scans detector frames back to the caller's
        // time bound and falls back to 0.2 s when the bound is negative.
        // Keep a chord attack alive for a similar span so a newly arriving
        // low root is not ghosted before the detector can publish it.
        private const double CHORD_ATTACK_SETTLE_SECONDS = 0.18;
        private const double CHORD_MASK_MAX_AGE_SECONDS = 0.20;
        // YARG Layer 2 extension: after a successful hit, ignore detector
        // attack edges from the immediately following frames so a
        // re-trigger/ringing edge cannot satisfy the next chart note early.
        // Rocksmith has no equivalent consumption or refractory; its
        // FUN_014a4f20 returns the newest +0x7b5 frame every call.
        private const double ATTACK_REFRACTORY_SECONDS = 0.03;
        // REA: FUN_014a706d scans detector frames backward to the caller's
        // time bound, and FUN_014a6f01 defaults that bound to 0.2 s when the
        // caller passes a negative value.  A chord mask therefore does not
        // have to be newer than the attack.  The previous 30 ms gate was a
        // YARG invention and rejected valid ringing masks.
        private const double CHORD_MASK_ATTACK_TOLERANCE_SECONDS = 0.20;
        // FUN_017b049a passes chart note time plus -0.5 and +0.5 to the
        // general note/chord queries.  Use those exact source bounds for the
        // detector-frame scan.
        private const double ROCKSMITH_QUERY_BOUND_SECONDS = 0.5;
        // Live FUN_017b049a evidence: the chart note object supplies
        // +0x8 = -0.2 and +0x0c = +0.3, so the general note and chord
        // queries use [note.Time - 0.2, note.Time + 0.3].  The old blanket
        // 0.5 s window let a strum from up to half a second earlier satisfy
        // a later chord whose notes were still ringing.
        private const double ROCKSMITH_QUERY_FRONT_SECONDS = 0.2;
        private const double ROCKSMITH_QUERY_BACK_SECONDS = 0.3;
        // Rocksmith assumes roughly 50 ms of input latency: a note played on
        // the chart time reaches the detector about 50 ms later.  The
        // judgement clock is time + offset, so the default -50 ms evaluates
        // that late input at the chart time and compensates the assumed
        // latency exactly.  Positive offsets judge ahead of song time.
        public const double REAL_GUITAR_JUDGEMENT_OFFSET_SECONDS = -0.050;

        /// <summary>
        /// Layer 2 of the multi-chord strum fix.  When enabled, the changed
        /// frame that satisfies a successful note/chord hit is consumed, so a
        /// later chart chord must be matched by a newer attack edge.  This
        /// enforces one strum per chart chord for chords closer than the
        /// source 0.2 s pre-window; Rocksmith itself reuses the changed frame
        /// across those chords.  Disable to fall back to source-only window
        /// behavior.  Kept as a runtime-toggleable setting for future
        /// features; the default preserves the current fix.
        /// </summary>
        public static bool EnableAttackEdgeConsumption { get; set; } = true;

        /// <summary>
        /// Standard-tuning open-string MIDI notes, ordered from low E to high E.
        /// </summary>
        public static readonly int[] StandardTuning = { 40, 45, 50, 55, 59, 64 };

        private static readonly (string Name, int[] Notes)[] KnownTunings =
        {
            ("E Standard",      StandardTuning),
            ("E Flat Standard", new[] { 39, 44, 49, 54, 58, 63 }),
            ("D Standard",      new[] { 38, 43, 48, 53, 57, 62 }),
            ("C# Standard",     new[] { 37, 42, 47, 52, 56, 61 }),
            ("C Standard",      new[] { 36, 41, 46, 51, 55, 60 }),
            ("B Standard",      new[] { 35, 40, 45, 50, 54, 59 }),
            ("B Flat Standard", new[] { 34, 39, 44, 49, 53, 58 }),
            ("Drop D",          new[] { 38, 45, 50, 55, 59, 64 }),
            ("Drop C#",         new[] { 37, 44, 49, 54, 58, 63 }),
            ("Drop C",          new[] { 36, 43, 48, 53, 57, 62 }),
            ("Drop B",          new[] { 35, 42, 47, 52, 56, 61 }),
            ("Drop B Flat",     new[] { 34, 41, 46, 51, 55, 60 }),
            ("Drop A",      new[] { 33, 40, 45, 50, 54, 59 }),
            ("Open D",      new[] { 38, 45, 50, 54, 57, 62 }),
            ("Open E",      new[] { 40, 47, 52, 56, 59, 64 }),
            ("Open G",      new[] { 38, 43, 50, 55, 59, 62 }),
            ("Open C",      new[] { 36, 43, 48, 52, 55, 60 }),
            ("DADGAD",      new[] { 38, 45, 50, 55, 57, 62 }),
        };

        private readonly int[] _openStringMidi;
        private readonly int _tuningOffsetCents;

        public IReadOnlyList<int> OpenStringMidi => _openStringMidi;
        public int TuningOffsetCents => _tuningOffsetCents;

        public float CurrentPitch { get; private set; } = float.NaN;
        public bool HasPitch => !float.IsNaN(CurrentPitch);
        public bool HasActiveSustains => ActiveSustains.Count > 0;
        public double LastChordCoverage { get; private set; }
        public double LastChordMatchedLevel { get; private set; }
        public double LastChordUnmatchedLevel { get; private set; }
        public double LastChordVoicingCoverage { get; private set; }
        public bool LastChordChanged => _chordChanged;
        public bool LastChordNew => _chordNew;
        public int LastChordAgeFrames => _chordAgeFrames;
        public int LastChordRoot => _chordRootNote;
        public bool LastInputClipped => _inputClipped;

        private float _attackPitch = float.NaN;
        private bool _hasAttack;
        private double _attackTime = double.NegativeInfinity;
        private double _lastPitchInputTime = double.NegativeInfinity;
        private double _hitCheckTime = double.NegativeInfinity;
        private double _judgementTime = double.NegativeInfinity;
        private readonly double _judgementOffsetSeconds;
        private int _lastConsumedAttackFrameId = -1;
        private double _lastConsumedAttackTime = double.NegativeInfinity;
        private double _lastHitTime = double.NegativeInfinity;

        private uint _chordNotes0;
        private uint _chordNotes1;
        private uint _chordNotes2;
        private uint _chordNotes3;
        private uint _strongNotes0;
        private uint _strongNotes1;
        private uint _strongNotes2;
        private uint _strongNotes3;
        private ulong _candidateMidiPacked;
        private ulong _candidateLevelPacked;
        private ulong _candidateMidiPacked2;
        private ulong _candidateLevelPacked2;
        private int _candidateCount;
        private readonly int[] _frameCandidateNotes = new int[20];
        private readonly float[] _frameCandidatePitches = new float[20];
        private double _frameCandidateInfoTime = double.NegativeInfinity;
        private float _frameRootPitch;
        private double _frameRootPitchTime = double.NegativeInfinity;
        private float _framePitchTolerance = 0.05f;
        private double _framePitchToleranceTime = double.NegativeInfinity;
        private int _chordRootNote;
        private bool _chordChanged;
        private bool _chordNew;
        private int _chordAgeFrames;
        private int _frameFrameId;
        private float _frameDetectorLevelDb;
        private float _frameDetectorQuality = 100f;
        private bool _frameImmediateState;
        private double _frameFrameIdTime = double.NegativeInfinity;
        private double _frameDetectorLevelTime = double.NegativeInfinity;
        private double _frameDetectorQualityTime = double.NegativeInfinity;
        private double _frameImmediateStateTime = double.NegativeInfinity;
        private int _localFrameId;
        private bool _hasReceivedChordState;
        private bool _hasReceivedChordMask;
        private bool _hasReceivedStrongMask;
        private bool _hasReceivedFrameLevels;
        private bool _hasEverReceivedFrameLevels;
        private bool _frameOnset;
        private bool _hasReceivedOnsetTransport;
        private double _frameOnsetTime = double.NegativeInfinity;
        private float _framePeak;
        private double _framePeakTime = double.NegativeInfinity;
        private float _frameLevelDb;
        private double _frameLevelTime = double.NegativeInfinity;
        private double _chordChangedTime = double.NegativeInfinity;
        private double _chordNewTime = double.NegativeInfinity;
        private ChordQualityFlags _frameQualityFlags;
        private double _frameQualityTime = double.NegativeInfinity;
        private ChordQualityFlags _chordMaskQualityFlags;
        private double _lastNoteQueryFrameTime = double.NegativeInfinity;
        private double _lastChordQueryFrameTime = double.NegativeInfinity;
        private long _chordTimeOffsetBits;
        private double _frameChordTimeOffset;
        private double _latestChordTimeOffset;
        private double _chordTimeOffsetTime = double.NegativeInfinity;
        private double _frameLevelsTime = double.NegativeInfinity;
        private readonly byte[] _frameLevels = new byte[128];
        private readonly float[] _frameFloatLevels = new float[128];
        private readonly float[] _byteLevelsAsFloat = new float[128];
        private double _frameFloatLevelsTime = double.NegativeInfinity;
        private bool _hasReceivedFloatLevels;
        private static readonly float[] ZeroFloatLevels = new float[128];
        private readonly ChordFrameHistory _chordHistory = new();
        private bool _inputClipped;
        private double _strongAttackTime = double.NegativeInfinity;
        private bool _hasPitchChange;
        private float _pitchChangePitch = float.NaN;
        private double _pitchChangeTime = double.NegativeInfinity;
        private readonly bool[] _previousChordPitchClasses = new bool[12];
        private uint _heldChordNotes0;
        private uint _heldChordNotes1;
        private uint _heldChordNotes2;
        private uint _heldChordNotes3;
        private double _heldChordTime = double.NegativeInfinity;
        private double _chordMaskTime = double.NegativeInfinity;
        private double _chordMaskChordTime = double.NegativeInfinity;

        public RealGuitarEngine(InstrumentDifficulty<ProGuitarNote> chart, SyncTrack syncTrack,
            GuitarEngineParameters engineParameters, bool isBot, int[]? openStringMidi = null,
            int tuningOffsetCents = 0, double judgementOffsetSeconds = 0)
            : base(chart, syncTrack, engineParameters, isChordSeparate: true, isBot)
        {
            _openStringMidi = NormalizeTuning(openStringMidi);
            _tuningOffsetCents = tuningOffsetCents;
            _judgementOffsetSeconds = Math.Clamp(judgementOffsetSeconds, -0.2, 0.2);
            GetWaitCountdowns(Notes);
        }

        /// <summary>
        /// Accepts either absolute open-string MIDI notes or per-string offsets
        /// from standard tuning. Rocksmith DTA files use offsets, while other
        /// chart metadata may store absolute notes; both are supported.
        /// </summary>
        public static string? GetTuningName(int[]? tuning)
        {
            if (tuning == null || tuning.Length != STRING_COUNT)
            {
                return null;
            }

            var normalized = NormalizeTuning(tuning);
            foreach (var known in KnownTunings)
            {
                if (normalized.AsSpan().SequenceEqual(known.Notes))
                {
                    return known.Name;
                }
            }

            return null;
        }

        public static int[] NormalizeTuning(int[]? tuning)
        {
            if (tuning == null || tuning.Length != STRING_COUNT)
            {
                return (int[]) StandardTuning.Clone();
            }

            bool isOffset = true;
            for (int i = 0; i < tuning.Length; i++)
            {
                if (tuning[i] < -24 || tuning[i] > 24)
                {
                    isOffset = false;
                    break;
                }
            }

            var result = new int[STRING_COUNT];
            for (int i = 0; i < STRING_COUNT; i++)
            {
                int value = isOffset ? StandardTuning[i] + tuning[i] : tuning[i];
                if (value < 0 || value > 127)
                {
                    return (int[]) StandardTuning.Clone();
                }

                result[i] = value;
            }

            return result;
        }

        public static int GetMidiNote(int stringIndex, int fret)
        {
            if (stringIndex < 0 || stringIndex >= STRING_COUNT)
            {
                return -1;
            }

            return StandardTuning[stringIndex] + Math.Max(0, fret);
        }

        public static int GetMidiNote(ProGuitarNote note)
        {
            return GetMidiNote(note.String, note.Fret);
        }

        public float GetTargetPitch(int stringIndex, int fret)
        {
            if (stringIndex < 0 || stringIndex >= STRING_COUNT)
            {
                return float.NaN;
            }

            return _openStringMidi[stringIndex] + Math.Max(0, fret) +
                (_tuningOffsetCents / 100f);
        }

        public float GetTargetPitch(ProGuitarNote note)
        {
            return GetTargetPitch(note.String, note.Fret);
        }

        // Normal Pro Guitar targets map the detector's fractional pitch to
        // the nearest semitone.  Harmonic notes use their own source target
        // mapping in GetHarmonicTargetMidi; bend/slide/tremolo pitch shaping
        // remains a separate technique path.  AwayFromZero avoids banker's
        // rounding at exact half-semitone tuning offsets.
        private int GetTargetMidi(ProGuitarNote note)
        {
            float pitch = GetTargetPitch(note);
            return float.IsNaN(pitch)
                ? -1
                : (int) MathF.Round(pitch, MidpointRounding.AwayFromZero);
        }

        public bool MatchesPitch(float midiPitch, ProGuitarNote note)
        {
            return !float.IsNaN(midiPitch) &&
                Math.Abs(midiPitch - GetTargetPitch(note)) <= PITCH_TOLERANCE;
        }

        public static bool IsPitchMatch(float midiPitch, ProGuitarNote note)
        {
            return !float.IsNaN(midiPitch) &&
                Math.Abs(midiPitch - GetMidiNote(note)) <= PITCH_TOLERANCE;
        }

        private static float DecodePitch(int value)
        {
            return value <= 0 ? float.NaN : value / (float) PITCH_SCALE;
        }

        protected override bool BatchSameTimestampInputs => true;

        public override void Reset(bool keepCurrentButtons = false)
        {
            CurrentPitch = float.NaN;
            _attackPitch = float.NaN;
            _hasAttack = false;
            _attackTime = double.NegativeInfinity;
            _lastPitchInputTime = double.NegativeInfinity;
            _hitCheckTime = double.NegativeInfinity;
            _judgementTime = double.NegativeInfinity;
            _lastConsumedAttackFrameId = -1;
            _lastConsumedAttackTime = double.NegativeInfinity;
            _lastHitTime = double.NegativeInfinity;

            _chordNotes0 = 0;
            _chordNotes1 = 0;
            _chordNotes2 = 0;
            _chordNotes3 = 0;
            _strongNotes0 = 0;
            _strongNotes1 = 0;
            _strongNotes2 = 0;
            _strongNotes3 = 0;
            _candidateMidiPacked = 0;
            _candidateLevelPacked = 0;
            _candidateMidiPacked2 = 0;
            _candidateLevelPacked2 = 0;
            _candidateCount = 0;
            Array.Clear(_frameCandidateNotes, 0, _frameCandidateNotes.Length);
            Array.Clear(_frameCandidatePitches, 0, _frameCandidatePitches.Length);
            _frameCandidateInfoTime = double.NegativeInfinity;
            _frameRootPitch = 0f;
            _frameRootPitchTime = double.NegativeInfinity;
            _framePitchTolerance = 0.05f;
            _framePitchToleranceTime = double.NegativeInfinity;
            _chordRootNote = 0;
            _chordChanged = false;
            _chordNew = false;
            _chordAgeFrames = 0;
            _frameFrameId = 0;
            _frameDetectorLevelDb = 0f;
            _frameDetectorQuality = 100f;
            _frameImmediateState = false;
            _frameFrameIdTime = double.NegativeInfinity;
            _frameDetectorLevelTime = double.NegativeInfinity;
            _frameDetectorQualityTime = double.NegativeInfinity;
            _frameImmediateStateTime = double.NegativeInfinity;
            _localFrameId = 0;
            _hasReceivedChordState = false;
            _hasReceivedChordMask = false;
            _hasReceivedStrongMask = false;
            _hasReceivedFrameLevels = false;
            _hasEverReceivedFrameLevels = false;
            _frameOnset = false;
            _hasReceivedOnsetTransport = false;
            _frameOnsetTime = double.NegativeInfinity;
            _framePeak = 0f;
            _framePeakTime = double.NegativeInfinity;
            _frameLevelDb = 0f;
            _frameLevelTime = double.NegativeInfinity;
            _chordChangedTime = double.NegativeInfinity;
            _chordNewTime = double.NegativeInfinity;
            _frameQualityFlags = ChordQualityFlags.None;
            _frameQualityTime = double.NegativeInfinity;
            _chordMaskQualityFlags = ChordQualityFlags.None;
            _lastNoteQueryFrameTime = double.NegativeInfinity;
            _lastChordQueryFrameTime = double.NegativeInfinity;
            _chordTimeOffsetBits = 0;
            _frameChordTimeOffset = 0;
            _latestChordTimeOffset = 0;
            _chordTimeOffsetTime = double.NegativeInfinity;
            _frameLevelsTime = double.NegativeInfinity;
            Array.Clear(_frameLevels, 0, _frameLevels.Length);
            _frameFloatLevelsTime = double.NegativeInfinity;
            _hasReceivedFloatLevels = false;
            Array.Clear(_frameFloatLevels, 0, _frameFloatLevels.Length);
            _chordHistory.Clear();
            _inputClipped = false;
            _strongAttackTime = double.NegativeInfinity;
            _hasPitchChange = false;
            _pitchChangePitch = float.NaN;
            _pitchChangeTime = double.NegativeInfinity;
            Array.Clear(_previousChordPitchClasses, 0, _previousChordPitchClasses.Length);
            _heldChordNotes0 = 0;
            _heldChordNotes1 = 0;
            _heldChordNotes2 = 0;
            _heldChordNotes3 = 0;
            _heldChordTime = double.NegativeInfinity;
            _chordMaskTime = double.NegativeInfinity;
            _chordMaskChordTime = double.NegativeInfinity;

            ActiveSustains.Clear();
            base.Reset(keepCurrentButtons);
        }

        protected override void MutateStateWithInput(GameInput gameInput)
        {
            int rawAction = (int) gameInput.GetAction<RealGuitarAction>();
            int floatLevelBase = (int) RealGuitarAction.ChordFloatLevels;
            if (rawAction >= floatLevelBase && rawAction < floatLevelBase + 128)
            {
                _frameFloatLevels[rawAction - floatLevelBase] =
                    BitConverter.Int32BitsToSingle(gameInput.Integer);
                _frameFloatLevelsTime = gameInput.Time;
                _hasReceivedFloatLevels = true;
                return;
            }

            if (rawAction >= (int) RealGuitarAction.ChordLevels0 &&
                rawAction <= (int) RealGuitarAction.ChordLevels31)
            {
                int group = rawAction - (int) RealGuitarAction.ChordLevels0;
                int offset = group * 4;
                uint packed = (uint) gameInput.Integer;
                _frameLevels[offset] = (byte) packed;
                _frameLevels[offset + 1] = (byte) (packed >> 8);
                _frameLevels[offset + 2] = (byte) (packed >> 16);
                _frameLevels[offset + 3] = (byte) (packed >> 24);
                _frameLevelsTime = gameInput.Time;
                return;
            }

            if (rawAction == (int) RealGuitarAction.ChordLevelsValid)
            {
                _hasReceivedFrameLevels = gameInput.Integer != 0;
                if (_hasReceivedFrameLevels)
                {
                    _hasEverReceivedFrameLevels = true;
                }
                _frameLevelsTime = gameInput.Time;
                if (!_hasReceivedFrameLevels)
                {
                    Array.Clear(_frameLevels, 0, _frameLevels.Length);
                }

                return;
            }

            if (rawAction >= (int) RealGuitarAction.ChordCandidatePitch0 &&
                rawAction <= (int) RealGuitarAction.ChordCandidatePitch19)
            {
                int index = rawAction - (int) RealGuitarAction.ChordCandidatePitch0;
                _frameCandidatePitches[index] =
                    BitConverter.Int32BitsToSingle(gameInput.Integer);
                _frameCandidateInfoTime = gameInput.Time;
                return;
            }

            if (rawAction >= (int) RealGuitarAction.ChordCandidateNotes0 &&
                rawAction <= (int) RealGuitarAction.ChordCandidateNotes4)
            {
                int group = rawAction - (int) RealGuitarAction.ChordCandidateNotes0;
                uint packed = (uint) gameInput.Integer;
                for (int j = 0; j < 4; j++)
                {
                    _frameCandidateNotes[group * 4 + j] =
                        (int) ((packed >> (j * 8)) & 0xFF);
                }

                _frameCandidateInfoTime = gameInput.Time;
                return;
            }

            switch ((RealGuitarAction) rawAction)
            {
                case RealGuitarAction.StarPower:
                    IsStarPowerInputActive = gameInput.Button;
                    break;
                case RealGuitarAction.Pitch:
                    CurrentPitch = DecodePitch(gameInput.Integer);
                    _lastPitchInputTime = gameInput.Time;
                    break;
                case RealGuitarAction.ChordNotes0:
                    if (_chordMaskTime != gameInput.Time)
                    {
                        RecordChordFrame();
                    }
                    _chordNotes0 = (uint) gameInput.Integer;
                    _hasReceivedChordMask = true;
                    SetChordMaskTime(gameInput.Time);
                    break;
                case RealGuitarAction.ChordNotes1:
                    _chordNotes1 = (uint) gameInput.Integer;
                    _hasReceivedChordMask = true;
                    SetChordMaskTime(gameInput.Time);
                    break;
                case RealGuitarAction.ChordNotes2:
                    _chordNotes2 = (uint) gameInput.Integer;
                    _hasReceivedChordMask = true;
                    SetChordMaskTime(gameInput.Time);
                    break;
                case RealGuitarAction.ChordNotes3:
                    _chordNotes3 = (uint) gameInput.Integer;
                    _hasReceivedChordMask = true;
                    SetChordMaskTime(gameInput.Time);
                    break;
                case RealGuitarAction.StrongChordNotes0:
                    _strongNotes0 = (uint) gameInput.Integer;
                    _hasReceivedStrongMask = true;
                    break;
                case RealGuitarAction.StrongChordNotes1:
                    _strongNotes1 = (uint) gameInput.Integer;
                    _hasReceivedStrongMask = true;
                    break;
                case RealGuitarAction.StrongChordNotes2:
                    _strongNotes2 = (uint) gameInput.Integer;
                    _hasReceivedStrongMask = true;
                    break;
                case RealGuitarAction.StrongChordNotes3:
                    _strongNotes3 = (uint) gameInput.Integer;
                    _hasReceivedStrongMask = true;
                    break;
                case RealGuitarAction.ChordOnset:
                    _frameOnset = gameInput.Integer != 0;
                    _frameOnsetTime = gameInput.Time;
                    _hasReceivedOnsetTransport = true;
                    break;
                case RealGuitarAction.ChordPeak:
                    _framePeak = Math.Max(0f, gameInput.Integer / 1000000f);
                    _framePeakTime = gameInput.Time;
                    break;
                case RealGuitarAction.ChordLevel:
                    _frameLevelDb = gameInput.Integer / 100f;
                    _frameLevelTime = gameInput.Time;
                    break;
                case RealGuitarAction.ChordCandidatesMidi0:
                    _candidateMidiPacked = (_candidateMidiPacked & 0xFFFFFFFF00000000UL) |
                        (uint) gameInput.Integer;
                    break;
                case RealGuitarAction.ChordCandidatesMidi1:
                    _candidateMidiPacked = (_candidateMidiPacked & 0x00000000FFFFFFFFUL) |
                        ((ulong) (uint) gameInput.Integer << 32);
                    break;
                case RealGuitarAction.ChordCandidatesLevel0:
                    _candidateLevelPacked = (_candidateLevelPacked & 0xFFFFFFFF00000000UL) |
                        (uint) gameInput.Integer;
                    break;
                case RealGuitarAction.ChordCandidatesLevel1:
                    _candidateLevelPacked = (_candidateLevelPacked & 0x00000000FFFFFFFFUL) |
                        ((ulong) (uint) gameInput.Integer << 32);
                    break;
                case RealGuitarAction.ChordCandidatesMidi2:
                    _candidateMidiPacked2 = (_candidateMidiPacked2 & 0xFFFFFFFF00000000UL) |
                        (uint) gameInput.Integer;
                    break;
                case RealGuitarAction.ChordCandidatesMidi3:
                    _candidateMidiPacked2 = (_candidateMidiPacked2 & 0x00000000FFFFFFFFUL) |
                        ((ulong) (uint) gameInput.Integer << 32);
                    break;
                case RealGuitarAction.ChordCandidatesLevel2:
                    _candidateLevelPacked2 = (_candidateLevelPacked2 & 0xFFFFFFFF00000000UL) |
                        (uint) gameInput.Integer;
                    break;
                case RealGuitarAction.ChordCandidatesLevel3:
                    _candidateLevelPacked2 = (_candidateLevelPacked2 & 0x00000000FFFFFFFFUL) |
                        ((ulong) (uint) gameInput.Integer << 32);
                    break;
                case RealGuitarAction.ChordCandidateCount:
                    _candidateCount = Math.Clamp(gameInput.Integer, 0, 20);
                    _frameCandidateInfoTime = gameInput.Time;
                    break;
                case RealGuitarAction.ChordRoot:
                    _chordRootNote = Math.Clamp(gameInput.Integer, 0, 127);
                    break;
                case RealGuitarAction.ChordTimeOffsetLow:
                    _chordTimeOffsetBits =
                        (_chordTimeOffsetBits & unchecked((long) 0xFFFFFFFF00000000UL)) |
                        (uint) gameInput.Integer;
                    break;
                case RealGuitarAction.ChordTimeOffsetHigh:
                    _chordTimeOffsetBits =
                        (_chordTimeOffsetBits & 0x00000000FFFFFFFFL) |
                        ((long) (uint) gameInput.Integer << 32);
                    _frameChordTimeOffset =
                        BitConverter.Int64BitsToDouble(_chordTimeOffsetBits);
                    _latestChordTimeOffset = _frameChordTimeOffset;
                    _chordTimeOffsetTime = gameInput.Time;
                    if (Math.Abs(_chordMaskTime - gameInput.Time) <= 1e-6)
                    {
                        _chordMaskChordTime = _chordMaskTime + _frameChordTimeOffset;
                    }
                    break;
                case RealGuitarAction.RootPitch:
                    _frameRootPitch = BitConverter.Int32BitsToSingle(gameInput.Integer);
                    _frameRootPitchTime = gameInput.Time;
                    break;
                case RealGuitarAction.PitchTolerance:
                    _framePitchTolerance = BitConverter.Int32BitsToSingle(gameInput.Integer);
                    _framePitchToleranceTime = gameInput.Time;
                    break;
                case RealGuitarAction.ChordQuality:
                    _frameQualityFlags = (ChordQualityFlags) (byte) gameInput.Integer;
                    _frameQualityTime = gameInput.Time;
                    if (Math.Abs(_chordMaskTime - gameInput.Time) <= 1e-6)
                    {
                        _chordMaskQualityFlags = _frameQualityFlags;
                    }
                    break;
                case RealGuitarAction.ChordState:
                {
                    int state = gameInput.Integer;
                    _chordChanged = (state & 1) != 0;
                    _chordNew = (state & 2) != 0;
                    _chordAgeFrames = Math.Max(0, state >> 2);
                    _chordChangedTime = gameInput.Time;
                    _chordNewTime = gameInput.Time;
                    _hasReceivedChordState = true;
                    break;
                }
                case RealGuitarAction.ChordFrameId:
                    _frameFrameId = gameInput.Integer;
                    _frameFrameIdTime = gameInput.Time;
                    break;
                case RealGuitarAction.ChordDetectorLevel:
                    _frameDetectorLevelDb = gameInput.Integer / 100f;
                    _frameDetectorLevelTime = gameInput.Time;
                    break;
                case RealGuitarAction.ChordDetectorQuality:
                    _frameDetectorQuality = gameInput.Integer / 100f;
                    _frameDetectorQualityTime = gameInput.Time;
                    break;
                case RealGuitarAction.ChordImmediateState:
                    _frameImmediateState = gameInput.Integer != 0;
                    _frameImmediateStateTime = gameInput.Time;
                    break;
                case RealGuitarAction.Clipped:
                    _inputClipped = gameInput.Integer != 0;
                    break;
                case RealGuitarAction.StrongAttack:
                    _strongAttackTime = gameInput.Integer != 0
                        ? gameInput.Time
                        : double.NegativeInfinity;
                    break;
                case RealGuitarAction.PitchChange:
                    _pitchChangePitch = DecodePitch(gameInput.Integer);
                    _pitchChangeTime = gameInput.Time;
                    _hasPitchChange = !float.IsNaN(_pitchChangePitch);
                    break;
                case RealGuitarAction.Attack:
                {
                    float pitch = DecodePitch(gameInput.Integer);
                    if (float.IsNaN(pitch))
                    {
                        break;
                    }

                    CurrentPitch = pitch;
                    _attackPitch = pitch;
                    _hasAttack = true;
                    _attackTime = gameInput.Time;
                    _lastPitchInputTime = gameInput.Time;
                    break;
                }
            }
        }

        protected override void UpdateHitLogic(double time)
        {
            _hitCheckTime = time;
            _judgementTime = time + _judgementOffsetSeconds;
            RecordChordFrame();
            UpdateBot(time);

            if (!IsBot && HasPitch && time - _lastPitchInputTime > PITCH_HOLD_SECONDS)
            {
                CurrentPitch = float.NaN;
            }

            if (NoteIndex >= Notes.Count)
            {
                _hasAttack = false;
                UpdateSustains();
                return;
            }

            if (_hasPitchChange && time - _pitchChangeTime > 0.15)
            {
                _hasPitchChange = false;
                _pitchChangePitch = float.NaN;
            }

            if (time - _strongAttackTime > ATTACK_LENIENCY_SECONDS)
            {
                _strongAttackTime = double.NegativeInfinity;
            }

            // Re-record the current frame after all same-time state actions
            // have been applied.  The detector transport sends the chord mask
            // first and ChordState later; without this refresh the previous
            // frame's changed/new flag can leak into the current frame.
            RecordChordFrame();

            CheckForNoteHit();
            UpdateSustains();

            // Keep an attack alive briefly so a pick that lands just before
            // the note's hit window is not thrown away.
            if (_hasAttack && time - _attackTime > ATTACK_LENIENCY_SECONDS)
            {
                _hasAttack = false;
                _attackPitch = float.NaN;
            }
        }

        protected override void CheckForNoteHit()
        {
            var parent = Notes[NoteIndex];
            bool inHitWindow = IsNoteInWindow(parent, out bool missed, _judgementTime);
            bool fullQuery = _hasEverReceivedFrameLevels && _hasReceivedOnsetTransport;
            // The detector query can scan a wide frame history, but it must
            // only be attempted for the current chart note inside YARG's
            // calibrated hit window.  Using the +/-0.5 s caller bounds here
            // consumed future notes up to half a second early.  The judgement
            // clock is shifted back by the Real Guitar detector offset so the
            // 4096-sample analysis delay does not read as a late hit.
            //
            // Do not add a note.Time gate here.  FUN_017b049a passes
            // [note.Time - 0.2, note.Time + 0.3] to the detector query and
            // FUN_014a5d4f/FUN_014a6f01 accept the note as soon as the
            // detector note clock is inside that window; the live trace
            // accepted a chord 144 ms before its chart time.  Requiring
            // _judgementTime >= note.Time replaced that 200 ms source front
            // window with a query that could not start until 50 ms after the
            // note (with the current -50 ms offset), which is both slower and
            // less source-faithful.  IsNoteInWindow already enforces the
            // calibrated front/back bounds.
            //
            // YARG Layer 2 extends the source by consuming the current
            // detector edge after a hit and ignoring the immediately
            // following re-trigger frames.  Rocksmith's FUN_014a4f20 has no
            // such consumption; this prevents a ringing/re-trigger edge from
            // satisfying the next chart note early.
            bool inRefractory = IsInAttackRefractory();
            ConsumeRefractoryEdges();

            bool inQueryWindow = fullQuery && inHitWindow && !inRefractory;

            if (fullQuery && inQueryWindow)
            {
                var chartNotes = new List<ProGuitarNote>();
                foreach (var chordNote in parent.ParentOrSelf.AllNotes)
                {
                    chartNotes.Add(chordNote);
                }

                bool queryHit;
                if (chartNotes.Count > 1)
                {
                    // FUN_017b049a checks NOTE_MASK_FRETHANDMUTE (0x8) and
                    // NOTE_MASK_IGNORE (0x40000) on the parent and every child
                    // before HOPO, harmonic, or normal chord dispatch.
                    bool immediateChord = false;
                    bool hopoChord = false;
                    bool harmonicChord = false;
                    foreach (var chordNote in chartNotes)
                    {
                        immediateChord |= chordNote.IsFretHandMute ||
                            chordNote.IsIgnored;
                        hopoChord |= chordNote.IsHopo || chordNote.IsTap;
                        harmonicChord |= chordNote.IsHarmonic;
                    }

                    if (immediateChord)
                    {
                        queryHit = TryQueryImmediateOnset();
                    }
                    else if (hopoChord)
                    {
                        queryHit = TryQueryHopoChordAttack(parent, chartNotes);
                    }
                    else if (harmonicChord)
                    {
                        queryHit = TryQueryHarmonicChord(chartNotes);
                    }
                    else
                    {
                        queryHit = TryQueryChordAttack(parent, chartNotes);
                    }
                }
                else if (parent.IsIgnored)
                {
                    // FUN_017b049a checks 0x40000 (ignore) before the
                    // ordinary mute branch and forwards to the onset query.
                    queryHit = TryQueryMuteNote(parent);
                }
                else if (parent.IsSlideUnpitched)
                {
                    // FUN_017b049a routes 0x400000 to FUN_014a9ddb, which
                    // uses the immediate detector state.
                    queryHit = TryQueryImmediateOnset();
                }
                else if (parent.IsMuted)
                {
                    // NOTE_MASK_MUTE (0x20000) -> FUN_014a61ba/FUN_014a4ff0.
                    // It is an onset-only query and does not use target pitch.
                    queryHit = TryQueryMuteNote(parent);
                }
                else if (parent.IsHarmonic)
                {
                    // NOTE_MASK_HARMONIC (0x20) -> FUN_014a9d7b/FUN_014a5889
                    // before palm mute, tap/HOPO, and arpeggio.
                    queryHit = TryQueryHarmonicNote(parent);
                }
                else if (parent.IsPalmMuted)
                {
                    // NOTE_MASK_PALMMUTE (0x40) takes the separate
                    // FUN_014a6459 six-decibel query.
                    queryHit = TryQueryPalmMuteNote(parent);
                }
                else if (parent.IsHopo || parent.IsTap)
                {
                    // FUN_017b049a routes tap/HOPO/pull-off masks to
                    // FUN_014a9bfe -> FUN_014a6bd9 before arpeggio.
                    queryHit = TryQueryHopoNote(parent);
                }
                else
                {
                    queryHit = parent.IsArpeggio
                        // FUN_017b049a tries the arpeggio root query only
                        // after the tap/HOPO/pull-off masks, then falls back
                        // to the normal note query.
                        ? TryQueryArpeggioNote(parent) || TryQueryNoteAttack(parent)
                        : TryQueryNoteAttack(parent);
                }
                if (queryHit)
                {
                    HitChord(parent);
                    _hasAttack = false;
                    _attackPitch = float.NaN;
                    _hasPitchChange = false;
                    _pitchChangePitch = float.NaN;
                    return;
                }
            }

            if (!inHitWindow)
            {
                if (missed)
                {
                    MissChord(parent);
                    _hasAttack = false;
                    _attackPitch = float.NaN;
                }

                return;
            }

            // Once the full detector transport is present, FUN_014a6bd9 owns
            // tap/HOPO/pull-off hit detection.  Do not fall through to the
            // legacy attack/pitch-change fallback; that fallback exists only
            // for backends that do not provide detector frames.
            if (fullQuery)
            {
                return;
            }

            bool hasAttack = _hasAttack;
            double attackTime = _attackTime;
            if (!hasAttack &&
                (parent.IsHopo || parent.IsTap) &&
                _hasPitchChange &&
                _hitCheckTime - _pitchChangeTime <= 0.15)
            {
                hasAttack = true;
                attackTime = _pitchChangeTime;
            }

            if (!hasAttack)
            {
                return;
            }

            if (CanNoteBeHit(parent))
            {
                HitChord(parent);
                _hasAttack = false;
                _attackPitch = float.NaN;
                _hasPitchChange = false;
                _pitchChangePitch = float.NaN;
                return;
            }

            // During the attack settle window, wait for the chord analysis or
            // a corrected pitch frame.  This is a YARG timing choice; it is
            // not derived from a recovered Rocksmith settle constant.
            double attackSettle = parent.ChildNotes.Count > 0
                ? CHORD_ATTACK_SETTLE_SECONDS
                : ATTACK_SETTLE_SECONDS;
            if (_hitCheckTime - attackTime < attackSettle)
            {
                return;
            }

            // A very hard strum also needs the full detector window before its
            // spectral balance can be trusted.
            if (!double.IsNegativeInfinity(_strongAttackTime) &&
                _hitCheckTime - _strongAttackTime < CHORD_ATTACK_SETTLE_SECONDS)
            {
                return;
            }

            // A clipped frame has no trustworthy spectral balance.  Wait for
            // a clean frame instead of treating the hard-strum transient as a
            // wrong note.  The attack remains alive for its normal leniency.
            if (_inputClipped)
            {
                return;
            }

            // Wrong pitch while a chart note is in its hit window. This is
            // intentionally lightweight: break combo, but leave the chart
            // note available for a later correct attack.
            EngineStats.GhostInputs++;
            ResetCombo();
            UpdateMultiplier();
            _hasAttack = false;
            _attackPitch = float.NaN;
            _hasPitchChange = false;
            _pitchChangePitch = float.NaN;
        }

        protected override bool CanNoteBeHit(ProGuitarNote note)
        {
            if (note.WasHit || note.WasMissed)
            {
                return false;
            }

            if (IsBot)
            {
                return true;
            }

            var chordNotes = new List<ProGuitarNote>();
            foreach (var chordNote in note.ParentOrSelf.AllNotes)
            {
                chordNotes.Add(chordNote);
            }

            // Rocksmith's chord query scans the detector's frame ring buffer;
            // it does not require the newest mask to be newer than the
            // attack.  Use the full 128-bin frames when the detector supplied
            // them.  This is the primary path for live input.
            double attackTime = _hasAttack ? _attackTime : _pitchChangeTime;
            double earliestFrameTime = attackTime - CHORD_MASK_ATTACK_TOLERANCE_SECONDS;
            if (chordNotes.Count > 1 && _hasEverReceivedFrameLevels)
            {
                return MatchesChordHistory(chordNotes, earliestFrameTime, strict: true);
            }

            // Legacy/back-end fallback for sources that do not send a full
            // frame histogram.  The packed-mask path remains only for those
            // sources; it is not used when full frames are available.
            bool chordMaskAvailable = HasFreshChordMask(_hitCheckTime) &&
                _chordMaskTime >= earliestFrameTime;

            if (chordMaskAvailable && chordNotes.Count > 1)
            {
                if (MatchesChartChord(chordNotes, strict: true))
                {
                    return true;
                }
            }

            if (chordNotes.Count > 1 && MatchesHeldChord(chordNotes))
            {
                return true;
            }

            // Once the polyphonic detector has supplied a chord mask, a
            // missing mask means silence or an unresolved transient, not an
            // invitation to accept a full chord from one monophonic pitch.
            // The single-pitch fallback remains only for legacy microphone
            // backends that never produce a chord mask.
            if (chordNotes.Count > 1 && _hasReceivedChordMask)
            {
                return false;
            }

            float attackPitch = _hasAttack ? _attackPitch : _pitchChangePitch;
            bool currentPitchIsRecent = HasPitch &&
                _hitCheckTime - _lastPitchInputTime <= ATTACK_SETTLE_SECONDS;

            foreach (var chordNote in chordNotes)
            {
                if (MatchesPitch(attackPitch, chordNote) ||
                    (currentPitchIsRecent && MatchesPitch(CurrentPitch, chordNote)))
                {
                    return true;
                }
            }

            return false;
        }

        protected override bool CanSustainHold(ProGuitarNote note)
        {
            if (IsBot)
            {
                return true;
            }

            var chordNotes = new List<ProGuitarNote>();
            foreach (var chordNote in note.ParentOrSelf.AllNotes)
            {
                chordNotes.Add(chordNote);
            }

            if (chordNotes.Count > 1 && _hasEverReceivedFrameLevels &&
                TryGetLatestLevelFrame(out var latestFrame,
                    _hitCheckTime - CHORD_MASK_MAX_AGE_SECONDS))
            {
                // Weak quality means the root rise was only secondary; keep
                // the stricter full-chord sustain rule.  Strong/default frames
                // retain the source's one-missing-large-tone leniency.
                bool allowMissing =
                    (latestFrame.QualityFlags & ChordQualityFlags.Weak) == 0;
                return MatchRocksmithChartChord(chordNotes, latestFrame.Levels,
                    allowMissingLargeChordTone: allowMissing);
            }

            if (HasFreshChordMask(_hitCheckTime))
            {
                if (chordNotes.Count > 1 && MatchesChartChord(chordNotes, strict: false))
                {
                    return true;
                }

                if (chordNotes.Count == 1 &&
                    IsNoteInMask(GetTargetMidi(note)))
                {
                    return true;
                }
            }

            // Once the polyphonic detector has produced a chord mask, a
            // missing or non-matching mask is not enough to sustain a whole
            // chord from one monophonic pitch.  Keep that fallback only for
            // legacy microphone backends that never provide masks.
            if (chordNotes.Count > 1 && _hasReceivedChordMask)
            {
                return false;
            }

            // Fall back to the monophonic pitch while chord analysis is not
            // available (for example, older microphone backends).
            foreach (var chordNote in chordNotes)
            {
                if (MatchesPitch(CurrentPitch, chordNote))
                {
                    return true;
                }
            }

            return false;
        }

        private void SetChordMaskTime(double time)
        {
            _chordMaskTime = time;
            _chordMaskChordTime = Math.Abs(_chordTimeOffsetTime - time) <= 1e-6
                ? time + _frameChordTimeOffset
                : time;
            _chordMaskQualityFlags =
                Math.Abs(_frameQualityTime - time) <= 1e-6
                    ? _frameQualityFlags
                    : ChordQualityFlags.None;
        }

        private void RecordChordFrame()
        {
            if (_chordMaskTime == double.NegativeInfinity)
            {
                return;
            }

            // The chord clock belongs to the frame, not to whatever offset
            // arrived most recently.  Preserve it while a new frame's offset
            // and mask are being assembled.
            double chordTime = _chordMaskChordTime;
            ChordQualityFlags qualityFlags = _chordMaskQualityFlags;

            bool sameCandidateInfo =
                Math.Abs(_frameCandidateInfoTime - _chordMaskTime) <= 1e-6;
            int candidateCount = sameCandidateInfo ? _candidateCount : 0;
            int[]? candidateNotes = sameCandidateInfo ? _frameCandidateNotes : null;
            float[]? candidatePitches = sameCandidateInfo ? _frameCandidatePitches : null;
            float rootPitch = Math.Abs(_frameRootPitchTime - _chordMaskTime) <= 1e-6
                ? _frameRootPitch
                : 0f;
            float pitchTolerance =
                Math.Abs(_framePitchToleranceTime - _chordMaskTime) <= 1e-6
                    ? _framePitchTolerance
                    : 0.05f;

            bool hasFloatLevels = _hasReceivedFloatLevels &&
                Math.Abs(_frameFloatLevelsTime - _chordMaskTime) <= 1e-6;
            if (!hasFloatLevels && !_hasReceivedFrameLevels)
            {
                // A cleared mask is still a detector frame.  Store an
                // all-zero frame so an older histogram cannot remain the
                // newest match during silence.
                _chordHistory.Upsert(_chordMaskTime, chordTime, ZeroFloatLevels,
                    false, false, false, 0f, 0f, qualityFlags, 0, null, null, 0, 0f, 0.05f);
                return;
            }

            if (!hasFloatLevels &&
                Math.Abs(_frameLevelsTime - _chordMaskTime) > 1e-6)
            {
                return;
            }

            float[] levels;
            if (hasFloatLevels)
            {
                levels = _frameFloatLevels;
            }
            else
            {
                for (int note = 0; note < 128; note++)
                {
                    _byteLevelsAsFloat[note] = _frameLevels[note];
                }
                levels = _byteLevelsAsFloat;
            }

            bool sameOnset = Math.Abs(_frameOnsetTime - _chordMaskTime) <= 1e-6;
            bool sameChanged = Math.Abs(_chordChangedTime - _chordMaskTime) <= 1e-6;
            bool sameNew = Math.Abs(_chordNewTime - _chordMaskTime) <= 1e-6;
            bool samePeak = Math.Abs(_framePeakTime - _chordMaskTime) <= 1e-6;
            bool sameLevel = Math.Abs(_frameLevelTime - _chordMaskTime) <= 1e-6;
            bool sameFrameId = Math.Abs(_frameFrameIdTime - _chordMaskTime) <= 1e-6;
            bool sameDetectorLevel =
                Math.Abs(_frameDetectorLevelTime - _chordMaskTime) <= 1e-6;
            bool sameDetectorQuality =
                Math.Abs(_frameDetectorQualityTime - _chordMaskTime) <= 1e-6;
            bool sameImmediateState =
                Math.Abs(_frameImmediateStateTime - _chordMaskTime) <= 1e-6;

            int frameId = sameFrameId ? _frameFrameId : 0;
            if (frameId <= 0)
            {
                var previous = _chordHistory.Count > 0
                    ? _chordHistory.GetFromNewest(0)
                    : null;
                frameId = previous is not null &&
                    Math.Abs(previous.NoteTime - _chordMaskTime) <= 1e-6
                        ? previous.FrameId
                        : ++_localFrameId;
            }

            _chordHistory.Upsert(_chordMaskTime, chordTime, levels,
                sameOnset && _frameOnset,
                sameChanged && _chordChanged,
                sameNew && _chordNew,
                samePeak ? _framePeak : 0f,
                sameLevel ? _frameLevelDb : 0f,
                qualityFlags, candidateCount, candidateNotes, candidatePitches,
                _chordRootNote, rootPitch, pitchTolerance, frameId,
                sameDetectorLevel ? _frameDetectorLevelDb : 0f,
                sameDetectorQuality ? _frameDetectorQuality : 100f,
                sameImmediateState && _frameImmediateState);
        }

        private double GetQueryFrontEnd(ProGuitarNote note)
        {
            double hitWindow = EngineParameters.HitWindow.CalculateHitWindow(
                GetAverageNoteDistance(note));
            return EngineParameters.HitWindow.GetFrontEnd(hitWindow);
        }

        private ChordFrame? GetNewestChordFrame()
        {
            return _chordHistory.Count > 0
                ? _chordHistory.GetFromNewest(0)
                : null;
        }

        // FUN_014a4f20: the attack/chord-change gate runs on the +0x730 input
        // clock, not the +0x720 note clock used for note-level matching.  The
        // current input time is obtained from the newest detector frame when
        // one is available, or from the engine clock plus the frame offset as
        // a compatibility fallback.
        private bool HasChordAttackInInputWindow(double duration,
            bool allowOnset, double? chordNowOverride,
            out int attackFrameId, out double attackChordTime)
        {
            attackFrameId = 0;
            attackChordTime = double.NegativeInfinity;
            if (_chordHistory.Count == 0)
            {
                return false;
            }

            // FUN_014a4f20 runs on the detector input/chord clock.  When the
            // caller has a detector frame, use that frame's ChordTime rather
            // than the engine hit-check clock; this is the source query clock.
            double chordNow = chordNowOverride ??
                (_hitCheckTime + _latestChordTimeOffset);
            double earliest = chordNow - Math.Max(0, duration);

            for (int i = 0; i < _chordHistory.Count; i++)
            {
                var frame = _chordHistory.GetFromNewest(i);
                if (frame.ChordTime < earliest - 1e-6)
                {
                    break;
                }

                if (frame.ChordTime > chordNow + 1e-6)
                {
                    continue;
                }

                if (frame.ChordChanged || (allowOnset && frame.Onset))
                {
                    if (IsAttackEdgeConsumed(frame))
                    {
                        continue;
                    }

                    attackFrameId = frame.FrameId;
                    attackChordTime = frame.ChordTime;
                    return true;
                }
            }

            return false;
        }

        // Layer 2 (YARG rule): a strum is a discrete attack.  Rocksmith's
        // FUN_014a6f01 uses FUN_014a4f20's changed-frame ID only as a gate and
        // reuses it across close chart notes.  YARG consumes the edge after a
        // successful hit so a later chart chord must be satisfied by a newer
        // attack.  EnableAttackEdgeConsumption exposes this as a toggle so a
        // future feature can fall back to source-only behavior.
        private bool IsAttackEdgeConsumed(ChordFrame frame)
        {
            if (!EnableAttackEdgeConsumption)
            {
                return false;
            }

            if (_lastConsumedAttackFrameId > 0 && frame.FrameId > 0)
            {
                return frame.FrameId <= _lastConsumedAttackFrameId;
            }

            return frame.ChordTime <= _lastConsumedAttackTime + 1e-6;
        }

        private void ConsumeAttackEdge(int frameId, double chordTime)
        {
            if (!EnableAttackEdgeConsumption)
            {
                return;
            }

            if (frameId > 0)
            {
                _lastConsumedAttackFrameId =
                    Math.Max(_lastConsumedAttackFrameId, frameId);
            }

            if (double.IsFinite(chordTime))
            {
                _lastConsumedAttackTime =
                    Math.Max(_lastConsumedAttackTime, chordTime);
            }
        }

        /// <summary>
        /// Consumes every attack edge up to the newest detector frame.  This
        /// is the YARG Layer 2 rule: one successful hit consumes the current
        /// detector state so a ringing/re-trigger frame cannot satisfy the
        /// next chart note.
        /// </summary>
        private void ConsumeNewestAttackEdge(bool updateLastHitTime = true)
        {
            if (!EnableAttackEdgeConsumption)
            {
                return;
            }

            var newest = GetNewestChordFrame();
            if (updateLastHitTime)
            {
                _lastHitTime = newest?.NoteTime ?? _hitCheckTime;
            }

            if (newest is null)
            {
                return;
            }

            ConsumeAttackEdge(newest.FrameId, newest.ChordTime);
        }

        /// <summary>
        /// While the post-hit refractory is active, consume the newest frame
        /// if it falls inside the refractory window.  A frame that arrives
        /// after the window is left for the next note's own attack.
        /// </summary>
        private void ConsumeRefractoryEdges()
        {
            if (!EnableAttackEdgeConsumption ||
                double.IsNegativeInfinity(_lastHitTime))
            {
                return;
            }

            double cutoff = _lastHitTime + ATTACK_REFRACTORY_SECONDS;
            var newest = GetNewestChordFrame();
            if (newest is null)
            {
                return;
            }

            if (newest.NoteTime <= cutoff + 1e-6)
            {
                ConsumeAttackEdge(newest.FrameId, newest.ChordTime);
                return;
            }

            // A jump can leave older re-trigger frames inside the refractory
            // while the newest frame is already past it.  Consume the latest
            // frame at or before the cutoff so those edges cannot be reused.
            for (int i = 1; i < _chordHistory.Count; i++)
            {
                var frame = _chordHistory.GetFromNewest(i);
                if (frame.NoteTime <= cutoff + 1e-6)
                {
                    ConsumeAttackEdge(frame.FrameId, frame.ChordTime);
                    break;
                }
            }
        }

        private bool IsInAttackRefractory()
        {
            if (!EnableAttackEdgeConsumption)
            {
                return false;
            }

            var newest = GetNewestChordFrame();
            return newest is not null &&
                newest.NoteTime <= _lastHitTime + ATTACK_REFRACTORY_SECONDS + 1e-6;
        }

        // General single-note query, ported from FUN_014a5d4f (reached from
        // FUN_014a9adc).  It requires an attack edge (+0x7b5 changed or a
        // silence-to-sound onset) and at least two matching note detections.
        // The query starts at the chart note and scans back through the
        // calibrated front hit window, so an early-captured attack is matched
        // at the note instead of being discarded.  It is not the palm-mute
        // query (FUN_014a6459).
        private bool TryQueryNoteAttack(ProGuitarNote note)
        {
            int midi = GetTargetMidi(note);
            if (midi is < 0 or > 127)
            {
                return false;
            }

            // FUN_014a5d4f receives the chart note's per-note query bounds:
            // start = note.Time - 0.2, end = note.Time + 0.3 (live FUN_017b049a
            // evidence, note object +0x8/+0x0c).  It validates the detector's
            // latest note clock (+0xd08), scans note-clock frames back to the
            // lower bound, and requires a +0x7b5 changed frame plus at least
            // two matching top-two detections.
            ChordFrame? newest = GetNewestChordFrame();
            if (newest is null)
            {
                return false;
            }

            double earliest = note.Time - ROCKSMITH_QUERY_FRONT_SECONDS;
            double latest = note.Time + ROCKSMITH_QUERY_BACK_SECONDS;
            double detectorLatest = newest.NoteTime;
            if (detectorLatest < earliest - 1e-6 ||
                detectorLatest > latest + 1e-6)
            {
                return false;
            }

            double queryDuration = Math.Max(0, detectorLatest - earliest);
            bool hasAttackEdge = HasChordAttackInInputWindow(queryDuration,
                allowOnset: false, chordNowOverride: newest.ChordTime,
                out int attackFrameId, out double attackChordTime);
            int detections = 0;

            for (int i = 0; i < _chordHistory.Count; i++)
            {
                var frame = _chordHistory.GetFromNewest(i);
                if (frame.NoteTime <= earliest + 1e-6)
                {
                    break;
                }

                if (FrameNoteIsTopTwo(frame, midi, i))
                {
                    detections++;
                }
            }

            if (hasAttackEdge && detections > 1)
            {
                ConsumeAttackEdge(attackFrameId, attackChordTime);
                return true;
            }

            return false;
        }

        // FUN_014a9bfe -> FUN_014a6bd9: the tap/HOPO/pull-off query.  Unlike
        // FUN_014a5d4f this path does not require a +0x7b5 changed frame.  It
        // uses the complete current note-state table (+0x604/+0x6a4), the
        // FUN_014a6746 lower-candidate template selection, and the source
        // +0xdd8/+0xd38 gate.
        private bool TryQueryHopoNote(ProGuitarNote note)
        {
            int midi = GetTargetMidi(note);
            if (midi is < 0 or > 127)
            {
                return false;
            }

            // FUN_014a6bd9 reads the detector's current candidate table and
            // frame ring directly.  The source query does not require the
            // chart note time and does not discard a frame because it leads
            // the engine hit-check.
            ChordFrame? newest = GetNewestChordFrame();
            if (newest is null)
            {
                return false;
            }

            // FUN_014a6bd9 opens with the source detector-quality gate.
            if (newest.DetectorLevelDb < -55f || newest.DetectorQuality < 35f)
            {
                return false;
            }

            bool targetActive = TryGetNoteStateLevel(newest, midi, out float currentLevel);
            Span<int> lowerMatches = stackalloc int[6];
            bool lowerTemplate = TrySelectLowerCandidates(midi, newest,
                lowerMatches, out int lowerCount);
            if (!targetActive && !lowerTemplate)
            {
                return false;
            }

            if (note.Time < 0.0)
            {
                return false;
            }

            // FUN_017b049a passes the chart-note lower bound (noteTime - 0.5)
            // to FUN_014a9bfe.  FUN_014a6bd9 then adds 0.05 to that bound
            // (local_30), so the effective HOPO scan starts at noteTime-0.45.
            double earliest = note.Time - ROCKSMITH_QUERY_BOUND_SECONDS + 0.05;
            for (int i = 0; i < _chordHistory.Count; i++)
            {
                var frame = _chordHistory.GetFromNewest(i);
                if (frame.ChordTime <= earliest + 1e-6)
                {
                    break;
                }

                if (targetActive)
                {
                    if (!TryGetNoteStateLevel(frame, midi, out float level))
                    {
                        return true;
                    }

                    if (currentLevel > level + 1e-4f)
                    {
                        return true;
                    }
                }
                else
                {
                    // FUN_014a6746's lower-template branch selects the
                    // lower fundamentals whose harmonics reach the target.
                    // Preserve the source behavior: this branch only
                    // continues the backward scan when one of those
                    // candidates is present above the current target level.
                    bool lowerRise = false;
                    for (int k = 0; k < lowerCount; k++)
                    {
                        if (TryGetNoteStateLevel(frame, lowerMatches[k], out float level) &&
                            level > currentLevel + 1e-4f)
                        {
                            lowerRise = true;
                            break;
                        }
                    }

                    if (!lowerRise)
                    {
                        return false;
                    }
                }
            }

            return false;
        }

        // FUN_014a582a/FUN_014a585a: the source note-state table is the
        // frame's candidate list (+0x604/+0x6a4), not an arbitrary histogram
        // bin.  A target absent from the table has no level for this query.
        private static bool TryGetNoteStateLevel(ChordFrame frame, int midi,
            out float level)
        {
            level = 0f;
            if (midi < 0 || midi >= frame.Levels.Length)
            {
                return false;
            }

            int count = Math.Min(frame.CandidateCount, frame.CandidateNotes.Length);
            for (int i = 0; i < count; i++)
            {
                if (frame.CandidateNotes[i] == midi)
                {
                    level = frame.Levels[midi];
                    return true;
                }
            }

            return false;
        }

        // Exact port of FUN_014a6746.  The source row for the target starts
        // with the expected note, then contains lower fundamentals whose
        // harmonics reach it.  The function skips the first entry, stops
        // before the last nonzero entry (the source loop's iVar6 - 1 bound),
        // and requires two matches for longer templates.
        private static bool TrySelectLowerCandidates(int target, ChordFrame frame,
            Span<int> matches, out int matchCount)
        {
            int count = Math.Min(frame.CandidateCount, frame.CandidateNotes.Length);
            return TrySelectRocksmithLowerCandidates(target,
                frame.CandidateNotes.AsSpan(0, count), matches, out matchCount);
        }

        /// <summary>
        /// Exact port of FUN_014a6746.  The source row for the target starts
        /// with the expected note, then contains lower fundamentals whose
        /// harmonics reach it.  The function skips the first entry, stops
        /// before the last nonzero entry (the source loop's iVar6 - 1 bound),
        /// and requires two matches for longer templates.
        /// </summary>
        public static bool TrySelectRocksmithLowerCandidates(int target,
            ReadOnlySpan<int> candidateNotes, Span<int> matches, out int matchCount)
        {
            matchCount = 0;
            if (matches.Length == 0 || target < 22 || target > 93)
            {
                return false;
            }

            int row = target - 22;
            int templateCount = 0;
            while (templateCount < 6 &&
                   RocksmithChordTemplates[row, templateCount] > 0)
            {
                templateCount++;
            }

            int limit = templateCount - 1;
            if (limit <= 0)
            {
                return false;
            }

            for (int templateIndex = 1; templateIndex < limit; templateIndex++)
            {
                int templateNote = RocksmithChordTemplates[row, templateIndex];
                for (int candidateIndex = 0; candidateIndex < candidateNotes.Length;
                     candidateIndex++)
                {
                    if (candidateNotes[candidateIndex] == templateNote)
                    {
                        if (matchCount < matches.Length)
                        {
                            matches[matchCount] = templateNote;
                        }

                        matchCount++;
                        break;
                    }
                }
            }

            return limit > 2 ? matchCount >= 2 : matchCount >= limit;
        }

        // FUN_014a5f8e, the alternate note query used by the 0x20000000
        // arpeggio branch.  It follows the root/onset pitch rather than the
        // top-two candidate list and still requires the +0x7b5 attack edge and
        // more than one matching frame.
        private bool TryQueryArpeggioNote(ProGuitarNote note)
        {
            int midi = GetTargetMidi(note);
            if (midi is < 0 or > 127)
            {
                return false;
            }

            ChordFrame? newest = GetNewestChordFrame();
            if (newest is null)
            {
                return false;
            }

            double earliest = note.Time - ROCKSMITH_QUERY_FRONT_SECONDS;
            double latest = note.Time + ROCKSMITH_QUERY_BACK_SECONDS;
            double detectorLatest = newest.NoteTime;
            if (detectorLatest < earliest - 1e-6 ||
                detectorLatest > latest + 1e-6)
            {
                return false;
            }

            double queryDuration = Math.Max(0, detectorLatest - earliest);
            if (!HasChordAttackInInputWindow(queryDuration, allowOnset: false,
                    chordNowOverride: newest.ChordTime,
                    out int attackFrameId, out double attackChordTime))
            {
                return false;
            }

            // FUN_014a5f8e breaks at the frame carrying the changed ID
            // returned by FUN_014a4f20.  Only frames newer than that changed
            // frame belong to the current arpeggio attack.
            int changedOffset = -1;
            int changedFrameId = -1;
            for (int i = 0; i < _chordHistory.Count; i++)
            {
                var candidate = _chordHistory.GetFromNewest(i);
                if (candidate.NoteTime <= earliest + 1e-6)
                {
                    break;
                }

                if (candidate.ChordChanged)
                {
                    changedOffset = i;
                    changedFrameId = candidate.FrameId;
                    break;
                }
            }

            if (changedOffset < 0)
            {
                return false;
            }

            int detections = 0;
            for (int i = 0; i < changedOffset; i++)
            {
                var frame = _chordHistory.GetFromNewest(i);
                if (frame.NoteTime <= earliest + 1e-6)
                {
                    break;
                }

                if (changedFrameId > 0 && frame.FrameId == changedFrameId)
                {
                    break;
                }

                if (frame.RootNote == midi ||
                    MatchesFractionalPitch(midi, frame.RootPitch,
                        frame.PitchTolerance))
                {
                    detections++;
                }
            }

            if (detections > 1)
            {
                ConsumeAttackEdge(attackFrameId, attackChordTime);
                return true;
            }

            return false;
        }

        // FUN_014a61ba(-1,-1): the source immediate-state branch used by
        // chord fret-hand mutes, chord ignores, and unpitched slides.  It does
        // not use a chart interval, target pitch, changed frame, or matcher;
        // the current detector +0xd7d/+0xd81 state is the whole query.
        private bool TryQueryImmediateOnset()
        {
            return GetNewestChordFrame()?.ImmediateState == true;
        }

        // FUN_014a5889, the harmonic branch reached from FUN_014a9d7b for a
        // single note and FUN_014a5c6d for a harmonic chord.  The source maps
        // the chart harmonic node to the open-string pitch plus the natural
        // harmonic interval, then requires the target in two detector frames
        // under the +0xdd8/+0xd38 >= 50 gate.
        private bool TryQueryHarmonicNote(ProGuitarNote note)
        {
            int target = GetHarmonicTargetMidi(note);
            if (target is < 0 or > 127)
            {
                return false;
            }

            int count = Math.Min(2, _chordHistory.Count);
            int matches = 0;
            for (int i = 0; i < count; i++)
            {
                var frame = _chordHistory.GetFromNewest(i);
                if (frame.DetectorLevelDb < -55f ||
                    frame.DetectorQuality < 50f)
                {
                    return false;
                }

                if (TryGetNoteStateLevel(frame, target, out _) ||
                    HarmonicTemplateContains(frame, target))
                {
                    matches++;
                }
            }

            return matches >= 2;
        }

        private bool TryQueryHarmonicChord(
            IReadOnlyList<ProGuitarNote> chordNotes)
        {
            // FUN_014a5c6d counts only valid six-entry chart claims and calls
            // FUN_014a5889(note, 0) for each.  It allows one miss for more than
            // two valid entries and requires every valid entry otherwise.
            int valid = 0;
            int matched = 0;
            foreach (var note in chordNotes)
            {
                if (GetHarmonicTargetMidi(note) < 0)
                {
                    continue;
                }

                valid++;
                if (TryQueryHarmonicNote(note))
                {
                    matched++;
                }
            }

            if (valid == 0)
            {
                return false;
            }

            return valid > 2 ? matched >= valid - 1 : matched >= valid;
        }

        private int GetHarmonicTargetMidi(ProGuitarNote note)
        {
            if (note.String < 0 || note.String >= _openStringMidi.Length)
            {
                return -1;
            }

            int interval = note.Fret switch
            {
                2 => 36,
                4 => 24,
                5 or 9 or 17 => 19,
                7 or 16 => 12,
                12 or 19 => 0,
                _ => -1,
            };
            if (interval < 0)
            {
                return -1;
            }

            float pitch = _openStringMidi[note.String] + interval +
                (_tuningOffsetCents / 100f);
            return (int) MathF.Round(pitch, MidpointRounding.AwayFromZero);
        }

        private static bool HarmonicTemplateContains(ChordFrame frame, int target)
        {
            int count = Math.Min(frame.CandidateCount, frame.CandidateNotes.Length);
            for (int i = 0; i < count; i++)
            {
                if (RocksmithTemplateContains(target, frame.CandidateNotes[i], false))
                {
                    return true;
                }
            }

            return false;
        }

        // FUN_014a5d4f considers only the top two candidates in each frame.
        // It reads the refined pitch at the frame's history offset rather
        // than at the candidate index (see SourceCandidatePitchIndex).
        // FUN_014a5ce9 supplies the fractional tolerance.
        private static bool FrameNoteIsTopTwo(ChordFrame frame, int midi,
            int frameAge)
        {
            if (frame.CandidateCount > 0)
            {
                int first = -1;
                int second = -1;
                float firstLevel = 0f;
                float secondLevel = 0f;
                int count = Math.Min(frame.CandidateCount, frame.CandidateNotes.Length);
                for (int i = 0; i < count; i++)
                {
                    int note = frame.CandidateNotes[i];
                    if (note <= 0 || note >= frame.Levels.Length)
                    {
                        continue;
                    }

                    float level = frame.Levels[note];
                    if (level <= 0f)
                    {
                        continue;
                    }

                    if (level > firstLevel)
                    {
                        second = first;
                        secondLevel = firstLevel;
                        first = i;
                        firstLevel = level;
                    }
                    else if (level > secondLevel)
                    {
                        second = i;
                        secondLevel = level;
                    }
                }

                if (first >= 0 && CandidateMatches(frame, first, midi, frameAge))
                {
                    return true;
                }

                return second >= 0 &&
                    CandidateMatches(frame, second, midi, frameAge);
            }

            // Legacy/back-end fallback for frames without the candidate list.
            int firstNote = -1;
            int secondNote = -1;
            float firstLevelFallback = 0f;
            float secondLevelFallback = 0f;
            for (int note = 0; note < frame.Levels.Length; note++)
            {
                float level = frame.Levels[note];
                if (level <= 0f)
                {
                    continue;
                }

                if (level > firstLevelFallback)
                {
                    secondNote = firstNote;
                    secondLevelFallback = firstLevelFallback;
                    firstNote = note;
                    firstLevelFallback = level;
                }
                else if (level > secondLevelFallback)
                {
                    secondNote = note;
                    secondLevelFallback = level;
                }
            }

            return midi == firstNote || midi == secondNote;
        }

        private static bool CandidateMatches(ChordFrame frame, int candidateIndex,
            int midi, int frameAge)
        {
            int candidateNote = frame.CandidateNotes[candidateIndex];
            if (candidateNote == midi)
            {
                return true;
            }

            int pitchIndex = SourceCandidatePitchIndex(frame, frameAge);
            float pitch = frame.CandidatePitches[pitchIndex];
            return MatchesFractionalPitch(midi, pitch, frame.PitchTolerance);
        }

        /// <summary>
        /// FUN_014a5d4f reads +0xc4 with the frame-history index, not the
        /// candidate index.  The source frame's candidate-pitch array has 20
        /// entries; the query can scan a long frame history, so reads past
        /// entry 19 are undefined in the shipped binary.  YARG follows the
        /// source frame-age index and clamps only the undefined tail to the
        /// last fixed-size slot.
        /// </summary>
        private static int SourceCandidatePitchIndex(ChordFrame frame,
            int frameAge)
        {
            if (frameAge < 0)
            {
                return 0;
            }

            return Math.Min(frameAge, frame.CandidatePitches.Length - 1);
        }

        /// <summary>
        /// Port of the FUN_014a5ce9 pitch tolerance used by FUN_014a5d4f/
        /// FUN_014a5f8e.  No chart bend is represented here; the detector
        /// accepts a refined pitch above the integer target within a half
        /// semitone plus the small source slack.
        /// </summary>
        public static bool MatchesFractionalPitch(int target, float pitch,
            float tolerance = 0.05f)
        {
            if (float.IsNaN(pitch) || float.IsInfinity(pitch) || pitch <= 0f)
            {
                return false;
            }

            return MathF.Floor(pitch + 1e-4f) == target &&
                pitch < target + 0.5f + tolerance;
        }

        /// <summary>
        /// No-bend/tuning context for FUN_014a5ce9.  The source adds a small
        /// bend-dependent allowance unless the target is more than five
        /// semitones above the tuning/bend reference, in which case it uses
        /// the base tolerance.
        /// </summary>
        public static float GetFractionalPitchTolerance(int target,
            int bendSemitones, int tuningOffsetSemitones)
        {
            const float BaseTolerance = 0.05f;
            if (tuningOffsetSemitones + 5 + bendSemitones < target)
            {
                return BaseTolerance;
            }

            return MathF.Abs(bendSemitones) * 0.05f + BaseTolerance;
        }

        // Mute technique query, ported from FUN_014a61ba -> FUN_014a4ff0.
        // NOTE_MASK_MUTE is scored from a detector onset only; the source
        // does not ask for the target pitch, top-two candidates, or the
        // palm-mute level drop.  FUN_014a61ba maps the chart note start to
        // the detector's input clock and FUN_014a4ff0 scans backward for a
        // +0x795 onset in that interval.
        private bool TryQueryMuteNote(ProGuitarNote note)
        {
            // FUN_014a9e0b forwards the chart note interval to FUN_014a61ba.
            // FUN_014a61ba validates the detector note clock and calls
            // FUN_014a4ff0 with (current - start), where start is the chart
            // note's -0.2 s front offset, so the source scan begins at
            // note.Time - 0.2, not note.Time - 0.5.
            ChordFrame? newest = GetNewestChordFrame();
            if (newest is null)
            {
                return false;
            }

            double lower = note.Time - ROCKSMITH_QUERY_FRONT_SECONDS;
            double upper = note.Time + ROCKSMITH_QUERY_BACK_SECONDS;
            if (newest.NoteTime < lower - 1e-6 ||
                newest.NoteTime > upper + 1e-6)
            {
                return false;
            }

            double duration = Math.Max(0, newest.NoteTime - lower);
            double earliestChordTime = newest.ChordTime - duration;
            for (int i = 0; i < _chordHistory.Count; i++)
            {
                var frame = _chordHistory.GetFromNewest(i);
                if (frame.ChordTime <= earliestChordTime + 1e-6)
                {
                    break;
                }

                if (frame.Onset || frame.ImmediateState)
                {
                    return true;
                }
            }

            return false;
        }

        // Palm-mute technique query, reached from
        // AlphaGameBuiltins::WasPalmMutePlayed through FUN_014a9dab.
        //
        // FUN_014a6459 opens with a context flag at +0x35.  The detector
        // context is allocated by FUN_014ab3a3, which initializes the 16-bit
        // pair at +0x34 to zero.  REA found no writer to +0x35 in the shipped
        // executable, and a runtime Read during calibration/gameplay reported
        // zero, so the shipped Real Guitar path takes FUN_014a6459's fallback
        // to the normal note query (FUN_014a5d4f).  Treating the palm-mute
        // chart bit as the six-decibel seven-frame branch is therefore not
        // source behavior.  Keep the fallback explicit so the normal query's
        // attack/frame rules remain the only palm-mute admission path.
        private bool TryQueryPalmMuteNote(ProGuitarNote note)
        {
            return TryQueryNoteAttack(note);
        }

        /// <summary>
        /// Strict single-frame chord query matching FUN_014a6b69 and
        /// FUN_014a9c9d (AlphaGameBuiltins::GRNoteDetector_IsChordBeingPlayed_Strict).
        /// The source gates on detector +0xdd8 &gt;= -55 and +0xd38 &gt;= 50,
        /// then runs FUN_014a734d with strict all-expected matching on frame
        /// offset 0.  The chart-scoring chord path uses TryQueryChordAttack
        /// instead; this method exists for the strict detection API.
        /// </summary>
        public bool TryQueryChordStrict(IReadOnlyList<ProGuitarNote> chordNotes)
        {
            if (chordNotes.Count < 2)
            {
                return false;
            }

            ChordFrame? newest = GetNewestChordFrame();
            if (newest is null ||
                newest.DetectorLevelDb < -55f ||
                newest.DetectorQuality < 50f)
            {
                return false;
            }

            return MatchRocksmithChord(chordNotes, newest.Levels,
                allowMissingLargeChordTone: false);
        }

        // General chord query, ported from FUN_014a6f01/FUN_014a706d (reached
        // from FUN_014a9b0c/FUN_014a9b7b).  It requires a +0x7b5
        // new-chord/changed frame inside the caller window and a full-frame
        // match for the chart chord.  It deliberately has no slap/pop peak-drop
        // rule; that belongs to FUN_014a6243.
        private bool TryQueryChordAttack(ProGuitarNote parent,
            IReadOnlyList<ProGuitarNote> chordNotes)
        {
            if (chordNotes.Count < 2)
            {
                return false;
            }

            ChordFrame? newest = GetNewestChordFrame();
            if (newest is null)
            {
                return false;
            }

            double earliest = parent.Time - ROCKSMITH_QUERY_FRONT_SECONDS;
            double latest = parent.Time + ROCKSMITH_QUERY_BACK_SECONDS;
            double detectorLatest = newest.NoteTime;
            if (detectorLatest < earliest - 1e-6 ||
                detectorLatest > latest + 1e-6)
            {
                return false;
            }

            double queryDuration = Math.Max(0, detectorLatest - earliest);
            // FUN_014a6f01 uses FUN_014a4f20's +0x730 input-clock window for
            // the new-chord/changed gate, then checks frame offset 0 (the
            // current detector frame) with FUN_014a809f.
            bool chordChanged = HasChordAttackInInputWindow(queryDuration,
                allowOnset: false, chordNowOverride: newest.ChordTime,
                out int attackFrameId, out double attackChordTime);
            if (!chordChanged)
            {
                return false;
            }

            if (MatchRocksmithChartChord(chordNotes, newest.Levels,
                    allowMissingLargeChordTone: true))
            {
                ConsumeAttackEdge(attackFrameId, attackChordTime);
                return true;
            }

            return false;
        }

        // HOPO/pull-off chart chord branch: FUN_017b0a12 sees note masks
        // 0x200/0x400 and calls FUN_014a9d0c, which runs FUN_014a809f on the
        // newest detector frame without a +0x7b5 changed-frame requirement.
        private bool TryQueryHopoChordAttack(ProGuitarNote parent,
            IReadOnlyList<ProGuitarNote> chordNotes)
        {
            if (chordNotes.Count < 2)
            {
                return false;
            }

            // FUN_014a9d0c calls FUN_014a809f with frame offset 0.  The source
            // frame is the current detector frame, not the newest frame that
            // happens to be at or before the engine hit-check.
            ChordFrame? newest = GetNewestChordFrame();
            return newest is not null &&
                MatchRocksmithChartChord(chordNotes, newest.Levels,
                    allowMissingLargeChordTone: false);
        }

        /// <summary>
        /// Alternate count-rule chord query corresponding to FUN_014a706d.
        /// This is exposed for chart/script paths that explicitly use the
        /// detected-note-count gate; the normal Pro Guitar chart path uses
        /// FUN_014a6f01 instead.
        /// </summary>
        public bool TryQueryChordWithCountRule(IReadOnlyList<ProGuitarNote> chordNotes,
            double earliestTime, double latestTime)
        {
            if (chordNotes.Count < 2 || latestTime < earliestTime)
            {
                return false;
            }

            ChordFrame? newest = GetNewestChordFrame();
            if (newest is null ||
                newest.NoteTime < earliestTime - 1e-6 ||
                newest.NoteTime > latestTime + 1e-6)
            {
                return false;
            }

            int root = int.MaxValue;
            foreach (var note in chordNotes)
            {
                int midi = GetTargetMidi(note);
                if (midi >= 0)
                {
                    root = Math.Min(root, midi);
                }
            }

            if (root == int.MaxValue)
            {
                return false;
            }

            // FUN_014a706d calls FUN_014a4f20 first for the changed-frame
            // gate on the input/chord clock, then matches the note-time window.
            double queryDuration = Math.Max(0, newest.NoteTime - earliestTime);
            if (!HasChordAttackInInputWindow(queryDuration, allowOnset: false,
                    chordNowOverride: newest.ChordTime,
                    out int attackFrameId, out double attackChordTime))
            {
                return false;
            }

            for (int i = 0; i < _chordHistory.Count; i++)
            {
                var frame = _chordHistory.GetFromNewest(i);
                if (frame.NoteTime < earliestTime)
                {
                    break;
                }

                if (frame.NoteTime > latestTime + 1e-6)
                {
                    continue;
                }

                int detected = RocksmithDetectedNoteCount(frame.Levels, root);
                if (detected <= 1 || detected >= chordNotes.Count)
                {
                    continue;
                }

                if (MatchRocksmithChord(chordNotes, frame.Levels,
                    allowMissingLargeChordTone: true))
                {
                    ConsumeAttackEdge(attackFrameId, attackChordTime);
                    return true;
                }
            }

            return false;
        }

        private bool TryGetLatestLevelFrame(out ChordFrame frame, double earliestTime)
        {
            for (int i = 0; i < _chordHistory.Count; i++)
            {
                var candidate = _chordHistory.GetFromNewest(i);
                if (candidate.NoteTime < earliestTime)
                {
                    break;
                }

                if (candidate.NoteTime > _hitCheckTime + 1e-6)
                {
                    continue;
                }

                frame = candidate;
                return true;
            }

            frame = default!;
            return false;
        }

        private bool MatchesChordHistory(IReadOnlyList<ProGuitarNote> chartNotes,
            double earliestTime, bool strict)
        {
            for (int i = 0; i < _chordHistory.Count; i++)
            {
                var frame = _chordHistory.GetFromNewest(i);
                if (frame.NoteTime < earliestTime)
                {
                    break;
                }

                if (frame.NoteTime > _hitCheckTime + 1e-6)
                {
                    continue;
                }

                if (MatchRocksmithChartChord(chartNotes, frame.Levels,
                    allowMissingLargeChordTone: !strict))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasFreshChordMask(double time)
        {
            return (_chordNotes0 != 0 || _chordNotes1 != 0 ||
                    _chordNotes2 != 0 || _chordNotes3 != 0) &&
                time - _chordMaskTime <= CHORD_MASK_MAX_AGE_SECONDS;
        }

        private bool IsNoteInMask(int midi)
        {
            if (midi < 0 || midi > 127)
            {
                return false;
            }

            uint mask = (midi >> 5) switch
            {
                0 => _chordNotes0,
                1 => _chordNotes1,
                2 => _chordNotes2,
                _ => _chordNotes3,
            };

            return (mask & (1u << (midi & 31))) != 0;
        }

        private bool IsStrongNoteInMask(int midi)
        {
            if (midi < 0 || midi > 127)
            {
                return false;
            }

            uint mask = (midi >> 5) switch
            {
                0 => _strongNotes0,
                1 => _strongNotes1,
                2 => _strongNotes2,
                _ => _strongNotes3,
            };

            return (mask & (1u << (midi & 31))) != 0;
        }

        private int CandidateMidiAt(int index)
        {
            if (index < 8)
            {
                return (int) ((_candidateMidiPacked >> (index * 8)) & 0xFF);
            }

            return (int) ((_candidateMidiPacked2 >> ((index - 8) * 8)) & 0xFF);
        }

        private int CandidateLevelAt(int index)
        {
            if (index < 8)
            {
                return (int) ((_candidateLevelPacked >> (index * 8)) & 0xFF);
            }

            return (int) ((_candidateLevelPacked2 >> ((index - 8) * 8)) & 0xFF);
        }

        private float GetCandidateLevelExact(int midi)
        {
            if (midi < 0 || midi > 127)
            {
                return 0;
            }

            for (int i = 0; i < _candidateCount && i < 16; i++)
            {
                if (CandidateMidiAt(i) != midi)
                {
                    continue;
                }

                return CandidateLevelAt(i) / 255f;
            }

            return 0;
        }

        private float GetCandidateLevelForNote(int midi)
        {
            float level = GetCandidateLevelExact(midi);
            if (level > 0)
            {
                return level;
            }

            // Allow the expected fundamental to be represented by its octave
            // or low-order harmonic, as Rocksmith's harmonic templates do.
            level = Math.Max(level, GetCandidateLevelExact(midi + 12) * 0.50f);
            level = Math.Max(level, GetCandidateLevelExact(midi + 19) * 0.33f);
            level = Math.Max(level, GetCandidateLevelExact(midi + 24) * 0.25f);
            return level;
        }

        private static readonly float[] HarmonicIntervals =
        {
            12f, 19f, 24f, 28f, 31f, 34f, 36f, 38f, 40f, 43f, 46f, 48f,
        };

        private bool IsLowRootSpill(int midi, IReadOnlyList<ProGuitarNote> chartNotes)
        {
            float lowMidi = float.MaxValue;
            foreach (var note in chartNotes)
            {
                lowMidi = Math.Min(lowMidi, GetTargetPitch(note));
            }

            return lowMidi < float.MaxValue && Math.Abs(midi - lowMidi) <= 1.01f;
        }

        private bool IsHarmonicOfChartNotes(int midi, IReadOnlyList<ProGuitarNote> chartNotes)
        {
            // Use the lowest expected note.  Low-string fundamentals generate
            // the strongest overtones, and allowing every chord tone's
            // harmonics would also excuse upper extensions such as Cadd9.
            float rootMidi = float.MaxValue;
            foreach (var note in chartNotes)
            {
                rootMidi = Math.Min(rootMidi, GetTargetPitch(note));
            }

            float interval = midi - rootMidi;
            if (interval < 7f)
            {
                return false;
            }

            foreach (float harmonic in HarmonicIntervals)
            {
                if (Math.Abs(interval - harmonic) <= 0.8f)
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasStrongUnrelatedPitchClass(IReadOnlyList<ProGuitarNote> chartNotes,
            ISet<int> chartPitchClasses)
        {
            bool hasStrongNotes = _strongNotes0 != 0 || _strongNotes1 != 0 ||
                _strongNotes2 != 0 || _strongNotes3 != 0;
            if (!_hasReceivedStrongMask && _candidateCount == 0)
            {
                return false;
            }

            if (_candidateCount > 0)
            {
                for (int i = 0; i < _candidateCount && i < 16; i++)
                {
                    int midi = CandidateMidiAt(i);
                    int levelByte = CandidateLevelAt(i);
                    if (levelByte < 89)
                    {
                        continue;
                    }

                    int pitchClass = midi % 12;
                    if (chartPitchClasses.Contains(pitchClass) ||
                        IsHarmonicOfChartNotes(midi, chartNotes))
                    {
                        continue;
                    }

                    // Ringing notes from the chord just played are allowed
                    // through the short new-chord window.
                    if ((!_hasReceivedChordState || _chordNew) &&
                        _previousChordPitchClasses[pitchClass])
                    {
                        continue;
                    }

                    // Only low-bin spill around the chart root is allowed.
                    // Allowing ±1 semitone around every chord tone would also
                    // excuse a major-seventh extension (B against C major).
                    if (!IsLowRootSpill(midi, chartNotes))
                    {
                        return true;
                    }
                }
            }

            if (!hasStrongNotes)
            {
                return false;
            }

            for (int midi = 0; midi < 128; midi++)
            {
                if (!IsStrongNoteInMask(midi))
                {
                    continue;
                }

                int pitchClass = midi % 12;
                if (chartPitchClasses.Contains(pitchClass) ||
                    IsHarmonicOfChartNotes(midi, chartNotes))
                {
                    continue;
                }

                if (_chordNew && _previousChordPitchClasses[pitchClass])
                {
                    continue;
                }

                if (!IsLowRootSpill(midi, chartNotes))
                {
                    return true;
                }
            }

            return false;
        }

        private void ComputeChordCoverage(IReadOnlyList<ProGuitarNote> chartNotes,
            ISet<int> pitchClasses, bool[] detected,
            out int matchedNotes, out int presentVoices,
            out double matchedLevel, out double unmatchedLevel)
        {
            matchedNotes = 0;
            presentVoices = 0;
            matchedLevel = 0;
            unmatchedLevel = 0;

            if (_candidateCount > 0)
            {
                foreach (var note in chartNotes)
                {
                    int midi = GetTargetMidi(note);
                    float level = GetCandidateLevelForNote(midi);
                    if (level > 0)
                    {
                        matchedNotes++;
                        matchedLevel += level;
                    }
                    else if (detected[((midi % 12) + 12) % 12])
                    {
                        // The stable mask can still prove the pitch class
                        // when the top-N list was crowded by harmonics.
                        matchedNotes++;
                        matchedLevel += 0.25;
                    }
                }

                for (int i = 0; i < _candidateCount && i < 16; i++)
                {
                    int midi = CandidateMidiAt(i);
                    int levelByte = CandidateLevelAt(i);
                    if (levelByte == 0)
                    {
                        continue;
                    }

                    int pitchClass = midi % 12;
                    if (pitchClasses.Contains(pitchClass) ||
                        IsHarmonicOfChartNotes(midi, chartNotes))
                    {
                        continue;
                    }

                    if ((!_hasReceivedChordState || _chordNew) &&
                        _previousChordPitchClasses[pitchClass])
                    {
                        continue;
                    }

                    // Allow only low-bin spill around the chart root.
                    if (!IsLowRootSpill(midi, chartNotes))
                    {
                        unmatchedLevel += levelByte / 255.0;
                    }
                }
            }
            else
            {
                foreach (int pitchClass in pitchClasses)
                {
                    if (detected[pitchClass])
                    {
                        matchedNotes++;
                        matchedLevel += 1.0;
                    }
                }
            }

            LastChordMatchedLevel = matchedLevel;
            LastChordUnmatchedLevel = unmatchedLevel;
            LastChordCoverage = matchedLevel + unmatchedLevel > 0
                ? matchedLevel / (matchedLevel + unmatchedLevel)
                : 0;

            // Count voices by their exact chart MIDI (with an octave/harmonic
            // fallback).  This is the closest a detector without string
            // assignment can get to a per-string voicing check.
            if (_candidateCount > 0)
            {
                foreach (var note in chartNotes)
                {
                    int midi = GetTargetMidi(note);
                    if (GetCandidateLevelForNote(midi) > 0)
                    {
                        presentVoices++;
                    }
                }

                LastChordVoicingCoverage = chartNotes.Count > 0
                    ? (double) presentVoices / chartNotes.Count
                    : 0;
            }
            else
            {
                presentVoices = chartNotes.Count;
                LastChordVoicingCoverage = 1;
            }
        }

        private bool MatchesHeldChord(IReadOnlyList<ProGuitarNote> chartNotes)
        {
            if (_heldChordTime == double.NegativeInfinity ||
                _hitCheckTime - _heldChordTime > CHORD_MASK_MAX_AGE_SECONDS)
            {
                return false;
            }

            var pitchClasses = new SortedSet<int>();
            int rootMidi = int.MaxValue;
            foreach (var note in chartNotes)
            {
                int midi = GetTargetMidi(note);
                pitchClasses.Add(((midi % 12) + 12) % 12);
                rootMidi = Math.Min(rootMidi, midi);
            }

            if (pitchClasses.Count == 0)
            {
                return false;
            }

            var detected = new bool[12];
            int detectedClassCount = 0;
            for (int midi = 0; midi < 128; midi++)
            {
                if (!IsHeldNoteInMask(midi))
                {
                    continue;
                }

                int pitchClass = midi % 12;
                if (!detected[pitchClass])
                {
                    detected[pitchClass] = true;
                    detectedClassCount++;
                }
            }

            int root = ((rootMidi % 12) + 12) % 12;
            if (!detected[root] || detectedClassCount != pitchClasses.Count)
            {
                return false;
            }

            foreach (int pitchClass in pitchClasses)
            {
                if (!detected[pitchClass])
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsHeldNoteInMask(int midi)
        {
            if (midi < 0 || midi > 127)
            {
                return false;
            }

            uint mask = (midi >> 5) switch
            {
                0 => _heldChordNotes0,
                1 => _heldChordNotes1,
                2 => _heldChordNotes2,
                _ => _heldChordNotes3,
            };

            return (mask & (1u << (midi & 31))) != 0;
        }

        private bool MatchesChartChord(IReadOnlyList<ProGuitarNote> chartNotes, bool strict)
        {
            // Guitar voicings duplicate roots, thirds, and fifths across
            // octaves.  Rocksmith's matcher is based on the chord's note set
            // and root, not on one exact FFT octave.  Collapse the chart to
            // pitch classes so a missing high octave does not reject a
            // correctly played chord, while still requiring the root and the
            // chord-defining third/seventh.
            var pitchClasses = new SortedSet<int>();
            int rootMidi = int.MaxValue;

            foreach (var note in chartNotes)
            {
                int midi = GetTargetMidi(note);
                pitchClasses.Add(((midi % 12) + 12) % 12);
                if (midi < rootMidi)
                {
                    rootMidi = midi;
                }
            }

            if (pitchClasses.Count == 0)
            {
                return false;
            }

            int root = ((rootMidi % 12) + 12) % 12;
            var detected = new bool[12];
            for (int midi = 0; midi < 128; midi++)
            {
                if (IsNoteInMask(midi))
                {
                    detected[midi % 12] = true;
                }
            }

            if (!detected[root])
            {
                return false;
            }

            ComputeChordCoverage(chartNotes, pitchClasses, detected,
                out int matchedNotes, out int presentVoices,
                out double matchedLevel, out double unmatchedLevel);

            // A root-plus-octave double stop has one pitch class.  Accept it
            // when the root is present, but still reject a loud unrelated
            // extension (for example E5 plus a strong B).
            if (pitchClasses.Count == 1)
            {
                if (HasStrongUnrelatedPitchClass(chartNotes, pitchClasses))
                {
                    return false;
                }

                if (matchedLevel > 0 &&
                    unmatchedLevel > matchedLevel * 0.20 + 0.02)
                {
                    return false;
                }

                return true;
            }

            // The strict attack query requires every expected chord tone.
            // This is the important false-positive guard: a C major chart
            // chord must not be satisfied by A minor (A/C/E), which shares C
            // and E but omits the fifth G.
            if (strict)
            {
                foreach (int pitchClass in pitchClasses)
                {
                    if (!detected[pitchClass])
                    {
                        return false;
                    }
                }

                // Also reject an unrelated bass note that is clearly below
                // the chart voicing.  This catches extensions such as Am7
                // (A/C/E/G) against a C major chart while allowing low-octave
                // chord tones and the one-semitone spill from the low-note
                // detector.
                int lowestDetected = -1;
                for (int midi = 0; midi < 128; midi++)
                {
                    if (IsNoteInMask(midi))
                    {
                        lowestDetected = midi;
                        break;
                    }
                }

                if (lowestDetected >= 0 &&
                    lowestDetected < rootMidi - 2 &&
                    !pitchClasses.Contains(lowestDetected % 12))
                {
                    return false;
                }

                if (HasStrongUnrelatedPitchClass(chartNotes, pitchClasses))
                {
                    return false;
                }

                // Matched/unmatched FFT levels: a chord may contain every
                // expected pitch class and still be wrong because a loud
                // extension dominates the frame.  This unmatched-energy gate
                // is YARG-specific and deliberately stricter than Rocksmith's
                // strict query, which has no explicit extra-note veto.
                if (matchedLevel > 0 &&
                    unmatchedLevel > matchedLevel * 0.20 + 0.02)
                {
                    return false;
                }

                // Per-voice coverage.  Top-N levels are only available when
                // a candidate list was supplied; legacy single-pitch tests
                // and backends fall back to the mask-only path.
                if (_candidateCount > 0)
                {
                    int required = chartNotes.Count <= 4
                        ? Math.Min(chartNotes.Count, pitchClasses.Count)
                        : Math.Min(chartNotes.Count - 1, pitchClasses.Count);
                    if (presentVoices < required)
                    {
                        return false;
                    }
                }

                return true;
            }

            // Rocksmith's loose sustain query tolerates one missing note in
            // large chords, but still requires the root and keeps unrelated
            // level energy under control.  Small chords and power chords
            // remain strict even while sustained.
            int missing = 0;
            foreach (int pitchClass in pitchClasses)
            {
                if (!detected[pitchClass])
                {
                    missing++;
                }
            }

            int allowedMissing = pitchClasses.Count >= 5 ? 1 : 0;
            if (missing > allowedMissing)
            {
                return false;
            }

            if (matchedLevel > 0 &&
                unmatchedLevel > matchedLevel * 0.20 + 0.02)
            {
                return false;
            }

            if (_candidateCount > 0)
            {
                int required = chartNotes.Count <= 4
                    ? Math.Min(chartNotes.Count, pitchClasses.Count)
                    : Math.Min(chartNotes.Count - 1, pitchClasses.Count);
                if (presentVoices < required)
                {
                    return false;
                }
            }

            if (HasStrongUnrelatedPitchClass(chartNotes, pitchClasses))
            {
                return false;
            }

            return true;
        }

        // Port of FUN_014a809f, the normal chart chord matcher reached from
        // FUN_014a9b0c/FUN_014a6f01 and from FUN_014a9d0c for HOPO/pull-off
        // chords.  Unlike FUN_014a734d this matcher has no 10% threshold and
        // no per-actual used marker: one detected bin may cover more than one
        // expected template.  The octave-double-stop branch in the source is
        // gated to detector mode 2 (four-string bass), so it does not run for
        // the six-string Real Guitar path.
        private bool MatchRocksmithChartChord(IReadOnlyList<ProGuitarNote> chartNotes,
            float[] levels, bool allowMissingLargeChordTone)
        {
            var expected = new List<int>(6);
            foreach (var note in chartNotes)
            {
                int midi = GetTargetMidi(note);
                if (midi is >= 0 and < 128 && !expected.Contains(midi))
                {
                    expected.Add(midi);
                }
            }

            expected.Sort();
            int expectedCount = expected.Count;
            if (expectedCount < 2)
            {
                return false;
            }

            // FUN_014a809f only assigns the small-chord root when the lowest
            // expected note is below MIDI 65.
            int root = expectedCount < 4 && expected[0] < 65 ? expected[0] : -1;

            var actual = new List<(int Note, float Level)>(128);
            float maxLevel = 0f;
            for (int midi = 0; midi < 128; midi++)
            {
                float level = levels[midi];
                if (level <= 0f)
                {
                    continue;
                }

                actual.Add((midi, level));
                if (level > maxLevel)
                {
                    maxLevel = level;
                }
            }

            if (maxLevel <= 0f)
            {
                return false;
            }

            actual.Sort((a, b) => b.Level.CompareTo(a.Level));

            Span<int> matchesPerExpected = stackalloc int[expectedCount];
            matchesPerExpected.Clear();
            float matchedLevel = 0f;
            float unmatchedLevel = 0f;

            foreach (var (note, level) in actual)
            {
                bool matchedAny = false;
                // FUN_014a809f checks every expected template for each actual
                // candidate.  It does not set a per-actual used flag, so a
                // harmonic can cover more than one expected note.
                for (int e = 0; e < expectedCount; e++)
                {
                    int expectedNote = expected[e];
                    bool rootOnly = root >= 0 && expectedNote == root;
                    if (!RocksmithTemplateContains(expectedNote, note, rootOnly))
                    {
                        continue;
                    }

                    matchesPerExpected[e]++;
                    matchedLevel += level;
                    matchedAny = true;
                }

                int matchedExpected = 0;
                for (int e = 0; e < expectedCount; e++)
                {
                    if (matchesPerExpected[e] > 0)
                    {
                        matchedExpected++;
                    }
                }

                if (matchedExpected == expectedCount)
                {
                    break;
                }

                if (!matchedAny)
                {
                    unmatchedLevel += level;
                }
            }

            int matchedExpectedCount = 0;
            for (int e = 0; e < expectedCount; e++)
            {
                if (matchesPerExpected[e] > 0)
                {
                    matchedExpectedCount++;
                }
            }

            LastChordMatchedLevel = matchedLevel;
            LastChordUnmatchedLevel = unmatchedLevel;
            LastChordCoverage = matchedLevel + unmatchedLevel > 0
                ? matchedLevel / (matchedLevel + unmatchedLevel)
                : 0;
            LastChordVoicingCoverage = (double) matchedExpectedCount / expectedCount;

            // Live 2026-10-31 evidence: the normal chart matcher accepted
            // six-note chords whose exact expected bins were only 2/6 or 4/6
            // because lower fundamentals in DAT_01197398 covered the missing
            // upper templates.  It also accepted lower-only octave chords for
            // the same reason, while never accepting an upper-only octave.
            // The one-missing branches below are a separate YARG extension
            // retained from the user-confirmed five/six-note behavior; the
            // source FUN_014a809f path itself accepts only when every expected
            // template is covered.
            //
            // The FUN_014a8c94 octave-double-stop shortcut is reached only
            // when detector mode is 2 (four-string bass).  Real Guitar uses
            // the normal six-string mode, so an octave pair is matched by the
            // same per-expected coverage rule as every other shape.
            if (expectedCount < 4)
            {
                return matchedExpectedCount == expectedCount;
            }

            bool accepted;
            if (allowMissingLargeChordTone && expectedCount == 6)
            {
                accepted = matchedExpectedCount > 5 ||
                    (matchedExpectedCount == 5 &&
                        unmatchedLevel < matchedLevel * 0.20f);
            }
            else if (allowMissingLargeChordTone && expectedCount == 5)
            {
                accepted = matchedExpectedCount > 4 ||
                    (matchedExpectedCount == 4 &&
                        unmatchedLevel < matchedLevel * 0.20f);
            }
            else
            {
                accepted = expectedCount <= matchedExpectedCount;
            }

            return accepted;
        }

        private bool MatchRocksmithChord(IReadOnlyList<ProGuitarNote> chartNotes,
            float[] levels, bool allowMissingLargeChordTone)
        {
            // Port of FUN_014a734d.  Expected notes are deduplicated exact
            // MIDI notes.  The lowest expected note is used as the root for
            // chords of fewer than four notes, and that root is matched only
            // by its own bin.  The other expected notes (and all expected
            // notes in larger chords) also accept the lower fundamentals
            // whose 3rd/5th/7th/9th/11th harmonics reach the expected note.
            var expected = new List<int>(6);
            foreach (var note in chartNotes)
            {
                int midi = GetTargetMidi(note);
                if (midi is >= 0 and < 128 && !expected.Contains(midi))
                {
                    expected.Add(midi);
                }
            }

            expected.Sort();
            int expectedCount = expected.Count;
            if (expectedCount < 2)
            {
                return false;
            }

            int root = -1;
            if (expectedCount < 4)
            {
                root = expected[0];
                for (int i = 1; i < expectedCount; i++)
                {
                    if (expected[i] < root)
                    {
                        root = expected[i];
                    }
                }
            }

            var actual = new List<(int Note, float Level)>(128);
            float maxLevel = 0f;
            for (int midi = 0; midi < 128; midi++)
            {
                float level = levels[midi];
                if (level <= 0)
                {
                    continue;
                }

                actual.Add((midi, level));
                if (level > maxLevel)
                {
                    maxLevel = level;
                }
            }

            if (maxLevel <= 0)
            {
                return false;
            }

            actual.Sort((a, b) => b.Level.CompareTo(a.Level));

            float baseThreshold = expectedCount < 4 ? maxLevel * 0.10f : 0f;
            Span<int> matchesPerExpected = stackalloc int[expectedCount];
            matchesPerExpected.Clear();
            int totalMatches = 0;
            float matchedLevel = 0f;
            float unmatchedLevel = 0f;

            foreach (var (note, level) in actual)
            {
                float threshold = baseThreshold;
                if (expectedCount < 4 && note > 40)
                {
                    threshold = baseThreshold * ((note - 40) / 22f);
                    if (threshold <= 5f)
                    {
                        threshold = 5f;
                    }
                }

                if (level <= threshold)
                {
                    continue;
                }

                bool used = false;
                for (int e = 0; e < expectedCount && !used; e++)
                {
                    int expectedNote = expected[e];
                    bool rootOnly = expectedCount < 4 && expectedNote == root;

                    if (!RocksmithTemplateContains(expectedNote, note, rootOnly))
                    {
                        continue;
                    }

                    matchesPerExpected[e]++;
                    totalMatches++;
                    matchedLevel += level;
                    used = true;
                }

                int matchedExpected = 0;
                for (int e = 0; e < expectedCount; e++)
                {
                    if (matchesPerExpected[e] > 0)
                    {
                        matchedExpected++;
                    }
                }

                if (matchedExpected == expectedCount)
                {
                    break;
                }

                if (!used)
                {
                    unmatchedLevel += level;
                }
            }

            int matchedExpectedCount = 0;
            for (int e = 0; e < expectedCount; e++)
            {
                if (matchesPerExpected[e] > 0)
                {
                    matchedExpectedCount++;
                }
            }

            LastChordMatchedLevel = matchedLevel;
            LastChordUnmatchedLevel = unmatchedLevel;
            LastChordCoverage = matchedLevel + unmatchedLevel > 0
                ? matchedLevel / (matchedLevel + unmatchedLevel)
                : 0;
            LastChordVoicingCoverage = (double) matchedExpectedCount / expectedCount;

            // FUN_014a8c94 is gated to detector mode 2 (four-string bass) in
            // the source.  Real Guitar uses the normal six-string rules.
            if (expectedCount < 4 && totalMatches < 2)
            {
                return false;
            }

            bool accepted;
            if (allowMissingLargeChordTone && expectedCount == 6)
            {
                accepted = matchedExpectedCount > 5 ||
                    (matchedExpectedCount == 5 &&
                        unmatchedLevel < matchedLevel * 0.20f);
            }
            else if (allowMissingLargeChordTone && expectedCount == 5)
            {
                accepted = matchedExpectedCount > 4 ||
                    (matchedExpectedCount == 4 &&
                        unmatchedLevel < matchedLevel * 0.20f);
            }
            else
            {
                accepted = expectedCount <= matchedExpectedCount;
            }

            return accepted;
        }

        /// <summary>
        /// Returns the index of <paramref name="actualNote"/> in the harmonic
        /// template for <paramref name="expectedNote"/>, or -1.
        /// </summary>
        public static int RocksmithTemplateIndex(int expectedNote, int actualNote)
        {
            if (expectedNote < 22 || expectedNote > 93)
            {
                return -1;
            }

            int row = expectedNote - 22;
            for (int i = 0; i < 6; i++)
            {
                int note = RocksmithChordTemplates[row, i];
                if (note == 0)
                {
                    break;
                }

                if (note == actualNote)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Port of FUN_014a14d9.  Counts detected notes that are not covered by
        /// the root's harmonic template.
        /// </summary>
        public static int RocksmithDetectedNoteCount(float[] levels, int root)
        {
            float maxLevel = 0f;
            for (int i = 0; i < levels.Length; i++)
            {
                if (levels[i] > maxLevel)
                {
                    maxLevel = levels[i];
                }
            }

            if (maxLevel <= 0f)
            {
                return 0;
            }

            int count = 1;
            float threshold = maxLevel * 0.3f;
            for (int note = 0; note < levels.Length; note++)
            {
                if (levels[note] <= threshold || note == root)
                {
                    continue;
                }

                if (RocksmithTemplateIndex(root, note) < 0)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Port of FUN_014a272f/FUN_0149f8b3 using the current local-maximum
        /// candidate list, which is the list used by the source root-quality
        /// gate.
        /// </summary>
        public static bool IsRocksmithRootQuality(int root, ReadOnlySpan<float> candidates)
        {
            if (root < 22 || root > 93)
            {
                return true;
            }

            int templateLength = 0;
            int present = 0;
            int row = root - 22;
            for (int i = 0; i < 6; i++)
            {
                int note = RocksmithChordTemplates[row, i];
                if (note == 0)
                {
                    break;
                }

                templateLength++;
                if (note >= 0 && note < candidates.Length && candidates[note] > 0f)
                {
                    present++;
                }
            }

            if (templateLength > 3)
            {
                return templateLength / 2 <= present;
            }

            if (templateLength > 2)
            {
                return present > 1;
            }

            if (templateLength > 1)
            {
                return present != 0;
            }

            if (root > 0x31)
            {
                return present == templateLength;
            }

            return true;
        }

        /// <summary>
        /// Port of FUN_014a272f/FUN_0149f8b3.  Counts how many notes from the
        /// root's harmonic template are present in the detector's stable mask.
        /// </summary>
        public static bool IsRocksmithRootQuality(int root, ulong lowMask, ulong highMask)
        {
            if (root < 22 || root > 93)
            {
                return true;
            }

            int templateLength = 0;
            int present = 0;
            int row = root - 22;
            for (int i = 0; i < 6; i++)
            {
                int note = RocksmithChordTemplates[row, i];
                if (note == 0)
                {
                    break;
                }

                templateLength++;
                bool isPresent = note < 64
                    ? ((lowMask >> note) & 1UL) != 0
                    : ((highMask >> (note - 64)) & 1UL) != 0;
                if (isPresent)
                {
                    present++;
                }
            }

            if (templateLength > 3)
            {
                return templateLength / 2 <= present;
            }

            if (templateLength > 2)
            {
                return present > 1;
            }

            if (templateLength > 1)
            {
                return present != 0;
            }

            if (root > 0x31)
            {
                return present == templateLength;
            }

            return true;
        }

        // Exact table read from Rocksmith2014.exe at 0x01197398 (72 notes,
        // six 32-bit MIDI entries each).  It lists the expected note followed
        // by the lower fundamentals whose 3rd/5th/7th/9th/11th harmonics
        // reach it.  Evidence: ev_8062f2456f6e33b73f9a9eb94495eebc9e03fc94b39748b53a4f012895b258f3.
        private static readonly int[,] RocksmithChordTemplates =
        {
            { 22, 0, 0, 0, 0, 0 },
            { 23, 0, 0, 0, 0, 0 },
            { 24, 0, 0, 0, 0, 0 },
            { 25, 0, 0, 0, 0, 0 },
            { 26, 0, 0, 0, 0, 0 },
            { 27, 0, 0, 0, 0, 0 },
            { 28, 0, 0, 0, 0, 0 },
            { 29, 0, 0, 0, 0, 0 },
            { 30, 0, 0, 0, 0, 0 },
            { 31, 0, 0, 0, 0, 0 },
            { 32, 0, 0, 0, 0, 0 },
            { 33, 0, 0, 0, 0, 0 },
            { 34, 0, 0, 0, 0, 0 },
            { 35, 0, 0, 0, 0, 0 },
            { 36, 0, 0, 0, 0, 0 },
            { 37, 0, 0, 0, 0, 0 },
            { 38, 0, 0, 0, 0, 0 },
            { 39, 0, 0, 0, 0, 0 },
            { 40, 0, 0, 0, 0, 0 },
            { 41, 0, 0, 0, 0, 0 },
            { 42, 0, 0, 0, 0, 0 },
            { 43, 24, 0, 0, 0, 0 },
            { 44, 25, 0, 0, 0, 0 },
            { 45, 26, 0, 0, 0, 0 },
            { 46, 27, 0, 0, 0, 0 },
            { 47, 28, 0, 0, 0, 0 },
            { 48, 29, 0, 0, 0, 0 },
            { 49, 30, 0, 0, 0, 0 },
            { 50, 31, 0, 0, 0, 0 },
            { 51, 32, 0, 0, 0, 0 },
            { 52, 33, 24, 0, 0, 0 },
            { 53, 34, 25, 0, 0, 0 },
            { 54, 35, 26, 0, 0, 0 },
            { 55, 36, 27, 0, 0, 0 },
            { 56, 37, 28, 0, 0, 0 },
            { 57, 38, 29, 0, 0, 0 },
            { 58, 39, 30, 24, 0, 0 },
            { 59, 40, 31, 25, 0, 0 },
            { 60, 41, 32, 26, 0, 0 },
            { 61, 42, 33, 27, 0, 0 },
            { 62, 43, 34, 28, 24, 0 },
            { 63, 44, 35, 29, 25, 0 },
            { 64, 45, 36, 30, 26, 0 },
            { 65, 46, 37, 31, 27, 0 },
            { 66, 47, 38, 32, 28, 0 },
            { 67, 48, 39, 33, 29, 0 },
            { 68, 49, 40, 34, 30, 0 },
            { 69, 50, 41, 35, 31, 0 },
            { 70, 51, 42, 36, 32, 0 },
            { 71, 52, 43, 37, 33, 24 },
            { 72, 53, 44, 38, 34, 25 },
            { 73, 54, 45, 39, 35, 26 },
            { 74, 55, 46, 40, 36, 27 },
            { 75, 56, 47, 41, 37, 28 },
            { 76, 57, 48, 42, 38, 29 },
            { 77, 58, 49, 43, 39, 30 },
            { 78, 59, 50, 44, 40, 31 },
            { 79, 60, 51, 45, 41, 32 },
            { 80, 61, 52, 45, 42, 33 },
            { 81, 62, 53, 47, 43, 34 },
            { 82, 63, 54, 48, 44, 35 },
            { 83, 64, 55, 49, 45, 36 },
            { 84, 65, 56, 50, 46, 37 },
            { 85, 66, 57, 51, 47, 38 },
            { 86, 67, 58, 52, 48, 39 },
            { 87, 68, 59, 53, 49, 42 },
            { 88, 69, 60, 54, 50, 43 },
            { 89, 70, 61, 55, 51, 44 },
            { 90, 71, 62, 56, 52, 0 },
            { 91, 72, 63, 57, 53, 0 },
            { 92, 73, 64, 58, 54, 0 },
            { 93, 74, 66, 59, 55, 0 },
        };

        private static bool RocksmithTemplateContains(int expectedNote, int actualNote,
            bool rootOnly)
        {
            if (actualNote == expectedNote)
            {
                return true;
            }

            if (rootOnly || actualNote <= 0 || expectedNote < 22 || expectedNote > 93)
            {
                return false;
            }

            int index = expectedNote - 22;
            for (int i = 0; i < 6; i++)
            {
                if (RocksmithChordTemplates[index, i] == actualNote)
                {
                    return true;
                }
            }

            return false;
        }

        private void HitChord(ProGuitarNote parent)
        {
            // Hold the chart chord that actually matched, not the raw FFT
            // mask.  The mask can contain transient extras or miss a quiet
            // voice; copying it would let those imperfections make the next
            // repeated attack fail.  The chart set is stable by definition.
            _heldChordNotes0 = 0;
            _heldChordNotes1 = 0;
            _heldChordNotes2 = 0;
            _heldChordNotes3 = 0;
            foreach (var note in parent.AllNotes)
            {
                int midi = GetTargetMidi(note);
                if (midi is < 0 or > 127)
                {
                    continue;
                }

                switch (midi >> 5)
                {
                    case 0:
                        _heldChordNotes0 |= 1u << (midi & 31);
                        break;
                    case 1:
                        _heldChordNotes1 |= 1u << (midi & 31);
                        break;
                    case 2:
                        _heldChordNotes2 |= 1u << (midi & 31);
                        break;
                    default:
                        _heldChordNotes3 |= 1u << (midi & 31);
                        break;
                }
            }
            _heldChordTime = _hitCheckTime;

            Array.Clear(_previousChordPitchClasses, 0, _previousChordPitchClasses.Length);
            foreach (var note in parent.AllNotes)
            {
                int midi = GetTargetMidi(note);
                _previousChordPitchClasses[((midi % 12) + 12) % 12] = true;
                HitNote(note);
            }
        }

        private void MissChord(ProGuitarNote parent)
        {
            // A missed note's frames must not be reused by the next chart
            // note.  The late caller window can otherwise leave a strum in
            // history after the note has been advanced.
            _lastNoteQueryFrameTime = Math.Max(_lastNoteQueryFrameTime,
                _hitCheckTime);
            _lastChordQueryFrameTime = Math.Max(_lastChordQueryFrameTime,
                _hitCheckTime);

            // YARG Layer 2 extension for misses: consume the current detector
            // edge so a wrong strum during this note's window cannot satisfy
            // the next chart chord.  Rocksmith's FUN_014a4f20 has no miss
            // consumption and would still return that edge.  A miss does not
            // start the post-hit refractory.
            ConsumeNewestAttackEdge(updateLastHitTime: false);

            foreach (var note in parent.AllNotes)
            {
                MissNote(note);
            }
        }

        protected override void HitNote(ProGuitarNote note)
        {
            if (note.WasHit || note.WasMissed)
            {
                return;
            }

            // YARG Layer 2: every successful hit consumes the current detector
            // edge, regardless of which query path produced the hit.  This
            // covers normal, HOPO/tap, harmonic, muted, immediate, and
            // arpeggio paths; Rocksmith itself does not consume.
            ConsumeNewestAttackEdge();

            note.SetHitState(true, includeChildren: false);

            bool skipped = SkipPreviousNotes(note);

            if (note.IsStarPower)
            {
                if (EngineStats.IsStarPowerActive && EngineParameters.NoStarPowerOverlap)
                {
                    StripStarPower(note);
                }
                else if (note.IsStarPowerEnd)
                {
                    AwardStarPower(note);
                    EngineStats.StarPowerPhrasesHit++;
                }
            }

            IncrementCombo();
            EngineStats.IncrementNotesHit(note, _judgementTime);
            UpdateMultiplier();
            AddScore(note);

            if (note.IsSustain)
            {
                StartSustain(note);
            }

            OnNoteHit?.Invoke(NoteIndex, note);
            base.HitNote(note);
        }

        protected override void MissNote(ProGuitarNote note)
        {
            if (note.WasHit || note.WasMissed)
            {
                return;
            }

            note.SetMissState(true, includeChildren: false);

            if (note.IsStarPower)
            {
                StripStarPower(note);
            }

            ResetCombo();
            UpdateMultiplier();

            OnNoteMissed?.Invoke(NoteIndex, note);
            base.MissNote(note);
        }

        protected override void AddScore(ProGuitarNote note)
        {
            EngineStats.NoteScore += POINTS_PER_PRO_NOTE;
            AddScore(POINTS_PER_PRO_NOTE);
        }

        protected override void UpdateMultiplier()
        {
            int previousMultiplier = EngineStats.ScoreMultiplier;
            base.UpdateMultiplier();
            int newMultiplier = EngineStats.ScoreMultiplier;

            if (newMultiplier != previousMultiplier)
            {
                EngineStats.ScoreMultiplier = previousMultiplier;
                RebaseSustains(CurrentTick);
                EngineStats.ScoreMultiplier = newMultiplier;
            }
        }

        protected override void UpdateBot(double time)
        {
            if (!IsBot)
            {
                return;
            }

            int guard = 0;
            while (NoteIndex < Notes.Count)
            {
                var parent = Notes[NoteIndex];
                if (parent.Time > time + 0.0001)
                {
                    break;
                }

                // A broken hit state must never turn the bot into an infinite
                // loop on the main thread.
                if (guard++ > Notes.Count)
                {
                    YargLogger.LogError("Real Guitar bot failed to advance through the chart.");
                    break;
                }

                int before = NoteIndex;
                foreach (var note in parent.AllNotes)
                {
                    HitNote(note);
                }

                if (NoteIndex == before)
                {
                    YargLogger.LogWarning("Real Guitar bot forced past an unresolved note.");
                    NoteIndex++;
                }
            }
        }

        protected override (int baseScore, int noteScore) CalculateChartScores()
        {
            double baseScore = 0;
            double noteScore = 0;
            int combo = 0;

            foreach (var parent in Notes)
            {
                if (parent.IsBigRockEnding)
                {
                    continue;
                }

                foreach (var note in parent.AllNotes)
                {
                    int multiplier = Math.Min((combo / 10) + 1, EngineParameters.MaxMultiplier);
                    double sustainPoints = Math.Ceiling(note.TickLength / TicksPerSustainPoint);
                    double points = POINTS_PER_PRO_NOTE + sustainPoints;

                    baseScore += multiplier * points;
                    noteScore += points;
                    combo++;
                }
            }

            YargLogger.LogFormatDebug("Real Guitar chart score: base {0}, notes {1}, combo {2}",
                baseScore, noteScore, combo);
            return ((int) Math.Ceiling(baseScore), (int) Math.Ceiling(noteScore));
        }

        protected override bool ProximalLaneForgivesInput(int inputNote, ProGuitarNote laneNote)
        {
            return false;
        }

        private sealed class ChordFrame
        {
            // Rocksmith's +0x720 is the game/note-query clock and +0x730 is
            // the input/chord-query clock.  Note-level scans use NoteTime;
            // attack, chord-change, and age gates use ChordTime.
            public double NoteTime;
            public double ChordTime;
            public bool Onset;
            public bool ChordChanged;
            public bool ChordNew;
            public ChordQualityFlags QualityFlags;
            public float Peak;
            public float LevelDb;
            public readonly float[] Levels = new float[128];
            public int CandidateCount;
            public readonly int[] CandidateNotes = new int[20];
            public readonly float[] CandidatePitches = new float[20];
            public int RootNote;
            public float RootPitch;
            public float PitchTolerance = 0.05f;
            public int FrameId;
            public float DetectorLevelDb;
            public float DetectorQuality = 100f;
            public bool ImmediateState;
        }

        private sealed class ChordFrameHistory
        {
            // FUN_0149b848 allocates the source frame ring for three seconds
            // of detector hops.  At the shipped 48 kHz / 512-sample geometry
            // that is ceil(48000 / 512 * 3) = 282 frames; at 44.1 kHz with
            // YARG's scaled 470-sample hop it is also 282.  The previous
            // 32-frame ring could not retain the source's 0.5 s query window.
            private const int Capacity = 282;
            private readonly ChordFrame?[] _frames = new ChordFrame?[Capacity];
            private int _count;
            private int _start;

            public int Count => _count;

            public void Clear()
            {
                Array.Clear(_frames, 0, _frames.Length);
                _count = 0;
                _start = 0;
            }

            public void Upsert(double noteTime, double chordTime, float[] levels,
                bool onset, bool chordChanged, bool chordNew, float peak,
                float levelDb, ChordQualityFlags qualityFlags, int candidateCount,
                int[]? candidateNotes, float[]? candidatePitches, int rootNote,
                float rootPitch, float pitchTolerance, int frameId = 0,
                float detectorLevelDb = 0f, float detectorQuality = 100f,
                bool immediateState = false)
            {
                if (_count > 0)
                {
                    var last = GetFromNewest(0);
                    if (Math.Abs(last.NoteTime - noteTime) <= 1e-6)
                    {
                        last.ChordTime = chordTime;
                        Array.Copy(levels, last.Levels, last.Levels.Length);
                        last.Onset = onset;
                        last.ChordChanged = chordChanged;
                        last.ChordNew = chordNew;
                        last.QualityFlags = qualityFlags;
                        last.CandidateCount = candidateCount;
                        Array.Clear(last.CandidateNotes, 0, last.CandidateNotes.Length);
                        Array.Clear(last.CandidatePitches, 0, last.CandidatePitches.Length);
                        if (candidateNotes is not null)
                        {
                            Array.Copy(candidateNotes, last.CandidateNotes,
                                Math.Min(candidateNotes.Length, last.CandidateNotes.Length));
                        }
                        if (candidatePitches is not null)
                        {
                            Array.Copy(candidatePitches, last.CandidatePitches,
                                Math.Min(candidatePitches.Length, last.CandidatePitches.Length));
                        }
                        last.RootNote = rootNote;
                        last.RootPitch = rootPitch;
                        last.PitchTolerance = pitchTolerance;
                        last.Peak = peak;
                        last.LevelDb = levelDb;
                        last.FrameId = frameId;
                        last.DetectorLevelDb = detectorLevelDb;
                        last.DetectorQuality = detectorQuality;
                        last.ImmediateState = immediateState;
                        return;
                    }

                    if (noteTime < last.NoteTime)
                    {
                        return;
                    }
                }

                if (_count == Capacity)
                {
                    _start = (_start + 1) % Capacity;
                    _count--;
                }

                int index = (_start + _count) % Capacity;
                var frame = _frames[index] ??= new ChordFrame();
                frame.NoteTime = noteTime;
                frame.ChordTime = chordTime;
                frame.Onset = onset;
                frame.ChordChanged = chordChanged;
                frame.ChordNew = chordNew;
                frame.QualityFlags = qualityFlags;
                frame.CandidateCount = candidateCount;
                if (candidateNotes is not null)
                {
                    Array.Copy(candidateNotes, frame.CandidateNotes,
                        Math.Min(candidateNotes.Length, frame.CandidateNotes.Length));
                }
                if (candidatePitches is not null)
                {
                    Array.Copy(candidatePitches, frame.CandidatePitches,
                        Math.Min(candidatePitches.Length, frame.CandidatePitches.Length));
                }
                frame.RootNote = rootNote;
                frame.RootPitch = rootPitch;
                frame.PitchTolerance = pitchTolerance;
                frame.Peak = peak;
                frame.LevelDb = levelDb;
                frame.FrameId = frameId;
                frame.DetectorLevelDb = detectorLevelDb;
                frame.DetectorQuality = detectorQuality;
                frame.ImmediateState = immediateState;
                Array.Copy(levels, frame.Levels, frame.Levels.Length);
                _count++;
            }

            public ChordFrame GetFromNewest(int offset)
            {
                int index = (_start + _count - 1 - offset) % Capacity;
                if (index < 0)
                {
                    index += Capacity;
                }

                return _frames[index]!;
            }

        }
    }
}
