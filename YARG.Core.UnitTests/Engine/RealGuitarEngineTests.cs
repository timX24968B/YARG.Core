using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.RealGuitar;
using YARG.Core.Input;

namespace YARG.Core.UnitTests.Engine;

public class RealGuitarEngineTests
{
    private const double TICKS_PER_SEC = 960.0;

    private static readonly int[][] PitchCases =
    {
        new[] { 0, 0, 40 },
        new[] { 1, 3, 48 },
        new[] { 2, 5, 55 },
        new[] { 3, 12, 67 },
        new[] { 4, 7, 66 },
        new[] { 5, 22, 86 },
    };

    [TestCaseSource(nameof(PitchCases))]
    public void MidiNote_UsesStandardTuning(int stringIndex, int fret, int expectedMidi)
    {
        Assert.That(RealGuitarEngine.GetMidiNote(stringIndex, fret), Is.EqualTo(expectedMidi));
    }

    [Test]
    public void MidiNote_RejectsInvalidString()
    {
        Assert.That(RealGuitarEngine.GetMidiNote(-1, 0), Is.EqualTo(-1));
        Assert.That(RealGuitarEngine.GetMidiNote(6, 0), Is.EqualTo(-1));
    }

    [Test]
    public void TuningName_RecognizesCommonTunings()
    {
        Assert.That(RealGuitarEngine.GetTuningName(new[] { 40, 45, 50, 55, 59, 64 }),
            Is.EqualTo("E Standard"));
        Assert.That(RealGuitarEngine.GetTuningName(new[] { 38, 43, 48, 53, 57, 62 }),
            Is.EqualTo("D Standard"));
        Assert.That(RealGuitarEngine.GetTuningName(new[] { 38, 45, 50, 55, 59, 64 }),
            Is.EqualTo("Drop D"));
        Assert.That(RealGuitarEngine.GetTuningName(new[] { 34, 41, 46, 51, 55, 60 }),
            Is.EqualTo("Drop B Flat"));
        Assert.That(RealGuitarEngine.GetTuningName(new[] { 37, 44, 49, 54, 58, 63 }),
            Is.EqualTo("Drop C#"));
    }

    [Test]
    public void TuningName_SpellsOutFlats()
    {
        Assert.That(RealGuitarEngine.GetTuningName(new[] { 39, 44, 49, 54, 58, 63 }),
            Is.EqualTo("E Flat Standard"));
        Assert.That(RealGuitarEngine.GetTuningName(new[] { 37, 42, 47, 52, 56, 61 }),
            Is.EqualTo("C# Standard"));
        Assert.That(RealGuitarEngine.GetTuningName(new[] { 34, 39, 44, 49, 53, 58 }),
            Is.EqualTo("B Flat Standard"));
    }

    [Test]
    public void TuningName_RecognizesOffsetTunings()
    {
        Assert.That(RealGuitarEngine.GetTuningName(new[] { 0, 0, 0, 0, 0, 0 }),
            Is.EqualTo("E Standard"));
        Assert.That(RealGuitarEngine.GetTuningName(new[] { -4, -4, -4, -4, -4, -4 }),
            Is.EqualTo("C Standard"));
    }

    [Test]
    public void TuningName_ReturnsNullForCustomTuning()
    {
        Assert.That(RealGuitarEngine.GetTuningName(new[] { 40, 45, 50, 55, 59, 65 }),
            Is.Null);
        Assert.That(RealGuitarEngine.GetTuningName(null), Is.Null);
    }

    [Test]
    public void PitchMatch_AllowsQuarterToneTolerance()
    {
        var note = Note(ProGuitarString.Green, 3, 960);

        Assert.That(RealGuitarEngine.IsPitchMatch(48f, note), Is.True);
        Assert.That(RealGuitarEngine.IsPitchMatch(48.5f, note), Is.True);
        Assert.That(RealGuitarEngine.IsPitchMatch(47.4f, note), Is.True);
        Assert.That(RealGuitarEngine.IsPitchMatch(47.1f, note), Is.False);
        Assert.That(RealGuitarEngine.IsPitchMatch(float.NaN, note), Is.False);
    }

    [Test]
    public void Attack_HitsSingleNoteWithMatchingPitch()
    {
        var note = Note(ProGuitarString.Green, 3, 960);
        var engine = CreateEngine(note);

        engine.Update(0.0);
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(note.WasHit, Is.True);
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(1));
            Assert.That(engine.EngineStats.Combo, Is.EqualTo(1));
            Assert.That(engine.EngineStats.NoteScore, Is.EqualTo(60));
        }
    }

    [Test]
    public void EarlyAttack_IsKeptForTheHitWindow()
    {
        var note = Note(ProGuitarString.Green, 3, 960);
        var engine = CreateEngine(note);

        engine.Update(0.0);
        QueuePitchAndAttack(engine, 0.85, 48);
        engine.Update(0.85);

        Assert.That(note.WasHit, Is.False);

        engine.Update(0.99);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void JudgementOffset_ExtendsTheLateWindow()
    {
        // 0.2 s total window => +/-0.1 s around the note at 1.0 s.
        var offsetNote = Note(ProGuitarString.Green, 3, 960);
        var offsetEngine = CreateEngineWithOffset(-0.05, 0.2, offsetNote);
        offsetEngine.Update(0.0);
        QueuePitchAndAttack(offsetEngine, 1.15, 48);
        offsetEngine.Update(1.15);
        Assert.That(offsetNote.WasHit, Is.True,
            "The -50 ms latency offset should keep a 150 ms detector frame inside the late window.");

        var normalNote = Note(ProGuitarString.Green, 3, 960);
        var normalEngine = CreateEngineWithWindow(0.2, normalNote);
        normalEngine.Update(0.0);
        QueuePitchAndAttack(normalEngine, 1.15, 48);
        normalEngine.Update(1.15);
        Assert.That(normalNote.WasHit, Is.False,
            "Without the offset the same frame is past the 100 ms late edge.");
    }

    [Test]
    public void JudgementOffset_CompensatesFiftyMillisecondsOfInputLatency()
    {
        // Rocksmith assumes the input arrives 50 ms after the chart time.
        var offsetNote = Note(ProGuitarString.Green, 3, 960);
        var offsetEngine = CreateEngineWithOffset(-0.05, 0.2, offsetNote);
        offsetEngine.Update(0.0);
        QueuePitchAndAttack(offsetEngine, 1.05, 48);
        offsetEngine.Update(1.05);
        Assert.That(offsetNote.WasHit, Is.True);
        Assert.That(offsetEngine.EngineStats.GetOffsetSamples().Last(),
            Is.EqualTo(0.0).Within(1e-6),
            "The -50 ms offset evaluates a 50 ms-late input at the 1.0 s chart time.");

        var normalNote = Note(ProGuitarString.Green, 3, 960);
        var normalEngine = CreateEngineWithWindow(0.2, normalNote);
        normalEngine.Update(0.0);
        QueuePitchAndAttack(normalEngine, 1.05, 48);
        normalEngine.Update(1.05);
        Assert.That(normalEngine.EngineStats.GetOffsetSamples().Last(),
            Is.EqualTo(0.05).Within(1e-6),
            "Without the offset the same input is recorded 50 ms late.");
    }

    [Test]
    public void JudgementOffset_DefaultIsMinusFiftyMilliseconds()
    {
        Assert.That(RealGuitarEngine.REAL_GUITAR_JUDGEMENT_OFFSET_SECONDS,
            Is.EqualTo(-0.050).Within(1e-9));
    }

    [Test]
    public void Attack_WaitsForCorrectedPitchAfterOnset()
    {
        var note = Note(ProGuitarString.Green, 3, 960); // C3, 48
        var engine = CreateEngine(note);
        engine.Update(0.0);

        // The onset note is wrong, but the corrected pitch arrives 100 ms
        // later.  The attack must remain pending long enough to use it.
        var earlyAttack = GameInput.Create(0.95, RealGuitarAction.Attack, 400); // E2
        engine.QueueInput(ref earlyAttack);
        engine.Update(0.95);

        Assert.That(note.WasHit, Is.False);

        var correctedPitch = GameInput.Create(1.05, RealGuitarAction.Pitch, 480); // C3
        engine.QueueInput(ref correctedPitch);
        engine.Update(1.05);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void WrongPitch_DoesNotHitNote()
    {
        var note = Note(ProGuitarString.Green, 3, 960);
        var engine = CreateEngine(note);

        engine.Update(0.0);
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);
        engine.Update(1.1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(note.WasHit, Is.False);
            Assert.That(engine.EngineStats.NotesHit, Is.Zero);
        }
    }

    [Test]
    public void Chord_IsHitWhenOneDetectedPitchMatches()
    {
        var root = Note(ProGuitarString.Red, 0, 960);
        var child = Note(ProGuitarString.Green, 2, 960);
        root.AddChildNote(child);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // A string, fret 2 = MIDI 47. The low E note is not detected by a
        // monophonic analyzer, but the chord is still accepted.
        QueuePitchAndAttack(engine, 1.0, 47);
        engine.Update(1.0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.WasHit, Is.True);
            Assert.That(child.WasHit, Is.True);
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(2));
            Assert.That(engine.EngineStats.TotalNotes, Is.EqualTo(2));
        }
    }

    [Test]
    public void Sustain_EndsWhenPitchChanges()
    {
        var note = Note(ProGuitarString.Green, 3, 960, 1920);
        var engine = CreateEngine(note);

        engine.Update(0.0);
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);

        Assert.That(engine.HasActiveSustains, Is.True);

        var newPitch = GameInput.Create(1.5, RealGuitarAction.Pitch, 400);
        engine.QueueInput(ref newPitch);
        engine.Update(1.5);

        Assert.That(engine.HasActiveSustains, Is.False);
    }

    [Test]
    public void ChordMask_RequiresTheChartChordTones()
    {
        var root = Note(ProGuitarString.Red, 0, 960);
        var fifth = Note(ProGuitarString.Green, 2, 960);
        var octave = Note(ProGuitarString.Orange, 2, 960);
        root.AddChildNote(fifth);
        root.AddChildNote(octave);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // Root, fifth, and octave are all present in the audio mask.
        QueueChordMask(engine, 0.99, NoteMask(40, 47, 52));
        QueuePitchAndAttack(engine, 1.0, 52);
        engine.Update(1.0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.WasHit, Is.True);
            Assert.That(fifth.WasHit, Is.True);
            Assert.That(octave.WasHit, Is.True);
        }
    }

    [Test]
    public void ChordMask_RejectsWrongChordSharingRootAndThird()
    {
        // C major: C3, E3, G3.
        var root = Note(ProGuitarString.Green, 3, 960);
        var third = Note(ProGuitarString.Orange, 2, 960);
        var fifth = Note(ProGuitarString.Blue, 0, 960);
        root.AddChildNote(third);
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // A minor contains C and E, but no G.  The loose matcher used to
        // accept this as C major because the fifth was optional.
        QueueChordMask(engine, 0.99, NoteMask(45, 48, 52));
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False);

        // Am7 contains every C major tone plus an unrelated low A.  It must
        // not be accepted just because the C-major pitch classes are present.
        QueueChordMask(engine, 1.03, NoteMask(45, 48, 52, 55));
        engine.Update(1.03);

        Assert.That(root.WasHit, Is.False);

        // The real C major voicing is accepted by the strict attack matcher.
        QueueChordMask(engine, 1.05, NoteMask(48, 52, 55));
        engine.Update(1.05);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void StrongUnrelatedChordTone_IsRejected()
    {
        var root = Note(ProGuitarString.Green, 3, 960); // C3
        var third = Note(ProGuitarString.Orange, 2, 960); // E3
        var fifth = Note(ProGuitarString.Blue, 0, 960); // G3
        root.AddChildNote(third);
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // The stable mask contains C/E/G, but a strong A is also present.
        // The strict matcher must reject the extra unrelated tone.
        QueueChordMask(engine, 0.99, NoteMask(48, 52, 55));
        QueueStrongMask(engine, 0.99, NoteMask(48, 52, 55, 57));
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False);

        QueueChordMask(engine, 1.05, NoteMask(48, 52, 55));
        QueueStrongMask(engine, 1.05, NoteMask(48, 52, 55));
        engine.Update(1.05);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void Sustain_RequiresAllSmallChordTones()
    {
        var root = Note(ProGuitarString.Green, 3, 960, 1920); // C3
        var third = Note(ProGuitarString.Orange, 2, 960, 1920); // E3
        var fifth = Note(ProGuitarString.Blue, 0, 960, 1920); // G3
        root.AddChildNote(third);
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(48, 52, 55));
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);
        Assert.That(root.WasHit, Is.True);

        // The fifth is not optional for a three-note chord, even while
        // sustaining.  A C/E dyad must not keep C major alive.
        QueueChordMask(engine, 1.20, NoteMask(48, 52));
        engine.Update(1.20);

        Assert.That(engine.HasActiveSustains, Is.False);
    }

    [Test]
    public void Sustain_AllowsOneMissingToneOnLargeChord()
    {
        var root = Note(ProGuitarString.Green, 3, 960, 1920); // C3, 48
        var third = Note(ProGuitarString.Orange, 2, 960, 1920); // E3, 52
        var fifth = Note(ProGuitarString.Blue, 0, 960, 1920); // G3, 55
        var seventh = Note(ProGuitarString.Yellow, 1, 960, 1920); // C4, 60
        var ninth = Note(ProGuitarString.Yellow, 0, 960, 1920); // B3, 59

        root.AddChildNote(third);
        root.AddChildNote(fifth);
        root.AddChildNote(seventh);
        root.AddChildNote(ninth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(48, 52, 55, 59, 60));
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);
        Assert.That(root.WasHit, Is.True);

        // One missing upper tone is allowed by the loose five/six-note path.
        QueueChordMask(engine, 1.20, NoteMask(48, 52, 55, 59));
        engine.Update(1.20);

        Assert.That(engine.HasActiveSustains, Is.True);
    }

    [Test]
    public void ChordCandidates_IgnoreHarmonicsAboveChartNotes()
    {
        var root = Note(ProGuitarString.Green, 3, 960); // C3, 48
        var third = Note(ProGuitarString.Orange, 2, 960); // E3, 52
        var fifth = Note(ProGuitarString.Blue, 0, 960); // G3, 55
        root.AddChildNote(third);
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // The top-N list contains legitimate overtones of the chart root:
        // C4 (C3+12), G4 (C3+19), and Bb5 (C3+34).  They must not count as
        // unmatched chord energy or a bright guitar would reject a correct
        // chord.
        QueueChordMask(engine, 0.99, NoteMask(48, 52, 55));
        QueueCandidates(engine, 0.99,
            (48, 255), (52, 255), (55, 255),
            (60, 80), (67, 70), (82, 60));
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.WasHit, Is.True);
            Assert.That(engine.LastChordUnmatchedLevel, Is.Zero);
        }
    }

    [Test]
    public void ChordCandidates_RejectMajorSeventhExtension()
    {
        var root = Note(ProGuitarString.Green, 3, 960); // C3, 48
        var third = Note(ProGuitarString.Orange, 2, 960); // E3, 52
        var fifth = Note(ProGuitarString.Blue, 0, 960); // G3, 55
        root.AddChildNote(third);
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // B3 is one semitone below C4 but is an unrelated major-seventh
        // extension against C major.  Only spill around the low chart root
        // should be tolerated, not spill around every chord tone.
        QueueChordMask(engine, 0.99, NoteMask(48, 52, 55));
        QueueCandidates(engine, 0.99,
            (48, 255), (52, 255), (55, 255), (59, 220));
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False);
    }

    [Test]
    public void ChordCandidates_RejectStrongHarmonicExtra()
    {
        var root = Note(ProGuitarString.Green, 3, 960); // C3, 48
        var third = Note(ProGuitarString.Orange, 2, 960); // E3, 52
        var fifth = Note(ProGuitarString.Blue, 0, 960); // G3, 55
        root.AddChildNote(third);
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // D5 is a physical harmonic of G3, but a loud independent D is still
        // an unrelated chord tone.  Harmonic tolerance must not blanket-ignore
        // strong extras.
        QueueChordMask(engine, 0.99, NoteMask(48, 52, 55));
        QueueCandidates(engine, 0.99,
            (48, 255), (52, 255), (55, 255), (74, 220));
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False);
    }

    [Test]
    public void ChordCandidates_RejectLoudUnrelatedExtension()
    {
        var root = Note(ProGuitarString.Green, 3, 960); // C3
        var third = Note(ProGuitarString.Orange, 2, 960); // E3
        var fifth = Note(ProGuitarString.Blue, 0, 960); // G3
        root.AddChildNote(third);
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(48, 52, 55));
        QueueCandidates(engine, 0.99, (48, 255), (52, 255), (55, 255), (57, 220));
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.WasHit, Is.False);
            Assert.That(engine.LastChordUnmatchedLevel, Is.GreaterThan(0.5));
            Assert.That(engine.LastChordCoverage, Is.LessThan(0.85));
        }

        QueueCandidates(engine, 1.05, (48, 255), (52, 255), (55, 255));
        engine.Update(1.05);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.WasHit, Is.True);
            Assert.That(engine.LastChordUnmatchedLevel, Is.Zero);
            Assert.That(engine.LastChordCoverage, Is.EqualTo(1.0));
        }
    }

    [Test]
    public void ChordCandidates_RequirePerVoiceCoverage()
    {
        var root = Note(ProGuitarString.Green, 3, 960); // C3
        var third = Note(ProGuitarString.Orange, 2, 960); // E3
        var fifth = Note(ProGuitarString.Blue, 0, 960); // G3
        root.AddChildNote(third);
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // The stable pitch-class mask has all three notes, but the top-N
        // candidate list proves that only two chart voices are present.
        QueueChordMask(engine, 0.99, NoteMask(48, 52, 55));
        QueueCandidates(engine, 0.99, (48, 255), (52, 255));
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.WasHit, Is.False);
            Assert.That(engine.LastChordVoicingCoverage, Is.LessThan(1.0));
        }

        QueueCandidates(engine, 1.05, (48, 255), (52, 255), (55, 255));
        engine.Update(1.05);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void ChordCandidates_DoNotRequireDuplicateOctaves()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        var octave = Note(ProGuitarString.Orange, 2, 960); // E3, 52
        root.AddChildNote(Note(ProGuitarString.Blue, 4, 960)); // B3, 59
        root.AddChildNote(octave);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // The octave E3 is missing, but both unique pitch classes (E and B)
        // are present.  A per-voice check must not make the duplicate octave
        // mandatory and regress power chords.
        QueueChordMask(engine, 0.99, NoteMask(40, 59));
        QueueCandidates(engine, 0.99, (40, 255), (59, 255));
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void ChordCandidates_HarmonicOctaveCanSatisfyVoice()
    {
        var root = Note(ProGuitarString.Green, 3, 960); // C3, 48
        var third = Note(ProGuitarString.Orange, 2, 960); // E3, 52
        var fifth = Note(ProGuitarString.Blue, 0, 960); // G3, 55
        root.AddChildNote(third);
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // The G fundamental is not in the top list, but its octave is.
        QueueChordMask(engine, 0.99, NoteMask(48, 52, 55));
        QueueCandidates(engine, 0.99, (48, 255), (52, 255), (67, 220));
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void OctaveDoubleStop_AcceptsRootAndOctave()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        var octave = Note(ProGuitarString.Orange, 2, 960); // E3, 52
        root.AddChildNote(octave);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40, 52));
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.WasHit, Is.True);
            Assert.That(octave.WasHit, Is.True);
            Assert.That(engine.LastChordVoicingCoverage, Is.EqualTo(1.0));
        }
    }

    [Test]
    public void RootQuality_UsesRocksmithTemplateCoverage()
    {
        Assert.That(RealGuitarEngine.IsRocksmithRootQuality(40, 1UL << 40, 0),
            Is.True);

        Assert.That(RealGuitarEngine.IsRocksmithRootQuality(52, 1UL << 52, 0),
            Is.False);
        Assert.That(RealGuitarEngine.IsRocksmithRootQuality(52,
            1UL << 52 | 1UL << 33, 0), Is.True);

        Assert.That(RealGuitarEngine.IsRocksmithRootQuality(64, 0, 1UL),
            Is.False);
        Assert.That(RealGuitarEngine.IsRocksmithRootQuality(64, 1UL << 26,
            (1UL << 0) | (1UL << 17) | (1UL << 28) | (1UL << 34)),
            Is.True);
    }

    [Test]
    public void RootQuality_UsesCandidateTemplateCoverage()
    {
        var levels = new float[128];
        levels[52] = 1f;
        Assert.That(RealGuitarEngine.IsRocksmithRootQuality(52, levels),
            Is.False);

        levels[33] = 1f;
        Assert.That(RealGuitarEngine.IsRocksmithRootQuality(52, levels),
            Is.True);
    }

    [Test]
    public void FractionalPitchTolerance_MatchesSourceWindow()
    {
        Assert.That(RealGuitarEngine.MatchesFractionalPitch(40, 40.49f),
            Is.True);
        Assert.That(RealGuitarEngine.MatchesFractionalPitch(40, 40.56f),
            Is.False);
        Assert.That(RealGuitarEngine.MatchesFractionalPitch(40, 39.9f),
            Is.False);
        Assert.That(RealGuitarEngine.MatchesFractionalPitch(40, 40f),
            Is.True);
    }

    [Test]
    public void FractionalPitchTolerance_UsesBendAndTuningContext()
    {
        Assert.That(RealGuitarEngine.GetFractionalPitchTolerance(40, 0, 0),
            Is.EqualTo(0.05f).Within(1e-5f));
        Assert.That(RealGuitarEngine.GetFractionalPitchTolerance(40, 2, 40),
            Is.EqualTo(0.15f).Within(1e-5f));
        Assert.That(RealGuitarEngine.GetFractionalPitchTolerance(80, 2, 40),
            Is.EqualTo(0.05f).Within(1e-5f));
    }

    [Test]
    public void DetectedNoteCount_IgnoresRootTemplateHarmonics()
    {
        var levels = new float[128];
        levels[64] = 1f; // root
        levels[45] = 1f; // root-template harmonic
        levels[40] = 1f; // unrelated note

        Assert.That(RealGuitarEngine.RocksmithTemplateIndex(64, 45),
            Is.GreaterThanOrEqualTo(0));
        Assert.That(RealGuitarEngine.RocksmithDetectedNoteCount(levels, 64),
            Is.EqualTo(2));
    }

    [Test]
    public void DetectorLevelEnvelope_MatchesRocksmithConfidenceFormula()
    {
        float maxDb = RocksmithDetectorConfidence.INITIAL_MAX_DB;
        float minDb = RocksmithDetectorConfidence.INITIAL_MIN_DB;

        RocksmithDetectorConfidence.UpdateLevelEnvelope(ref maxDb, ref minDb,
            -20f, 0.01, out float quality, out float floorQuality);
        Assert.That(maxDb, Is.EqualTo(-20f).Within(1e-5f));
        Assert.That(minDb, Is.EqualTo(-20f).Within(1e-5f));
        Assert.That(quality, Is.Zero.Within(1e-5f));
        Assert.That(floorQuality, Is.Zero.Within(1e-5f));

        RocksmithDetectorConfidence.UpdateLevelEnvelope(ref maxDb, ref minDb,
            -10f, 0.01, out quality, out floorQuality);
        Assert.That(quality, Is.EqualTo(100f).Within(1e-4f));
        Assert.That(floorQuality, Is.EqualTo(99.8f).Within(0.2f));
        Assert.That(RocksmithDetectorConfidence.IsFrameLive(maxDb, quality),
            Is.True);

        RocksmithDetectorConfidence.UpdateLevelEnvelope(ref maxDb, ref minDb,
            -30f, 0.01, out quality, out floorQuality);
        Assert.That(quality, Is.Zero.Within(1e-4f));
        Assert.That(floorQuality, Is.Zero.Within(1e-4f));
        Assert.That(RocksmithDetectorConfidence.IsFrameLive(maxDb, quality),
            Is.False);
    }

    [Test]
    public void NoteCandidates_UseStrictLocalMaximaAndDescendingOrder()
    {
        var levels = new float[128];
        levels[40] = 10f;
        levels[41] = 5f;
        levels[42] = 8f;
        levels[44] = 4f;
        levels[45] = 3f;

        Span<int> notes = stackalloc int[20];
        Span<float> values = stackalloc float[20];
        int count = RocksmithNoteCandidates.FindLocalMaxima(levels, 24, 100,
            useNoiseFloor: false, notes, values);

        Assert.That(count, Is.EqualTo(3));
        Assert.That(notes[0], Is.EqualTo(40));
        Assert.That(values[0], Is.EqualTo(10f));
        Assert.That(notes[1], Is.EqualTo(42));
        Assert.That(values[1], Is.EqualTo(8f));
        Assert.That(notes[2], Is.EqualTo(44));
        Assert.That(values[2], Is.EqualTo(4f));

        count = RocksmithNoteCandidates.FindLocalMaxima(levels, 24, 100,
            useNoiseFloor: true, notes, values);
        Assert.That(count, Is.EqualTo(2));
        Assert.That(notes[0], Is.EqualTo(40));
        Assert.That(notes[1], Is.EqualTo(42));

        // Rocksmith gathers all maxima before sorting, so a high note that is
        // discovered late must still make the strongest-20 list.
        levels[99] = 100f;
        count = RocksmithNoteCandidates.FindLocalMaxima(levels, 24, 100,
            useNoiseFloor: true, notes, values);
        Assert.That(count, Is.EqualTo(3));
        Assert.That(notes[0], Is.EqualTo(99));
        Assert.That(values[0], Is.EqualTo(100f));
    }

    [Test]
    public void NoteCandidates_KeepTopTwentyAfterSorting()
    {
        var levels = new float[128];
        for (int i = 0; i < 30; i++)
        {
            int note = 24 + i * 2;
            levels[note] = 100f - i;
        }

        Span<int> notes = stackalloc int[20];
        Span<float> values = stackalloc float[20];
        int count = RocksmithNoteCandidates.FindLocalMaxima(levels, 24, 100,
            useNoiseFloor: false, notes, values);

        Assert.That(count, Is.EqualTo(20));
        Assert.That(notes[0], Is.EqualTo(24));
        Assert.That(values[0], Is.EqualTo(100f));
        Assert.That(notes[19], Is.EqualTo(62));
    }

    [Test]
    public void NoteCandidates_IncludeMidi127Boundary()
    {
        var levels = new float[128];
        levels[101] = 10f;
        levels[110] = 20f;
        levels[126] = 30f;
        levels[127] = 40f;

        Span<int> notes = stackalloc int[20];
        Span<float> values = stackalloc float[20];
        int count = RocksmithNoteCandidates.FindLocalMaxima(levels, 24, 127,
            useNoiseFloor: false, notes, values);

        Assert.That(count, Is.EqualTo(3));
        Assert.That(notes[0], Is.EqualTo(127));
        Assert.That(notes.ToArray()[..count], Does.Contain(110));
        Assert.That(notes.ToArray()[..count], Does.Contain(101));
    }

    [Test]
    public void NoteCandidates_RejectNearEqualLowPlateau()
    {
        var levels = new float[128];
        levels[40] = 100f;
        levels[39] = 101f; // source requires strict local maxima
        levels[41] = 99f;
        levels[70] = 80f;
        levels[71] = 100f;

        Span<int> notes = stackalloc int[20];
        Span<float> values = stackalloc float[20];
        int count = RocksmithNoteCandidates.FindLocalMaxima(levels, 24, 100,
            useNoiseFloor: false, notes, values);

        // MIDI 40 is not a local maximum because 39 is louder.  MIDI 41 is
        // also rejected because 40 is louder.  Both high-note conditions are
        // strict as in FUN_0149f035.
        Assert.That(notes.ToArray()[..count], Does.Not.Contain(40));
        Assert.That(notes.ToArray()[..count], Does.Not.Contain(41));
        Assert.That(notes.ToArray()[..count], Does.Not.Contain(70));
        Assert.That(notes.ToArray()[..count], Does.Contain(39));
        Assert.That(notes.ToArray()[..count], Does.Contain(71));
    }

    [Test]
    public void NormalOnset_RequiresSilenceToSoundTransition()
    {
        Assert.That(RocksmithDetectorConfidence.IsNormalOnset(-20f, -160f,
            hasPreviousFrame: false), Is.True);
        Assert.That(RocksmithDetectorConfidence.IsNormalOnset(-20f, -70f,
            hasPreviousFrame: true), Is.True);
        Assert.That(RocksmithDetectorConfidence.IsNormalOnset(-20f, -30f,
            hasPreviousFrame: true), Is.False);
        Assert.That(RocksmithDetectorConfidence.IsNormalOnset(-60f, -70f,
            hasPreviousFrame: true), Is.False);
        Assert.That(RocksmithDetectorConfidence.IsNormalOnset(-20f, -20f,
            hasPreviousFrame: true), Is.False);
    }

    [Test]
    public void StrongAttackFluxRise_UsesFiveFrameMedianAndThirtyThreshold()
    {
        Span<float> history = stackalloc float[
            RocksmithDetectorConfidence.STRONG_HISTORY];
        int index = 0;
        float previousRise = 0f;

        float rise = RocksmithDetectorConfidence.UpdateStrongFluxRise(100f,
            history, ref index, ref previousRise, out bool strong);
        Assert.That(rise, Is.EqualTo(100f));
        Assert.That(strong, Is.True);
        Assert.That(previousRise, Is.EqualTo(100f));

        // The median of [0,0,0,0,100] is still zero, but the previous rise was
        // already above the threshold, so this is not a new crossing.
        rise = RocksmithDetectorConfidence.UpdateStrongFluxRise(100f,
            history, ref index, ref previousRise, out strong);
        Assert.That(rise, Is.EqualTo(100f));
        Assert.That(strong, Is.False);

        history.Clear();
        index = 0;
        previousRise = 0f;
        rise = RocksmithDetectorConfidence.UpdateStrongFluxRise(29.75f,
            history, ref index, ref previousRise, out strong);
        Assert.That(rise, Is.EqualTo(29.75f));
        Assert.That(strong, Is.False);

        rise = RocksmithDetectorConfidence.UpdateStrongFluxRise(30.25f,
            history, ref index, ref previousRise, out strong);
        Assert.That(rise, Is.EqualTo(30.25f));
        Assert.That(strong, Is.True);
    }

    [Test]
    public void DetectedNoteCount_PreservesFractionalLevels()
    {
        var levels = new float[128];
        levels[64] = 100f;   // root
        levels[65] = 30.01f; // above the 30% cutoff
        levels[67] = 29.99f; // below the 30% cutoff

        Assert.That(RealGuitarEngine.RocksmithDetectedNoteCount(levels, 64),
            Is.EqualTo(2));
    }

    [Test]
    public void OctaveDoubleStop_FullFrameRequiresBothNotes()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        var octave = Note(ProGuitarString.Orange, 2, 960); // E3, 52
        root.AddChildNote(octave);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueFrameWithMeta(engine, 0.995, new[] { 40 },
            onset: true, chordChanged: true);
        engine.Update(1.0);
        Assert.That(root.WasHit, Is.False);

        QueueFrameWithMeta(engine, 1.0, new[] { 40, 52 },
            onset: false, chordChanged: true);
        engine.Update(1.0);
        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void OctaveDoubleStop_RejectsUnrelatedStrongTone()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        var octave = Note(ProGuitarString.Orange, 2, 960); // E3, 52
        root.AddChildNote(octave);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40, 52));
        QueueStrongMask(engine, 0.99, NoteMask(40, 52, 48)); // unrelated C3
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False);
    }

    [Test]
    public void ChordState_IsExposedToGameplay()
    {
        var root = Note(ProGuitarString.Red, 0, 960);
        var fifth = Note(ProGuitarString.Green, 2, 960);
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueueChordState(engine, 0.99, changed: true, newChord: true, ageFrames: 0);
        QueuePitchAndAttack(engine, 1.0, 47);
        engine.Update(1.0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.WasHit, Is.True);
            Assert.That(engine.LastChordChanged, Is.True);
            Assert.That(engine.LastChordNew, Is.True);
            Assert.That(engine.LastChordAgeFrames, Is.Zero);
        }
    }

    [Test]
    public void Hopo_AcceptsConfirmedPitchChangeWithoutOnset()
    {
        var note = new ProGuitarNote(
            ProGuitarString.Green,
            3,
            ProGuitarNoteType.Hopo,
            ProGuitarNoteFlags.None,
            NoteFlags.None,
            1.0,
            0,
            960,
            0);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        var pitch = GameInput.Create(0.99, RealGuitarAction.Pitch, 480);
        var pitchChange = GameInput.Create(0.99, RealGuitarAction.PitchChange, 480);
        engine.QueueInput(ref pitch);
        engine.QueueInput(ref pitchChange);
        engine.Update(0.99);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void HopoNote_UsesSourceLevelRiseWithoutChangedFrame()
    {
        var note = new ProGuitarNote(
            ProGuitarString.Green,
            3,
            ProGuitarNoteType.Hopo,
            ProGuitarNoteFlags.None,
            NoteFlags.None,
            1.0,
            0,
            960,
            0);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMaskNotes(engine, 0.99, new[] { 48 });
        QueueCandidates(engine, 0.99, (48, 80));
        QueueFloatNoteLevels(engine, 0.99, (48, 80f));
        QueueFrameMeta(engine, 0.99, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f);
        engine.Update(0.99);
        QueueChordMaskNotes(engine, 1.0, new[] { 48 });
        QueueCandidates(engine, 1.0, (48, 220));
        QueueFloatNoteLevels(engine, 1.0, (48, 220f));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void TapNote_UsesSourceLevelRiseWithoutChangedFrame()
    {
        var note = new ProGuitarNote(
            ProGuitarString.Green,
            3,
            ProGuitarNoteType.Tap,
            ProGuitarNoteFlags.None,
            NoteFlags.None,
            1.0,
            0,
            960,
            0);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMaskNotes(engine, 0.99, new[] { 48 });
        QueueCandidates(engine, 0.99, (48, 80));
        QueueFloatNoteLevels(engine, 0.99, (48, 80f));
        QueueFrameMeta(engine, 0.99, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f);
        engine.Update(0.99);
        QueueChordMaskNotes(engine, 1.0, new[] { 48 });
        QueueCandidates(engine, 1.0, (48, 220));
        QueueFloatNoteLevels(engine, 1.0, (48, 220f));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void HopoNote_RejectsDetectorLevelBelowSourceGate()
    {
        var note = new ProGuitarNote(
            ProGuitarString.Green, 3, ProGuitarNoteType.Hopo,
            ProGuitarNoteFlags.None, NoteFlags.None, 1.0, 0, 960, 0);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMaskNotes(engine, 1.0, new[] { 48 });
        QueueCandidates(engine, 1.0, (48, 220));
        QueueFloatNoteLevels(engine, 1.0, (48, 220f));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f,
            detectorLevelDb: -60f, detectorQuality: 100f);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.False);
    }

    [Test]
    public void HopoNote_RejectsDetectorQualityBelowSourceGate()
    {
        var note = new ProGuitarNote(
            ProGuitarString.Green, 3, ProGuitarNoteType.Hopo,
            ProGuitarNoteFlags.None, NoteFlags.None, 1.0, 0, 960, 0);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMaskNotes(engine, 1.0, new[] { 48 });
        QueueCandidates(engine, 1.0, (48, 220));
        QueueFloatNoteLevels(engine, 1.0, (48, 220f));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f,
            detectorLevelDb: -20f, detectorQuality: 20f);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.False);
    }

    [Test]
    public void LowerCandidateTemplate_SkipsTargetAndLastTemplateEntry()
    {
        Span<int> matches = stackalloc int[6];

        bool matched = RealGuitarEngine.TrySelectRocksmithLowerCandidates(
            67, new[] { 48, 39, 33 }, matches, out int count);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(matched, Is.True);
            Assert.That(count, Is.EqualTo(3));
            Assert.That(matches[0], Is.EqualTo(48));
            Assert.That(matches[1], Is.EqualTo(39));
            Assert.That(matches[2], Is.EqualTo(33));
        }

        bool lastOnly = RealGuitarEngine.TrySelectRocksmithLowerCandidates(
            67, new[] { 29 }, matches, out int lastCount);
        Assert.That(lastOnly, Is.False);
        Assert.That(lastCount, Is.Zero);
    }

    [Test]
    public void ClippedAttack_IsNotCountedAsAGhost()
    {
        var note = Note(ProGuitarString.Green, 3, 960);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(48));
        QueueClipped(engine, 0.99, true);
        QueuePitchAndAttack(engine, 1.0, 40); // wrong pitch, clipped strum
        engine.Update(1.0);
        engine.Update(1.04);

        Assert.That(engine.EngineStats.GhostInputs, Is.Zero);

        QueueClipped(engine, 1.05, false);
        QueuePitchAndAttack(engine, 1.06, 48);
        engine.Update(1.06);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void Chord_DoesNotFallbackToSinglePitchAfterMaskWasSeen()
    {
        var root = Note(ProGuitarString.Green, 3, 960); // C3
        var third = Note(ProGuitarString.Orange, 2, 960); // E3
        var fifth = Note(ProGuitarString.Blue, 0, 960); // G3
        root.AddChildNote(third);
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // A real detector mask was seen, then the next frame went silent.
        // The old fallback would still accept the C-major chart from one
        // monophonic C pitch.
        QueueChordMask(engine, 0.99, NoteMask(48, 52, 55));
        QueueChordMask(engine, 1.0, 0);
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False);

        QueueChordMask(engine, 1.05, NoteMask(48, 52, 55));
        engine.Update(1.05);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void ChordMask_DoesNotAcceptOnlyTheRoot()
    {
        var root = Note(ProGuitarString.Red, 0, 960);
        var fifth = Note(ProGuitarString.Green, 2, 960);
        var octave = Note(ProGuitarString.Orange, 2, 960);
        root.AddChildNote(fifth);
        root.AddChildNote(octave);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40));
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.WasHit, Is.False);
            Assert.That(engine.EngineStats.GhostInputs, Is.EqualTo(0));
        }

        engine.Update(1.12);

        Assert.That(root.WasHit, Is.False);
    }

    [Test]
    public void ChordMaskWithinLookback_HitsAttack()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // The mask is 120 ms older than the attack.  REA shows Rocksmith's
        // chord query scans back to the caller's time bound (0.2 s fallback),
        // so a ringing but still valid mask must remain usable.
        QueueChordMask(engine, 0.88, NoteMask(40, 47));
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void RocksmithLevelMatcher_UsesFullFrameHistogram()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.99, (40, 220), (47, 180), (42, 120));
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void RocksmithLevelMatcher_AllowsExtraPitchClass()
    {
        var c = Note(ProGuitarString.Green, 3, 960); // C3, 48
        c.AddChildNote(Note(ProGuitarString.Orange, 2, 960)); // E3, 52
        c.AddChildNote(Note(ProGuitarString.Blue, 0, 960)); // G3, 55

        var engine = CreateEngine(c);
        engine.Update(0.0);

        // G#3 is unrelated to C major.  Rocksmith's strict matcher has no
        // explicit extra-note veto when every expected note is covered.
        QueueChordMask(engine, 0.99, NoteMask(48, 52, 55, 56));
        QueueNoteLevels(engine, 0.99,
            (48, 220), (52, 210), (55, 200), (56, 190));
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);

        Assert.That(c.WasHit, Is.True);
    }

    [Test]
    public void RocksmithLevelMatcher_RejectsIncompletePowerChord()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40));
        QueueNoteLevels(engine, 0.99, (40, 220), (42, 180));
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False);
    }

    [Test]
    public void RocksmithLevelMatcher_AcceptsHarmonicTemplate()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // E1 (28) is a lower fundamental whose third harmonic is B2 (47).
        // Rocksmith's FUN_014a734d template accepts that relation.
        QueueChordMask(engine, 0.99, NoteMask(40, 28));
        QueueNoteLevels(engine, 0.99, (40, 220), (28, 180));
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void RocksmithLevelMatcher_UsesHistoricalFrame()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // The only matching frame is 120 ms before the attack.  The engine
        // must scan history rather than requiring the newest mask.
        QueueChordMask(engine, 0.88, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.88, (40, 220), (47, 180));
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void RocksmithLevelMatcher_UsesFrameAfterAttack()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // Mark the stream as full-frame capable with an empty frame so this
        // test exercises the histogram path rather than the legacy pitch
        // fallback.
        QueueChordMask(engine, 0.5, 0);
        QueueNoteLevels(engine, 0.5);
        engine.Update(0.5);

        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);
        Assert.That(root.WasHit, Is.False);

        QueueChordMask(engine, 1.05, NoteMask(40, 47));
        QueueNoteLevels(engine, 1.05, (40, 220), (47, 180));
        engine.Update(1.05);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void WindowedChordQuery_HitsWithoutAttackAction()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.99, (40, 220), (47, 180));
        QueueFrameMeta(engine, 0.99, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(40, 47));
        QueueNoteLevels(engine, 1.0, (40, 160), (47, 120));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 0.5f, levelDb: -20f);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void SameTimestampInputBatchRunsOneHitLoop()
    {
        var note = Note(ProGuitarString.Green, 3, 960);
        var engine = CreateCountingEngine(note);
        engine.Update(0.0);
        engine.HitLoops = 0;

        QueueChordMask(engine, 0.99, NoteMask(48));
        QueueNoteLevels(engine, 0.99, (48, 220));
        QueueFrameMeta(engine, 0.99, onset: true, chordChanged: true,
            chordNew: false, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(48));
        QueueNoteLevels(engine, 1.0, (48, 180));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
        Assert.That(engine.HitLoops, Is.LessThan(20));
    }

    [Test]
    public void WindowedNoteQuery_AllowsLateHitInsideBackWindow()
    {
        var note = Note(ProGuitarString.Green, 3, 960); // C3, 48
        var engine = CreateEngineWithWindow(0.4, note);

        // Live input has already established the full-frame query channel
        // before the late frame arrives.
        QueueFrameWithMeta(engine, 0.0, Array.Empty<int>(),
            onset: true, chordChanged: false);
        engine.Update(0.0);

        QueueChordMask(engine, 1.12, NoteMask(48));
        QueueNoteLevels(engine, 1.12, (48, 220));
        QueueFrameMeta(engine, 1.12, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.18, NoteMask(48));
        QueueNoteLevels(engine, 1.18, (48, 160));
        QueueFrameMeta(engine, 1.18, onset: false, chordChanged: false,
            chordNew: false, peak: 0.6f, levelDb: -18f);
        engine.Update(1.19);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void WindowedNoteQuery_RejectsFutureAttack()
    {
        var note = Note(ProGuitarString.Green, 3, 960); // C3, 48
        var engine = CreateEngine(note);

        QueueFrameWithMeta(engine, 0.0, Array.Empty<int>(),
            onset: true, chordChanged: false);
        engine.Update(0.0);

        // A detector frame well before the chart note must not consume it,
        // even if it is otherwise a valid chord-change frame.
        QueueFrameWithMeta(engine, 0.80, new[] { 48 },
            onset: true, chordChanged: true);
        QueueFrameWithMeta(engine, 0.86, new[] { 48 },
            onset: false, chordChanged: false);
        engine.Update(0.87);

        Assert.That(note.WasHit, Is.False);
    }

    [Test]
    public void WindowedNoteQuery_MissesOutsideCallerWindow()
    {
        var note = Note(ProGuitarString.Green, 3, 960);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        engine.Update(1.51);

        Assert.That(note.WasMissed, Is.True);
    }

    [Test]
    public void WindowedNoteQuery_UsesSourceFrontAndBackBounds()
    {
        var note = Note(ProGuitarString.Green, 3, 960); // C3, 48
        var engine = CreateEngineWithWindow(0.4, note);
        engine.Update(0.0);

        // 0.79 is before the source note.Time - 0.2 lower bound, so the
        // queued 0.82/0.83 frames must not be visible yet.
        engine.Update(0.79);
        Assert.That(note.WasHit, Is.False);

        // Frames at 0.82 and 0.83 are inside the source note.Time - 0.2
        // window.  Rocksmith queries the note as soon as its front window
        // opens, so the hit is accepted before note.Time.
        QueueFrameWithMeta(engine, 0.82, new[] { 48 },
            onset: true, chordChanged: true);
        QueueFrameWithMeta(engine, 0.83, new[] { 48 },
            onset: false, chordChanged: false);
        engine.Update(0.84);
        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void WindowedChordQuery_AcceptsSourceFrontWindowBeforeNoteTime()
    {
        var root = Note(ProGuitarString.Red, 0, 960);       // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47
        var engine = CreateEngineWithWindow(0.4, root);
        engine.Update(0.0);

        // Live Rocksmith accepted a chord 144 ms before its chart time
        // (detector 18.008 for chart 18.152).  FUN_017b049a passes the
        // [note.Time - 0.2, note.Time + 0.3] bounds and FUN_014a6f01 accepts
        // the detector frame inside them; YARG must not hold the query until
        // note.Time.
        QueueFrameWithMeta(engine, 0.86, new[] { 40, 47 },
            onset: true, chordChanged: true);
        engine.Update(0.87);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void WindowedNoteQuery_RetainsSourceFrameRingAcrossDenseHistory()
    {
        var note = Note(ProGuitarString.Green, 3, 1920); // C3, 48 at t=2.0
        var engine = CreateEngineWithWindow(0.1, note);
        engine.Update(0.0);

        // Put the changed frame at the source lower bound (note 2.0 - 0.2),
        // then fill the history with ordinary detector hops.  A 32-frame ring
        // would discard the changed frame before the note enters its window.
        QueueFrameWithMeta(engine, 1.80, new[] { 48 },
            onset: true, chordChanged: true);
        for (int i = 1; i < 50; i++)
        {
            QueueFrameWithMeta(engine, 1.80 + i * 0.01, new[] { 48 },
                onset: false, chordChanged: false);
        }

        engine.Update(2.0);
        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void WindowedNoteQuery_RejectsAttackOutsideSourceFrameBound()
    {
        var note = Note(ProGuitarString.Green, 3, 960); // C3, 48
        var engine = CreateEngineWithWindow(0.4, note);
        engine.Update(0.0);

        // 0.79 is earlier than note.Time - 0.2 and must not satisfy the
        // source query bounds.
        QueueFrameWithMeta(engine, 0.79, new[] { 48 },
            onset: true, chordChanged: true);
        QueueFrameWithMeta(engine, 0.80, new[] { 48 },
            onset: false, chordChanged: false);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.False);
    }

    [Test]
    public void WindowedNoteQuery_RequiresTwoDetectionsAndChangedFrame()
    {
        var note = Note(ProGuitarString.Green, 3, 960); // C3, 48

        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(48));
        QueueNoteLevels(engine, 0.99, (48, 220));
        QueueFrameMeta(engine, 0.99, onset: true, chordChanged: true,
            chordNew: false, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(48));
        QueueNoteLevels(engine, 1.0, (48, 120));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 0.4f, levelDb: -30f);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void WindowedNoteQuery_AcceptsEarlyFrontWindowAttackBeforeNoteTime()
    {
        var note = Note(ProGuitarString.Green, 3, 960); // C3, 48
        var engine = CreateEngineWithWindow(0.4, note);
        engine.Update(0.0);

        // The pickup can be captured 50-100 ms before the chart note.  The
        // source front window is 200 ms, so the attack is consumed as soon as
        // the detector frame is available rather than being held until
        // note.Time.
        QueueFrameWithMeta(engine, 0.90, new[] { 48 },
            onset: true, chordChanged: true);
        QueueFrameWithMeta(engine, 0.91, new[] { 48 },
            onset: false, chordChanged: false);
        engine.Update(0.92);
        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void WindowedNoteQuery_OnsetDoesNotSatisfyChangedFrame()
    {
        var note = Note(ProGuitarString.Green, 3, 960); // C3, 48

        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(48));
        QueueNoteLevels(engine, 0.99, (48, 220));
        QueueFrameMeta(engine, 0.99, onset: true, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(48));
        QueueNoteLevels(engine, 1.0, (48, 180));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f);
        engine.Update(1.0);

        // FUN_014a4f20 requires the +0x7b5 changed flag; +0x795 onset alone
        // is not the note-query attack edge.
        Assert.That(note.WasHit, Is.False);
    }

    [Test]
    public void SplitTimestamps_NoteAttackGateUsesChordClock()
    {
        var note = Note(ProGuitarString.Green, 3, 960); // C3, 48
        var engine = CreateEngineWithWindow(0.4, note);
        engine.Update(0.0);

        // The changed frame is inside the note clock window but its input
        // clock timestamp is far outside the attack window.
        QueueChordTimeOffset(engine, 0.99, -1.0);
        QueueChordMask(engine, 0.99, NoteMask(48));
        QueueNoteLevels(engine, 0.99, (48, 220));
        QueueFrameMeta(engine, 0.99, onset: false, chordChanged: true,
            chordNew: false, peak: 1.0f, levelDb: -10f);

        QueueChordTimeOffset(engine, 1.0, 0.0);
        QueueChordMask(engine, 1.0, NoteMask(48));
        QueueNoteLevels(engine, 1.0, (48, 180));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f);

        engine.Update(1.0);

        Assert.That(note.WasHit, Is.False);
    }

    [Test]
    public void SplitTimestamps_AttackGateCanUseChordTimeOutsideNoteWindow()
    {
        var note = Note(ProGuitarString.Green, 3, 960); // C3, 48
        var engine = CreateEngineWithWindow(0.4, note);
        engine.Update(0.0);

        // The changed frame's note time is before the chart window, but its
        // chord/input time is inside the attack gate.  Note detections still
        // come from the later +0x720 frames.
        QueueChordTimeOffset(engine, 0.70, 0.30);
        QueueChordMask(engine, 0.70, NoteMask(48));
        QueueNoteLevels(engine, 0.70, (48, 220));
        QueueFrameMeta(engine, 0.70, onset: false, chordChanged: true,
            chordNew: false, peak: 1.0f, levelDb: -10f);

        QueueChordTimeOffset(engine, 0.90, 0.0);
        QueueChordMask(engine, 0.90, NoteMask(48));
        QueueNoteLevels(engine, 0.90, (48, 200));
        QueueFrameMeta(engine, 0.90, onset: false, chordChanged: false,
            chordNew: false, peak: 0.8f, levelDb: -14f);

        QueueChordTimeOffset(engine, 1.0, 0.0);
        QueueChordMask(engine, 1.0, NoteMask(48));
        QueueNoteLevels(engine, 1.0, (48, 180));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f);

        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void SplitTimestamps_ChordQueryUsesChordClock()
    {
        var root = Note(ProGuitarString.Red, 0, 960);
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960));
        // The changed frame is captured before the note's front window, but
        // its -1 s chord clock must keep it outside the attack scan when a
        // newer, unchanged frame arrives.  Using the note clock for the
        // attack scan would wrongly admit this frame.
        var engine = CreateEngineWithWindow(0.1, root);
        engine.Update(0.0);

        QueueChordTimeOffset(engine, 0.75, -1.0);
        QueueChordMask(engine, 0.75, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.75, (40, 220), (47, 180));
        QueueFrameMeta(engine, 0.75, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);
        engine.Update(0.75);
        Assert.That(root.WasHit, Is.False);

        QueueChordTimeOffset(engine, 0.95, 0.0);
        QueueChordMask(engine, 0.95, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.95, (40, 220), (47, 180));
        QueueFrameMeta(engine, 0.95, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(0.96);

        Assert.That(root.WasHit, Is.False);
    }

    [Test]
    public void WindowedNoteQuery_RejectsOneDetection()
    {
        var note = Note(ProGuitarString.Green, 3, 960);

        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMask(engine, 1.0, NoteMask(48));
        QueueNoteLevels(engine, 1.0, (48, 220));
        QueueFrameMeta(engine, 1.0, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.False);
    }

    [Test]
    public void WindowedChordQuery_AllowsOneActualToCoverHarmonicVoices()
    {
        var root = Note(ProGuitarString.Red, 0, 960);       // E2, 40
        root.AddChildNote(Note(ProGuitarString.Yellow, 0, 960)); // B3, 59

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // FUN_014a809f has no per-actual used flag.  E2 is present in the
        // harmonic template for B3, so one detected bin can cover both
        // expected notes.  The live octave trace confirmed this behavior.
        QueueFrameWithMeta(engine, 0.995, new[] { 40 },
            onset: true, chordChanged: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void LiveOctaveWindow_AcceptsLowerFundamentalTemplateCoverage()
    {
        // Live Rocksmith octave query: expected G3/G4 (55/67), with the
        // upper expected bin absent.  The lower fundamentals 33 and 36 in
        // the same histogram cover 67 and 55 through DAT_01197398.
        var root = ChordFromMidi(55, 67);
        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueFrameWithLevels(engine, 0.995, new[]
        {
            (55, 839f), (63, 371f), (47, 256f), (33, 234f), (70, 229f),
            (49, 215f), (41, 180f), (36, 91f), (27, 1f),
        }, onset: true, chordChanged: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
        Assert.That(engine.LastChordVoicingCoverage, Is.EqualTo(1.0));
    }

    [Test]
    public void LiveOctaveWindow_RejectsUpperOnly()
    {
        // The source template direction is asymmetric: the upper note's row
        // does not contain the lower octave, so an upper-only octave chord is
        // not accepted.  The live trace had zero accepted upper-only cases.
        var root = ChordFromMidi(55, 67);
        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueFrameWithLevels(engine, 0.995, new[] { (67, 900f) },
            onset: true, chordChanged: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False);
    }

    [Test]
    public void LiveSixNoteWindow_139426_AcceptsTemplateCoverage()
    {
        // Live Rocksmith 5/6-note window: expected 47/54/59/63/66/71 had
        // five exact expected bins; 47 is a lower fundamental in 66's
        // template, so the source matcher still covers all six templates.
        var root = ChordFromMidi(47, 54, 59, 63, 66, 71);
        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueFrameWithLevels(engine, 0.995, new[]
        {
            (54, 365f), (44, 308f), (35, 296f), (37, 215f), (71, 177f),
            (52, 175f), (63, 145f), (75, 125f), (47, 118f), (33, 39f),
            (59, 10f),
        }, onset: true, chordChanged: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
        Assert.That(engine.LastChordVoicingCoverage, Is.EqualTo(1.0));
    }

    [Test]
    public void LiveSixNoteWindow_148185_AcceptsOctaveTemplateCoverage()
    {
        // Live Rocksmith 5/6-note window: expected 43/50/55/59/62/67 had
        // only four exact expected bins.  The present lower fundamentals
        // 43, 31, 36, 25, and 33 cover 62, 50, 55, 59, and 67 respectively.
        var root = ChordFromMidi(43, 50, 55, 59, 62, 67);
        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueFrameWithLevels(engine, 0.995, new[]
        {
            (43, 508f), (63, 204f), (50, 144f), (59, 113f), (56, 112f),
            (25, 96f), (53, 51f), (38, 47f), (67, 38f), (69, 29f),
            (48, 27f), (31, 22f), (33, 10f), (36, 1f),
        }, onset: true, chordChanged: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
        Assert.That(engine.LastChordVoicingCoverage, Is.EqualTo(1.0));
    }

    [Test]
    public void ThreeNoteChord_RejectsMissingThirdVoice()
    {
        var root = Note(ProGuitarString.Green, 0, 960);       // A2, 45
        root.AddChildNote(Note(ProGuitarString.Orange, 2, 960)); // E3, 52
        root.AddChildNote(Note(ProGuitarString.Yellow, 0, 960)); // B3, 59

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // FUN_014a809f still requires every expected note for a three-note
        // chord when no lower fundamental covers the missing note.
        QueueFrameWithMeta(engine, 0.995, new[] { 45, 52 },
            onset: true, chordChanged: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False);
    }

    [Test]
    public void ThreeNoteChord_RejectsSingleDetectedVoice()
    {
        var root = Note(ProGuitarString.Green, 0, 960);       // A2, 45
        root.AddChildNote(Note(ProGuitarString.Orange, 2, 960)); // E3, 52
        root.AddChildNote(Note(ProGuitarString.Yellow, 0, 960)); // B3, 59

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueFrameWithMeta(engine, 0.995, new[] { 45 },
            onset: true, chordChanged: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False);
    }

    [Test]
    public void WindowedChordQuery_DoesNotApplyBassOctaveExtraToneVeto()
    {
        var root = Note(ProGuitarString.Red, 0, 960);       // E2, 40
        root.AddChildNote(Note(ProGuitarString.Orange, 2, 960)); // E3, 52

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // FUN_014a809f's unmatched-level octave veto is gated to detector
        // mode 2 (four-string bass).  The six-string Real Guitar path matches
        // both expected notes and does not veto an unrelated extra.
        QueueFrameWithMeta(engine, 0.995, new[] { 40, 52, 48 },
            onset: true, chordChanged: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void LowPowerChord_AcceptsWeakFifthWhenDetectorReportsIt()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);
        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.99, (40, 220), (47, 1));
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True,
            "Live Rocksmith accepted a low chord with a very weak expected fifth; the source matcher has no level floor.");
    }

    [Test]
    public void LowPowerChord_RejectsMissingFifth()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);
        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.99, (40, 220));
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False,
            "A missing expected note must not be accepted on the normal chord query.");
    }

    [Test]
    public void LowPowerChord_RejectsWrongFifth()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);
        QueueFrameWithMeta(engine, 0.995, new[] { 40, 45 },
            onset: true, chordChanged: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False,
            "The wrong fifth must not satisfy the source template matcher.");
    }

    [Test]
    public void LowPowerChord_RejectsWrongRoot()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);
        QueueFrameWithMeta(engine, 0.995, new[] { 28, 47 },
            onset: true, chordChanged: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False,
            "The small-chord root must be matched by its own bin, not a lower octave.");
    }

    [Test]
    public void LowPowerChord_AcceptsLowerFundamentalFifth()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);
        QueueFrameWithMeta(engine, 0.995, new[] { 40, 28 },
            onset: true, chordChanged: true);
        engine.Update(1.0);

        // The source harmonic template accepts B1 as the lower fundamental
        // of B2; the live octave trace confirmed this lower-note coverage.
        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void WindowedChordQuery_AllowsOneMissingLargeChordTone()
    {
        var root = Note(ProGuitarString.Green, 3, 960); // C3
        root.AddChildNote(Note(ProGuitarString.Orange, 2, 960)); // E3
        root.AddChildNote(Note(ProGuitarString.Blue, 0, 960)); // G3
        root.AddChildNote(Note(ProGuitarString.Yellow, 1, 960)); // C4
        root.AddChildNote(Note(ProGuitarString.Yellow, 0, 960)); // B3

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // Five-note chart, four detected notes.  FUN_014a809f applies the
        // source one-missing rule for five/six-note chart chords.
        QueueFrameWithMeta(engine, 0.995, new[] { 48, 52, 55, 59 },
            onset: true, chordChanged: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void WindowedChordQuery_RejectsMissingToneOnFourNoteChord()
    {
        var root = Note(ProGuitarString.Green, 3, 960); // C3
        root.AddChildNote(Note(ProGuitarString.Orange, 2, 960)); // E3
        root.AddChildNote(Note(ProGuitarString.Blue, 0, 960)); // G3
        root.AddChildNote(Note(ProGuitarString.Yellow, 1, 960)); // C4

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueFrameWithMeta(engine, 0.995, new[] { 48, 52, 55 },
            onset: true, chordChanged: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False);
    }

    [Test]
    public void SustainedFullFrame_AllowsOneMissingLargeChordTone()
    {
        var root = Note(ProGuitarString.Green, 3, 960, 1920); // C3
        root.AddChildNote(Note(ProGuitarString.Orange, 2, 960, 1920)); // E3
        root.AddChildNote(Note(ProGuitarString.Blue, 0, 960, 1920)); // G3
        root.AddChildNote(Note(ProGuitarString.Yellow, 1, 960, 1920)); // C4
        root.AddChildNote(Note(ProGuitarString.Yellow, 0, 960, 1920)); // B3

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueFrameWithMeta(engine, 0.995, new[] { 48, 52, 55, 59, 60 },
            onset: true, chordChanged: true);
        engine.Update(1.0);
        Assert.That(root.WasHit, Is.True);

        QueueFrameWithMeta(engine, 1.2, new[] { 48, 52, 55, 59 },
            onset: false, chordChanged: false);
        engine.Update(1.2);

        Assert.That(engine.HasActiveSustains, Is.True);
    }

    [Test]
    public void WeakQualitySustain_RequiresAllLargeChordTones()
    {
        var root = Note(ProGuitarString.Green, 3, 960, 1920); // C3
        root.AddChildNote(Note(ProGuitarString.Orange, 2, 960, 1920)); // E3
        root.AddChildNote(Note(ProGuitarString.Blue, 0, 960, 1920)); // G3
        root.AddChildNote(Note(ProGuitarString.Yellow, 1, 960, 1920)); // C4
        root.AddChildNote(Note(ProGuitarString.Yellow, 0, 960, 1920)); // B3

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueFrameWithMeta(engine, 0.995, new[] { 48, 52, 55, 59, 60 },
            onset: true, chordChanged: true);
        engine.Update(1.0);
        Assert.That(root.WasHit, Is.True);

        // A weak-quality frame may not use the one-missing-large-tone
        // sustain leniency.
        QueueChordQuality(engine, 1.2, ChordQualityFlags.Weak);
        QueueFrameWithMeta(engine, 1.2, new[] { 48, 52, 55, 59 },
            onset: false, chordChanged: false);
        engine.Update(1.2);

        Assert.That(engine.HasActiveSustains, Is.False);
    }

    [Test]
    public void StrongQualitySustain_KeepsLargeChordLeniency()
    {
        var root = Note(ProGuitarString.Green, 3, 960, 1920); // C3
        root.AddChildNote(Note(ProGuitarString.Orange, 2, 960, 1920)); // E3
        root.AddChildNote(Note(ProGuitarString.Blue, 0, 960, 1920)); // G3
        root.AddChildNote(Note(ProGuitarString.Yellow, 1, 960, 1920)); // C4
        root.AddChildNote(Note(ProGuitarString.Yellow, 0, 960, 1920)); // B3

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueFrameWithMeta(engine, 0.995, new[] { 48, 52, 55, 59, 60 },
            onset: true, chordChanged: true);
        engine.Update(1.0);
        Assert.That(root.WasHit, Is.True);

        QueueChordQuality(engine, 1.2, ChordQualityFlags.Strong);
        QueueFrameWithMeta(engine, 1.2, new[] { 48, 52, 55, 59 },
            onset: false, chordChanged: false);
        engine.Update(1.2);

        Assert.That(engine.HasActiveSustains, Is.True);
    }

    [Test]
    public void WindowedChordQuery_UsesSourceFrontAndBackBounds()
    {
        var root = Note(ProGuitarString.Red, 0, 960);
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960));

        var engine = CreateEngineWithWindow(0.4, root);
        engine.Update(0.0);

        QueueFrameWithMeta(engine, 0.82, new[] { 40, 47 },
            onset: true, chordChanged: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void HopoChord_UsesDirectNewestFrameQueryWithoutChangedFlag()
    {
        var root = Note(ProGuitarString.Red, 0, 960);
        root.Type = ProGuitarNoteType.Hopo;
        var fifth = Note(ProGuitarString.Green, 2, 960);
        fifth.Type = ProGuitarNoteType.Hopo;
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.995, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.995, (40, 220), (47, 180));
        QueueFrameMeta(engine, 0.995, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void HopoChord_ChildMaskTriggersDirectHopoPath()
    {
        var root = Note(ProGuitarString.Red, 0, 960);
        var fifth = Note(ProGuitarString.Green, 2, 960);
        fifth.Type = ProGuitarNoteType.Hopo;
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.995, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.995, (40, 220), (47, 180));
        QueueFrameMeta(engine, 0.995, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void HopoChord_RejectsMissingTone()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2
        root.Type = ProGuitarNoteType.Hopo;
        var fifth = Note(ProGuitarString.Green, 2, 960); // B2
        fifth.Type = ProGuitarNoteType.Hopo;
        var octave = Note(ProGuitarString.Orange, 2, 960); // E3
        octave.Type = ProGuitarNoteType.Hopo;
        root.AddChildNote(fifth);
        root.AddChildNote(octave);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.995, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.995, (40, 220), (47, 180));
        QueueFrameMeta(engine, 0.995, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False,
            "FUN_014a9d0c/FUN_014a809f requires every expected chord tone.");
    }

    [Test]
    public void TapChord_UsesDirectNewestFrameQueryWithoutChangedFlag()
    {
        var root = Note(ProGuitarString.Red, 0, 960);
        root.Type = ProGuitarNoteType.Tap;
        var fifth = Note(ProGuitarString.Green, 2, 960);
        fifth.Type = ProGuitarNoteType.Tap;
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.995, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.995, (40, 220), (47, 180));
        QueueFrameMeta(engine, 0.995, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void StrictChordQuery_RequiresAllExpectedNotes()
    {
        var root = Note(ProGuitarString.Red, 0, 960);
        var fifth = Note(ProGuitarString.Green, 2, 960);
        root.AddChildNote(fifth);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.99, (40, 220), (47, 180));
        QueueFrameMeta(engine, 0.99, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        Assert.That(engine.TryQueryChordStrict(new[] { root, fifth }), Is.True);

        QueueChordMask(engine, 1.05, NoteMask(40));
        QueueNoteLevels(engine, 1.05, (40, 220));
        QueueFrameMeta(engine, 1.05, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(1.05);

        Assert.That(engine.TryQueryChordStrict(new[] { root, fifth }), Is.False);
    }

    [Test]
    public void ArpeggioNote_UsesRootPitchAndChangedFrame()
    {
        var note = Note(ProGuitarString.Green, 3, 960,
            proFlags: ProGuitarNoteFlags.Arpeggio); // C3, 48
        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(48));
        QueueNoteLevels(engine, 0.99, (48, 220));
        QueueRootPitch(engine, 0.99, 48, 48.35f);
        QueueFrameMeta(engine, 0.99, onset: false, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(48));
        QueueNoteLevels(engine, 1.0, (48, 180));
        QueueRootPitch(engine, 1.0, 48, 48.30f);
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f);

        // FUN_014a5f8e only counts frames newer than the changed frame.
        QueueChordMask(engine, 1.01, NoteMask(48));
        QueueNoteLevels(engine, 1.01, (48, 180));
        QueueRootPitch(engine, 1.01, 48, 48.30f);
        QueueFrameMeta(engine, 1.01, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f);
        engine.Update(1.01);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void ArpeggioNote_StopsAtTheChangedFrameId()
    {
        var note = Note(ProGuitarString.Green, 3, 960,
            proFlags: ProGuitarNoteFlags.Arpeggio);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(48));
        QueueNoteLevels(engine, 0.99, (48, 220));
        QueueRootPitch(engine, 0.99, 48, 48.35f);
        QueueFrameMeta(engine, 0.99, onset: false, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f, frameId: 100);

        QueueChordMask(engine, 1.0, NoteMask(48));
        QueueNoteLevels(engine, 1.0, (48, 220));
        QueueRootPitch(engine, 1.0, 48, 48.30f);
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f, frameId: 101);

        QueueChordMask(engine, 1.01, NoteMask(48));
        QueueNoteLevels(engine, 1.01, (48, 220));
        QueueRootPitch(engine, 1.01, 48, 48.30f);
        QueueFrameMeta(engine, 1.01, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f, frameId: 102);
        engine.Update(1.01);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void ArpeggioNote_UsesRootQueryWhenTopTwoCandidatesMiss()
    {
        var note = Note(ProGuitarString.Green, 3, 960,
            proFlags: ProGuitarNoteFlags.Arpeggio); // C3, 48
        var engine = CreateEngine(note);
        engine.Update(0.0);

        // The chart pitch is not in the top-two candidate list, but the root
        // tracker resolves it.  This is the FUN_014a5f8e path.
        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.99, (40, 220), (47, 180));
        QueueRootPitch(engine, 0.99, 48, 48.35f);
        QueueFrameMeta(engine, 0.99, onset: false, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(40, 47));
        QueueNoteLevels(engine, 1.0, (40, 220), (47, 180));
        QueueRootPitch(engine, 1.0, 48, 48.30f);
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f);
        QueueChordMask(engine, 1.01, NoteMask(40, 47));
        QueueNoteLevels(engine, 1.01, (40, 220), (47, 180));
        QueueRootPitch(engine, 1.01, 48, 48.30f);
        QueueFrameMeta(engine, 1.01, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f);
        engine.Update(1.01);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void ArpeggioNote_FallsBackToNormalNoteQueryWhenRootPitchMisses()
    {
        var note = Note(ProGuitarString.Green, 3, 960,
            proFlags: ProGuitarNoteFlags.Arpeggio); // C3, 48
        var engine = CreateEngine(note);
        engine.Update(0.0);

        // The top-two candidate list contains the chart pitch, but the root
        // tracker is on a different voice.  FUN_017b049a falls back from
        // FUN_014a5f8e to FUN_014a9adc in this case.
        QueueChordMask(engine, 0.99, NoteMask(48));
        QueueNoteLevels(engine, 0.99, (48, 220));
        QueueRootPitch(engine, 0.99, 40, 40.35f);
        QueueFrameMeta(engine, 0.99, onset: false, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(48));
        QueueNoteLevels(engine, 1.0, (48, 180));
        QueueRootPitch(engine, 1.0, 40, 40.30f);
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void WindowedChordQuery_RequiresChangedFrame()
    {
        var root = Note(ProGuitarString.Red, 0, 960);
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960));

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 1.0, NoteMask(40, 47));
        QueueNoteLevels(engine, 1.0, (40, 220), (47, 180));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: true, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False);
    }

    [Test]
    public void WindowedNoteQuery_UsesRefinedFractionalCandidatePitch()
    {
        var note = Note(ProGuitarString.Red, 0, 960); // E2, 40
        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(39));
        QueueNoteLevels(engine, 0.99, (39, 220));
        QueueCandidatePitch(engine, 0.99, 39, 40.35f);
        QueueFrameMeta(engine, 0.99, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(39));
        QueueNoteLevels(engine, 1.0, (39, 180));
        QueueCandidatePitch(engine, 1.0, 39, 40.30f);
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void WindowedNoteQuery_AcceptsCandidateAboveMidi100()
    {
        var note = Note(ProGuitarString.Purple, 40, 960); // 104
        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(104));
        QueueCandidatePitch(engine, 0.99, 104, 104.0f);
        QueueFloatNoteLevels(engine, 0.99, (104, 220f));
        QueueFrameMeta(engine, 0.99, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(104));
        QueueCandidatePitch(engine, 1.0, 104, 104.0f);
        QueueFloatNoteLevels(engine, 1.0, (104, 220f));
        QueueFrameMeta(engine, 1.0, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void WindowedNoteQuery_UsesFrameAgeCandidatePitchIndex()
    {
        var note = Note(ProGuitarString.Red, 0, 960); // E2, 40
        var engine = CreateEngine(note);
        engine.Update(0.0);

        // FUN_014a5d4f reads +0xc4 with the frame-history index.  The newest
        // frame (age 0) uses the first candidate pitch; the older frame used
        // for the second detection (age 1) must use the second candidate
        // pitch, even though the first candidate is the one being checked.
        QueueChordMask(engine, 0.99, NoteMask(39, 41));
        QueueCandidatesWithPitches(engine, 0.99,
            (39, 220, 40.35f), (41, 100, 39.0f));
        QueueFloatNoteLevels(engine, 0.99, (39, 220f), (41, 100f));
        QueueFrameMeta(engine, 0.99, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(39, 41));
        QueueCandidatesWithPitches(engine, 1.0,
            (39, 220, 40.35f), (41, 100, 39.0f));
        QueueFloatNoteLevels(engine, 1.0, (39, 220f), (41, 100f));
        QueueFrameMeta(engine, 1.0, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        engine.Update(1.0);

        // Only the newest frame contributes a fractional detection because
        // the older frame's source pitch slot is 39.0.  The candidate-index
        // implementation incorrectly contributed a second detection and hit
        // the note.
        Assert.That(note.WasHit, Is.False);
    }

    [Test]
    public void WindowedNoteQuery_IgnoresTargetOutsideTopTwo()
    {
        var note = Note(ProGuitarString.Green, 3, 960); // C3, 48

        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(48, 52, 55));
        QueueNoteLevels(engine, 0.99, (48, 80), (52, 220), (55, 180));
        QueueFrameMeta(engine, 0.99, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(48, 52, 55));
        QueueNoteLevels(engine, 1.0, (48, 80), (52, 220), (55, 180));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        // C3 is present but sits below E3 and G3, outside FUN_014a5d4f's
        // two-note window.
        Assert.That(note.WasHit, Is.False);
    }

    [Test]
    public void WindowedNoteQuery_RejectsLowTargetOutsideTopTwo()
    {
        var note = Note(ProGuitarString.Red, 0, 960); // E2, 40
        var engine = CreateEngine(note);
        engine.Update(0.0);

        // FUN_014a5d4f considers only the top two candidates.  No YARG
        // half-peak exception is part of the source note query.
        QueueChordMask(engine, 0.99, NoteMask(39, 40, 43));
        QueueNoteLevels(engine, 0.99, (39, 255), (40, 200), (43, 210));
        QueueFrameMeta(engine, 0.99, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(39, 40, 43));
        QueueNoteLevels(engine, 1.0, (39, 255), (40, 200), (43, 210));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.False);
    }

    [Test]
    public void WindowedNoteQuery_RejectsHarmonicOnlyLowTarget()
    {
        var note = Note(ProGuitarString.Red, 0, 960); // E2, 40
        var engine = CreateEngine(note);
        engine.Update(0.0);

        // The fundamental is still buried in the analysis window/hum, but
        // E2's octave (52) and twelfth (59) are already resolved.
        QueueChordMask(engine, 0.99, NoteMask(52, 59, 64));
        QueueNoteLevels(engine, 0.99, (52, 220), (59, 180), (64, 90));
        QueueFrameMeta(engine, 0.99, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(52, 59, 64));
        QueueNoteLevels(engine, 1.0, (52, 220), (59, 180), (64, 90));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        // The fundamental is absent from the top-two candidate list; the
        // source note query does not accept harmonic-only evidence.
        Assert.That(note.WasHit, Is.False);
    }

    [Test]
    public void MutedNote_AcceptsOnsetInsideSourceFrontBound()
    {
        var note = Note(ProGuitarString.Green, 3, 960,
            proFlags: ProGuitarNoteFlags.Muted);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        // FUN_014a9e0b -> FUN_014a61ba scans the chart interval starting at
        // noteTime - 0.2.  An onset at 0.81 is inside the front bound.
        QueueChordMask(engine, 0.81, NoteMask(40));
        QueueNoteLevels(engine, 0.81, (40, 220));
        QueueFrameMeta(engine, 0.81, onset: true, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);

        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void MutedNote_RejectsOnsetBeforeSourceFrontBound()
    {
        var note = Note(ProGuitarString.Green, 3, 960,
            proFlags: ProGuitarNoteFlags.Muted);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        // An onset at 0.79 is before note.Time - 0.2 and must not satisfy
        // the source muted-note query.
        QueueChordMask(engine, 0.79, NoteMask(40));
        QueueNoteLevels(engine, 0.79, (40, 220));
        QueueFrameMeta(engine, 0.79, onset: true, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);

        engine.Update(1.0);

        Assert.That(note.WasHit, Is.False);
    }

    [Test]
    public void FretHandMuteChord_UsesImmediateDetectorState()
    {
        var root = Note(ProGuitarString.Red, 0, 960,
            proFlags: ProGuitarNoteFlags.FretHandMute);
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960));

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 1.0, NoteMask(40, 47));
        QueueCandidates(engine, 1.0, (40, 220), (47, 220));
        QueueNoteLevels(engine, 1.0, (40, 220), (47, 220));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f,
            immediateState: true);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void HarmonicNote_UsesSourceHarmonicTarget()
    {
        // Open low E plus the second-fret natural harmonic is E5 (76).
        var note = Note(ProGuitarString.Red, 2, 960,
            proFlags: ProGuitarNoteFlags.Harmonic);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMask(engine, 1.0, NoteMask(76));
        QueueCandidates(engine, 1.0, (76, 220));
        QueueNoteLevels(engine, 1.0, (76, 220));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.01, NoteMask(76));
        QueueCandidates(engine, 1.01, (76, 220));
        QueueNoteLevels(engine, 1.01, (76, 220));
        QueueFrameMeta(engine, 1.01, onset: false, chordChanged: false,
            chordNew: false, peak: 0.8f, levelDb: -12f);

        engine.Update(1.01);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void HarmonicChord_UsesSourceChildHarmonicQueries()
    {
        var root = Note(ProGuitarString.Red, 2, 960,
            proFlags: ProGuitarNoteFlags.Harmonic);
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960,
            proFlags: ProGuitarNoteFlags.Harmonic));

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // E2 second-fret harmonic -> E5 (76); A2 second-fret harmonic -> A5
        // (81).  FUN_014a5c6d queries every harmonic child.
        QueueChordMask(engine, 1.0, NoteMask(76, 81));
        QueueCandidates(engine, 1.0, (76, 220), (81, 220));
        QueueNoteLevels(engine, 1.0, (76, 220), (81, 220));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.01, NoteMask(76, 81));
        QueueCandidates(engine, 1.01, (76, 220), (81, 220));
        QueueNoteLevels(engine, 1.01, (76, 220), (81, 220));
        QueueFrameMeta(engine, 1.01, onset: false, chordChanged: false,
            chordNew: false, peak: 0.8f, levelDb: -12f);

        engine.Update(1.01);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void HarmonicChord_AllowsOneMissingValidTone()
    {
        var root = Note(ProGuitarString.Red, 2, 960,
            proFlags: ProGuitarNoteFlags.Harmonic);
        var child1 = Note(ProGuitarString.Green, 2, 960,
            proFlags: ProGuitarNoteFlags.Harmonic);
        var child2 = Note(ProGuitarString.Orange, 2, 960,
            proFlags: ProGuitarNoteFlags.Harmonic);
        var invalid = Note(ProGuitarString.Blue, 3, 960,
            proFlags: ProGuitarNoteFlags.Harmonic);
        root.AddChildNote(child1);
        root.AddChildNote(child2);
        root.AddChildNote(invalid);

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // Three valid harmonic targets (76, 81, 86); the fret-3 child has no
        // source harmonic interval.  FUN_014a5c6d counts valid claims only and
        // allows one miss when more than two are valid.
        QueueChordMask(engine, 1.0, NoteMask(76, 81));
        QueueCandidates(engine, 1.0, (76, 220), (81, 220));
        QueueNoteLevels(engine, 1.0, (76, 220), (81, 220));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.01, NoteMask(76, 81));
        QueueCandidates(engine, 1.01, (76, 220), (81, 220));
        QueueNoteLevels(engine, 1.01, (76, 220), (81, 220));
        QueueFrameMeta(engine, 1.01, onset: false, chordChanged: false,
            chordNew: false, peak: 0.8f, levelDb: -12f);

        engine.Update(1.01);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void SlideUnpitched_UsesImmediateDetectorState()
    {
        var note = Note(ProGuitarString.Green, 3, 960,
            proFlags: ProGuitarNoteFlags.SlideUnpitched);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMask(engine, 1.0, NoteMask(48));
        QueueCandidates(engine, 1.0, (48, 220));
        QueueNoteLevels(engine, 1.0, (48, 220));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f,
            immediateState: true);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void IgnoreNote_UsesSourceOnsetQuery()
    {
        var note = Note(ProGuitarString.Green, 3, 960,
            proFlags: ProGuitarNoteFlags.Ignore);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        QueueChordMask(engine, 1.0, NoteMask(40));
        QueueNoteLevels(engine, 1.0, (40, 220));
        QueueFrameMeta(engine, 1.0, onset: true, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void MutedNote_UsesSourceOnsetOnlyQuery()
    {
        var note = Note(ProGuitarString.Green, 3, 960,
            proFlags: ProGuitarNoteFlags.Muted);

        var engine = CreateEngine(note);
        engine.Update(0.0);

        // FUN_014a61ba/FUN_014a4ff0 only need a +0x795 onset.  The target
        // pitch is deliberately absent from the frame.
        QueueChordMask(engine, 1.0, NoteMask(40));
        QueueNoteLevels(engine, 1.0, (40, 220));
        QueueFrameMeta(engine, 1.0, onset: true, chordChanged: false,
            chordNew: false, peak: 1.0f, levelDb: -10f);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void PalmMutedNote_UsesSourceNormalQueryFallback()
    {
        var note = Note(ProGuitarString.Green, 3, 960,
            proFlags: ProGuitarNoteFlags.PalmMuted);

        var engine = CreateEngine(note);
        engine.Update(0.0);

        // FUN_014a6459's +0x35 context is zero in the shipped detector, so
        // WasPalmMutePlayed falls back to the normal note query.  The normal
        // query requires the source attack frame plus a second matching
        // detection; the palm-specific six-decibel drop is not applied.
        QueueChordMask(engine, 0.99, NoteMask(48));
        QueueCandidates(engine, 0.99, (48, 220));
        QueueNoteLevels(engine, 0.99, (48, 220));
        QueueFrameMeta(engine, 0.99, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(48));
        QueueNoteLevels(engine, 1.0, (48, 180));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 0.7f, levelDb: -16f);
        engine.Update(1.0);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void WindowedChordQuery_DoesNotReuseOneStrumForTwoNotes()
    {
        var first = Note(ProGuitarString.Red, 0, 960);
        first.AddChildNote(Note(ProGuitarString.Green, 2, 960));

        var second = Note(ProGuitarString.Red, 0, 1050);
        second.AddChildNote(Note(ProGuitarString.Green, 2, 1050));

        var engine = CreateEngineWithWindow(0.4, first, second);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueueNoteLevels(engine, 0.99, (40, 220), (47, 180));
        QueueFrameMeta(engine, 0.99, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);

        QueueChordMask(engine, 1.0, NoteMask(40, 47));
        QueueNoteLevels(engine, 1.0, (40, 160), (47, 120));
        QueueFrameMeta(engine, 1.0, onset: false, chordChanged: false,
            chordNew: false, peak: 0.5f, levelDb: -20f);
        engine.Update(1.0);

        Assert.That(first.WasHit, Is.True);
        Assert.That(second.WasHit, Is.False);

        // Decay frames from the same strum must not score the next note.
        QueueChordMask(engine, 1.01, NoteMask(40, 47));
        QueueNoteLevels(engine, 1.01, (40, 220), (47, 180));
        QueueFrameMeta(engine, 1.01, onset: false, chordChanged: false,
            chordNew: false, peak: 0.9f, levelDb: -11f);
        engine.Update(1.05);
        Assert.That(second.WasHit, Is.False);

        QueueChordMask(engine, 1.09, NoteMask(40, 47));
        QueueNoteLevels(engine, 1.09, (40, 220), (47, 180));
        QueueFrameMeta(engine, 1.09, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);
        engine.Update(1.09375);

        Assert.That(second.WasHit, Is.True);
    }

    [Test]
    public void RapidChords_UseEachNewMask()
    {
        var firstRoot = Note(ProGuitarString.Red, 0, 960);
        var firstFifth = Note(ProGuitarString.Green, 2, 960);
        firstRoot.AddChildNote(firstFifth);

        var secondRoot = Note(ProGuitarString.Green, 0, 1075);
        var secondFifth = Note(ProGuitarString.Orange, 2, 1075);
        secondRoot.AddChildNote(secondFifth);

        var engine = CreateEngine(firstRoot, secondRoot);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueuePitchAndAttack(engine, 1.0, 47);
        engine.Update(1.0);

        QueueChordMask(engine, 1.12, NoteMask(45, 52));
        QueuePitchAndAttack(engine, 1.13, 52);
        engine.Update(1.13);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstRoot.WasHit, Is.True);
            Assert.That(firstFifth.WasHit, Is.True);
            Assert.That(secondRoot.WasHit, Is.True);
            Assert.That(secondFifth.WasHit, Is.True);
        }
    }

    [Test]
    public void RapidChords_DoNotReuseThePreviousChordMask()
    {
        var firstRoot = Note(ProGuitarString.Red, 0, 960);
        var firstFifth = Note(ProGuitarString.Green, 2, 960);
        firstRoot.AddChildNote(firstFifth);

        var secondRoot = Note(ProGuitarString.Green, 0, 1075);
        var secondFifth = Note(ProGuitarString.Orange, 2, 1075);
        secondRoot.AddChildNote(secondFifth);

        var engine = CreateEngine(firstRoot, secondRoot);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueuePitchAndAttack(engine, 1.0, 47);
        engine.Update(1.0);

        // The second attack arrives before a new mask.  The old E5 mask must
        // not be allowed to satisfy the A5 chord.
        QueuePitchAndAttack(engine, 1.12, 30);
        engine.Update(1.12);

        Assert.That(secondRoot.WasHit, Is.False);

        QueueChordMask(engine, 1.13, NoteMask(45, 52));
        engine.Update(1.13);

        Assert.That(secondRoot.WasHit, Is.True);
    }

    [Test]
    public void TwoNotePowerChord_Hits()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);
        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void BrightPowerChord_AllowsRootHarmonic()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);

        // B3 is the third harmonic of the low E root and is expected on a
        // bright power chord.  It must not count as an unrelated tone.
        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueueStrongMask(engine, 0.99, NoteMask(40, 47, 59));
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void PowerChord_RejectsUnrelatedMajorThird()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var engine = CreateEngine(root);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueueStrongMask(engine, 0.99, NoteMask(40, 47, 56)); // G#3
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.False);
    }

    [Test]
    public void ThreeNotePowerChord_Hits()
    {
        var root = Note(ProGuitarString.Red, 0, 960); // E2, 40
        root.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47
        root.AddChildNote(Note(ProGuitarString.Orange, 2, 960)); // E3, 52

        var engine = CreateEngine(root);
        engine.Update(0.0);
        QueueChordMask(engine, 0.99, NoteMask(40, 47, 52));
        QueuePitchAndAttack(engine, 1.0, 40);
        engine.Update(1.0);

        Assert.That(root.WasHit, Is.True);
    }

    [Test]
    public void RapidTwoNotePowerChords_Hit()
    {
        var e5 = Note(ProGuitarString.Red, 0, 960); // E2, 40
        e5.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47

        var a5 = Note(ProGuitarString.Green, 0, 1075); // A2, 45
        a5.AddChildNote(Note(ProGuitarString.Orange, 2, 1075)); // E3, 52

        var d5 = Note(ProGuitarString.Orange, 0, 1190); // D3, 50
        d5.AddChildNote(Note(ProGuitarString.Blue, 2, 1190)); // A3, 57

        var engine = CreateEngine(e5, a5, d5);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40, 47));
        QueuePitchAndAttack(engine, 1.0, 47);
        engine.Update(1.0);

        QueueChordMask(engine, 1.14, NoteMask(45, 52));
        QueuePitchAndAttack(engine, 1.15, 52);
        engine.Update(1.15);

        QueueChordMask(engine, 1.24, NoteMask(50, 57));
        QueuePitchAndAttack(engine, 1.25, 57);
        engine.Update(1.25);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(e5.WasHit, Is.True, "E5");
            Assert.That(a5.WasHit, Is.True, "A5");
            Assert.That(d5.WasHit, Is.True, "D5");
        }
    }

    [Test]
    public void RapidThreeNotePowerChords_Hit()
    {
        var e5 = Note(ProGuitarString.Red, 0, 960); // E2, 40
        e5.AddChildNote(Note(ProGuitarString.Green, 2, 960)); // B2, 47
        e5.AddChildNote(Note(ProGuitarString.Orange, 2, 960)); // E3, 52

        var a5 = Note(ProGuitarString.Green, 0, 1075); // A2, 45
        a5.AddChildNote(Note(ProGuitarString.Orange, 2, 1075)); // E3, 52
        a5.AddChildNote(Note(ProGuitarString.Blue, 2, 1075)); // A3, 57

        var d5 = Note(ProGuitarString.Orange, 0, 1190); // D3, 50
        d5.AddChildNote(Note(ProGuitarString.Blue, 2, 1190)); // A3, 57
        d5.AddChildNote(Note(ProGuitarString.Yellow, 3, 1190)); // D4, 62

        var engine = CreateEngine(e5, a5, d5);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(40, 47, 52));
        QueuePitchAndAttack(engine, 1.0, 47);
        engine.Update(1.0);

        QueueChordMask(engine, 1.14, NoteMask(45, 52, 57));
        QueuePitchAndAttack(engine, 1.15, 52);
        engine.Update(1.15);

        QueueChordMask(engine, 1.24, NoteMask(50, 57, 62));
        QueuePitchAndAttack(engine, 1.25, 57);
        engine.Update(1.25);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(e5.WasHit, Is.True, "E5");
            Assert.That(a5.WasHit, Is.True, "A5");
            Assert.That(d5.WasHit, Is.True, "D5");
        }
    }

    [Test]
    public void RapidRegularChords_Hit()
    {
        var c = Note(ProGuitarString.Green, 3, 960); // C3, 48
        c.AddChildNote(Note(ProGuitarString.Orange, 2, 960)); // E3, 52
        c.AddChildNote(Note(ProGuitarString.Blue, 0, 960)); // G3, 55

        var g = Note(ProGuitarString.Red, 3, 1075); // G2, 43
        g.AddChildNote(Note(ProGuitarString.Green, 2, 1075)); // B2, 47
        g.AddChildNote(Note(ProGuitarString.Orange, 0, 1075)); // D3, 50

        var aMinor = Note(ProGuitarString.Green, 0, 1190); // A2, 45
        aMinor.AddChildNote(Note(ProGuitarString.Green, 3, 1190)); // C3, 48
        aMinor.AddChildNote(Note(ProGuitarString.Orange, 2, 1190)); // E3, 52

        var engine = CreateEngine(c, g, aMinor);
        engine.Update(0.0);

        QueueChordMask(engine, 0.99, NoteMask(48, 52, 55));
        QueuePitchAndAttack(engine, 1.0, 48);
        engine.Update(1.0);

        QueueChordMask(engine, 1.14, NoteMask(43, 47, 50));
        QueuePitchAndAttack(engine, 1.15, 47);
        engine.Update(1.15);

        QueueChordMask(engine, 1.24, NoteMask(45, 48, 52));
        QueuePitchAndAttack(engine, 1.25, 48);
        engine.Update(1.25);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(c.WasHit, Is.True, "C");
            Assert.That(g.WasHit, Is.True, "G");
            Assert.That(aMinor.WasHit, Is.True, "Am");
        }
    }

    [Test]
    public void RapidRepeatedChords_UseHeldChordState()
    {
        var chord = Note(ProGuitarString.Red, 0, 960, 60); // E2, 40
        chord.AddChildNote(Note(ProGuitarString.Green, 2, 960, 60)); // B2, 47

        var second = Note(ProGuitarString.Red, 0, 1050, 60);
        second.AddChildNote(Note(ProGuitarString.Green, 2, 1050, 60));

        var third = Note(ProGuitarString.Red, 0, 1140, 60);
        third.AddChildNote(Note(ProGuitarString.Green, 2, 1140, 60));

        var fourth = Note(ProGuitarString.Red, 0, 1230, 60);
        fourth.AddChildNote(Note(ProGuitarString.Green, 2, 1230, 60));

        var engine = CreateEngine(chord, second, third, fourth);
        engine.Update(0.0);

        // Only one new mask is published, and it contains a transient extra
        // (42) that is not part of the chart chord.  The held chord state is
        // built from the matched chart notes, not from this dirty mask, so it
        // must carry the next three attacks through.
        QueueChordMask(engine, 0.99, NoteMask(40, 42, 47));
        foreach (var note in new[] { chord, second, third, fourth })
        {
            double time = note.Time;
            QueuePitchAndAttack(engine, time, 40);
            engine.Update(time);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chord.WasHit, Is.True);
            Assert.That(second.WasHit, Is.True);
            Assert.That(third.WasHit, Is.True);
            Assert.That(fourth.WasHit, Is.True);
        }
    }

    [Test]
    public void ExpandedTestChart_HitsEveryNote()
    {
        // The legacy 113-note core fixture after the four-times-denser chord
        // expansion.  The expanded 121-note test song adds tap/HOPO/arpeggio
        // and muted events after this section; those branches are covered by
        // the dedicated parser/engine tests and the full-song technique
        // harness.
        // MIDI ticks are converted to the test chart's 960-ticks-per-second
        // clock (192 MIDI ticks/sec * 5).
        const int ChartTickScale = 5;
        var events = ExpandedTestEvents();

        var chart = new List<ProGuitarNote>();
        int totalNotes = 0;
        foreach (var (tick, length, raws) in events)
        {
            uint scaledTick = tick * ChartTickScale;
            uint scaledLength = length * ChartTickScale;
            var parent = NoteFromRaw(raws[0], scaledTick, scaledLength);
            for (int i = 1; i < raws.Length; i++)
            {
                parent.AddChildNote(NoteFromRaw(raws[i], scaledTick, scaledLength));
            }

            chart.Add(parent);
            totalNotes += raws.Length;
        }

        var engine = CreateEngine(chart.ToArray());
        engine.Update(0.0);

        // Every chart event gets a full 128-bin frame, matching the live
        // detector transport.  The engine must match the chart from frame
        // history rather than from the newest mask alone.
        foreach (var (tick, length, raws) in events)
        {
            double time = tick * ChartTickScale / TICKS_PER_SEC;
            int[] midi = raws.Select(raw =>
            {
                int stringIndex = (raw >> 5) & 0x7;
                int fret = raw & 0x1F;
                return RealGuitarEngine.GetMidiNote(stringIndex, fret);
            }).ToArray();

            QueueChordMaskNotes(engine, time - 0.005, midi);
            QueueNoteLevels(engine, time - 0.005,
                midi.Select(midiNote => (midiNote, 220)).ToArray());
            QueuePitchAndAttack(engine, time, midi.Min());
            engine.Update(time);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(totalNotes, Is.EqualTo(113));
            Assert.That(engine.EngineStats.TotalNotes, Is.EqualTo(totalNotes));
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(totalNotes));
            Assert.That(engine.EngineStats.NotesMissed, Is.Zero);
            bool allHit = true;
            foreach (var parent in chart)
            {
                foreach (var note in parent.AllNotes)
                {
                    allHit &= note.WasHit;
                }
            }
            Assert.That(allHit, Is.True);
        }
    }

    [Test]
    public void ExpandedTestChart_HitsEveryNoteThroughWindowedQuery()
    {
        const int ChartTickScale = 5;
        var events = ExpandedTestEvents();
        var chart = new List<ProGuitarNote>();
        int totalNotes = 0;

        foreach (var (tick, length, raws) in events)
        {
            uint scaledTick = tick * ChartTickScale;
            uint scaledLength = length * ChartTickScale;
            var parent = NoteFromRaw(raws[0], scaledTick, scaledLength);
            for (int i = 1; i < raws.Length; i++)
            {
                parent.AddChildNote(NoteFromRaw(raws[i], scaledTick, scaledLength));
            }

            chart.Add(parent);
            totalNotes += raws.Length;
        }

        // Use the full 200 ms front/back Real Guitar window, matching live
        // gameplay rather than the narrower unit-test default.
        var engine = CreateEngineWithWindow(0.4, chart.ToArray());
        engine.Update(0.0);

        foreach (var (tick, _, raws) in events)
        {
            double time = tick * ChartTickScale / TICKS_PER_SEC;
            int[] midi = raws.Select(raw =>
            {
                int stringIndex = (raw >> 5) & 0x7;
                int fret = raw & 0x1F;
                return RealGuitarEngine.GetMidiNote(stringIndex, fret);
            }).ToArray();

            if (midi.Length == 1)
            {
                // FUN_014a5d4f requires two matching detections inside the
                // caller window.
                QueueFrameWithMeta(engine, time - 0.02, midi,
                    onset: true, chordChanged: true);
                QueueFrameWithMeta(engine, time - 0.005, midi,
                    onset: false, chordChanged: false);
            }
            else
            {
                // FUN_014a6f01 needs one new-chord frame and a full-frame
                // chord match.
                QueueFrameWithMeta(engine, time - 0.005, midi,
                    onset: true, chordChanged: true);
            }

            engine.Update(time);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(totalNotes, Is.EqualTo(113));
            Assert.That(engine.EngineStats.TotalNotes, Is.EqualTo(totalNotes));
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(totalNotes));
            Assert.That(engine.EngineStats.NotesMissed, Is.Zero);
            foreach (var parent in chart)
            {
                foreach (var note in parent.AllNotes)
                {
                    Assert.That(note.WasHit, Is.True);
                }
            }
        }
    }

    private static List<(uint Tick, uint Length, int[] Raw)> ExpandedTestEvents()
    {
        var events = new List<(uint Tick, uint Length, int[] Raw)>
        {
            (960, 240, new[] { 0 }),
            (1440, 240, new[] { 35 }),
            (1920, 480, new[] { 103, 69 }),
            (2880, 960, new[] { 172 }),
            (4320, 240, new[] { 5 }),
            (4800, 240, new[] { 64, 96, 128, 160, 0, 32 }),
            (5184, 72, new[] { 0, 34 }),
            (5280, 72, new[] { 0, 34, 66 }),
        };

        var rapidGroups = new (uint Tick, int[] Raw)[]
        {
            (5376, new[] { 0, 34 }),
            (5448, new[] { 32, 66 }),
            (5520, new[] { 64, 98 }),
            (5592, new[] { 0, 34, 66 }),
            (5664, new[] { 32, 66, 98 }),
            (5736, new[] { 64, 98, 131 }),
            (5808, new[] { 35, 66, 96 }),
            (5880, new[] { 3, 34, 64 }),
            (5952, new[] { 32, 66, 129 }),
        };

        foreach (var (baseTick, raws) in rapidGroups)
        {
            for (int copy = 0; copy < 4; copy++)
            {
                events.Add((baseTick + (uint) (18 * copy), 0, raws));
            }
        }

        events.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        return events;
    }

    private static void QueueFrameWithMeta(RealGuitarEngine engine, double time,
        IReadOnlyList<int> midi, bool onset, bool chordChanged)
    {
        QueueChordMaskNotes(engine, time, midi);
        // Mirror live input: only the exact float blocks are sent, not a
        // byte histogram plus a float copy.
        QueueFloatNoteLevels(engine, time,
            midi.Select(note => (note, 220.25f)).ToArray());
        QueueFrameMeta(engine, time, onset, chordChanged, chordChanged,
            peak: 1.0f, levelDb: -10f);
    }

    private static void QueueFrameWithLevels(RealGuitarEngine engine, double time,
        IReadOnlyList<(int Midi, float Level)> notes, bool onset,
        bool chordChanged)
    {
        QueueChordMaskNotes(engine, time, notes.Select(n => n.Midi).ToArray());
        QueueFloatNoteLevels(engine, time, notes.ToArray());
        QueueFrameMeta(engine, time, onset, chordChanged, chordChanged,
            peak: 1.0f, levelDb: -10f);
    }

    private static ProGuitarNote ChordFromMidi(params int[] midi)
    {
        var notes = new ProGuitarNote[midi.Length];
        for (int i = 0; i < midi.Length; i++)
        {
            notes[i] = MidiToNote(midi[i]);
        }

        for (int i = 1; i < notes.Length; i++)
        {
            notes[0].AddChildNote(notes[i]);
        }

        return notes[0];
    }

    private static ProGuitarNote MidiToNote(int midi)
    {
        int bestString = -1;
        int bestFret = int.MaxValue;
        for (int stringIndex = 0; stringIndex < 6; stringIndex++)
        {
            int fret = midi - RealGuitarEngine.StandardTuning[stringIndex];
            if (fret is >= 0 and <= 24 && fret < bestFret)
            {
                bestString = stringIndex;
                bestFret = fret;
            }
        }

        if (bestString < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(midi), midi,
                "MIDI note is outside the standard-tuning fretboard range.");
        }

        return Note((ProGuitarString) bestString, bestFret, 960);
    }

    private static ProGuitarNote NoteFromRaw(int raw, uint tick, uint length)
    {
        int stringIndex = (raw >> 5) & 0x7;
        int fret = raw & 0x1F;
        return new ProGuitarNote((ProGuitarString) stringIndex, fret,
            ProGuitarNoteType.Strum, ProGuitarNoteFlags.None, NoteFlags.None,
            tick / TICKS_PER_SEC, length / TICKS_PER_SEC, tick, length);
    }

    [Test]
    public void AttackSettle_WaitsForTheCorrectPitch()
    {
        var note = Note(ProGuitarString.Green, 3, 960);
        var engine = CreateEngine(note);
        engine.Update(0.0);

        var wrongPitch = GameInput.Create(0.99, RealGuitarAction.Attack, 400);
        engine.QueueInput(ref wrongPitch);
        engine.Update(0.99);

        Assert.That(note.WasHit, Is.False);

        var pitch = GameInput.Create(1.00, RealGuitarAction.Pitch, 480);
        engine.QueueInput(ref pitch);
        engine.Update(1.00);

        Assert.That(note.WasHit, Is.True);
    }

    [Test]
    public void Bot_HitsAllNotesIncludingChords()
    {
        var single = Note(ProGuitarString.Green, 3, 960);
        var root = Note(ProGuitarString.Red, 0, 1920);
        var child = Note(ProGuitarString.Green, 2, 1920);
        root.AddChildNote(child);

        var engine = CreateEngine(true, single, root);

        for (double t = 0; t <= 3.0; t += 1.0 / 60.0)
        {
            engine.Update(t);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(single.WasHit, Is.True);
            Assert.That(root.WasHit, Is.True);
            Assert.That(child.WasHit, Is.True);
            Assert.That(engine.EngineStats.NotesHit, Is.EqualTo(3));
            Assert.That(engine.EngineStats.NotesMissed, Is.Zero);
            Assert.That(engine.EngineStats.MaxCombo, Is.EqualTo(3));
        }
    }

    [Test]
    public void NormalizeTuning_ConvertsPerStringOffsets()
    {
        var tuning = RealGuitarEngine.NormalizeTuning(new[] { -1, -1, -1, -1, -1, -1 });

        Assert.That(tuning, Is.EqualTo(new[] { 39, 44, 49, 54, 58, 63 }));
    }

    [Test]
    public void NormalizeTuning_KeepsAbsoluteMidiNotes()
    {
        var tuning = RealGuitarEngine.NormalizeTuning(new[] { 38, 45, 50, 55, 59, 64 });

        Assert.That(tuning, Is.EqualTo(new[] { 38, 45, 50, 55, 59, 64 }));
    }

    [Test]
    public void AlternateTuning_HitsAtTheTunesOpenStringPitch()
    {
        var note = Note(ProGuitarString.Red, 0, 960);
        var engine = CreateTunedEngine(new[] { -1, -1, -1, -1, -1, -1 }, 0, note);

        engine.Update(0.0);
        QueuePitchAndAttack(engine, 1.0, 39);
        engine.Update(1.0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(note.WasHit, Is.True);
            Assert.That(engine.GetTargetPitch(note), Is.EqualTo(39f));
            Assert.That(engine.MatchesPitch(40f, note), Is.False);
        }
    }

    [Test]
    public void TuningOffsetCents_IsAppliedToTheTargetPitch()
    {
        var note = Note(ProGuitarString.Red, 0, 960);
        var engine = CreateTunedEngine(new[] { 40, 45, 50, 55, 59, 64 }, 25, note);

        Assert.That(engine.GetTargetPitch(note), Is.EqualTo(40.25f));
    }

    private static RealGuitarEngine CreateEngine(params ProGuitarNote[] notes)
    {
        return CreateEngine(false, notes);
    }

    private static RealGuitarEngine CreateEngine(bool isBot, params ProGuitarNote[] notes)
    {
        var difficulty = new InstrumentDifficulty<ProGuitarNote>(
            Instrument.RealGuitar,
            Difficulty.Expert,
            new List<ProGuitarNote>(notes),
            new List<Phrase>(),
            new List<TextEvent>());

        var syncTrack = new SyncTrack(480);
        syncTrack.Tempos.Add(new TempoChange(120, 0, 0));

        return new RealGuitarEngine(difficulty, syncTrack, CreateParameters(), isBot);
    }

    private static CountingRealGuitarEngine CreateCountingEngine(
        params ProGuitarNote[] notes)
    {
        var difficulty = new InstrumentDifficulty<ProGuitarNote>(
            Instrument.RealGuitar,
            Difficulty.Expert,
            new List<ProGuitarNote>(notes),
            new List<Phrase>(),
            new List<TextEvent>());

        var syncTrack = new SyncTrack(480);
        syncTrack.Tempos.Add(new TempoChange(120, 0, 0));

        return new CountingRealGuitarEngine(difficulty, syncTrack, CreateParameters());
    }

    private sealed class CountingRealGuitarEngine : RealGuitarEngine
    {
        public int HitLoops;

        public CountingRealGuitarEngine(InstrumentDifficulty<ProGuitarNote> chart,
            SyncTrack syncTrack, GuitarEngineParameters parameters)
            : base(chart, syncTrack, parameters, false)
        {
        }

        protected override void UpdateHitLogic(double time)
        {
            HitLoops++;
            base.UpdateHitLogic(time);
        }
    }

    private static RealGuitarEngine CreateEngineWithWindow(double maxWindow,
        params ProGuitarNote[] notes)
    {
        var difficulty = new InstrumentDifficulty<ProGuitarNote>(
            Instrument.RealGuitar,
            Difficulty.Expert,
            new List<ProGuitarNote>(notes),
            new List<Phrase>(),
            new List<TextEvent>());

        var syncTrack = new SyncTrack(480);
        syncTrack.Tempos.Add(new TempoChange(120, 0, 0));

        return new RealGuitarEngine(difficulty, syncTrack,
            CreateParameters(maxWindow), false);
    }

    private static RealGuitarEngine CreateEngineWithOffset(double offset, double maxWindow,
        params ProGuitarNote[] notes)
    {
        var difficulty = new InstrumentDifficulty<ProGuitarNote>(
            Instrument.RealGuitar,
            Difficulty.Expert,
            new List<ProGuitarNote>(notes),
            new List<Phrase>(),
            new List<TextEvent>());

        var syncTrack = new SyncTrack(480);
        syncTrack.Tempos.Add(new TempoChange(120, 0, 0));

        return new RealGuitarEngine(difficulty, syncTrack,
            CreateParameters(maxWindow), false, null, 0, offset);
    }

    private static RealGuitarEngine CreateTunedEngine(int[] tuning, int tuningOffsetCents,
        params ProGuitarNote[] notes)
    {
        var difficulty = new InstrumentDifficulty<ProGuitarNote>(
            Instrument.RealGuitar,
            Difficulty.Expert,
            new List<ProGuitarNote>(notes),
            new List<Phrase>(),
            new List<TextEvent>());

        var syncTrack = new SyncTrack(480);
        syncTrack.Tempos.Add(new TempoChange(120, 0, 0));

        return new RealGuitarEngine(difficulty, syncTrack, CreateParameters(), false,
            tuning, tuningOffsetCents);
    }

    private static GuitarEngineParameters CreateParameters(double maxWindow = 0.1)
    {
        var hitWindow = new HitWindowSettings(maxWindow, maxWindow, 1.0, false, 0,
            1.0, 1.0, 0.15, 0.25);
        return new GuitarEngineParameters(
            hitWindow,
            4,
            0,
            0.05,
            new[] { 0.08f, 0.17f, 0.28f, 0.41f, 0.55f, 0.71f, 0.88f, 1.0f },
            new[] { 0.08f, 0.17f, 0.28f, 0.41f, 0.55f, 0.71f, 0.88f, 1.0f },
            0.1,
            0.1,
            0.1,
            false,
            true,
            false,
            false,
            true);
    }

    private static ulong NoteMask(params int[] notes)
    {
        ulong mask = 0;
        foreach (int note in notes)
        {
            mask |= 1UL << note;
        }
        return mask;
    }

    private static void QueueChordTimeOffset(RealGuitarEngine engine,
        double time, double offset)
    {
        long bits = BitConverter.DoubleToInt64Bits(offset);
        var low = GameInput.Create(time, RealGuitarAction.ChordTimeOffsetLow,
            unchecked((int) (uint) bits));
        var high = GameInput.Create(time, RealGuitarAction.ChordTimeOffsetHigh,
            unchecked((int) (uint) (bits >> 32)));
        engine.QueueInput(ref low);
        engine.QueueInput(ref high);
    }

    private static void QueueChordQuality(RealGuitarEngine engine, double time,
        ChordQualityFlags quality)
    {
        var input = GameInput.Create(time, RealGuitarAction.ChordQuality,
            (int) quality);
        engine.QueueInput(ref input);
    }

    private static void QueueChordMask(RealGuitarEngine engine, double time, ulong mask)
    {
        var notes0 = GameInput.Create(time, RealGuitarAction.ChordNotes0,
            unchecked((int) (uint) mask));
        var notes1 = GameInput.Create(time, RealGuitarAction.ChordNotes1,
            unchecked((int) (uint) (mask >> 32)));
        var notes2 = GameInput.Create(time, RealGuitarAction.ChordNotes2, 0);
        var notes3 = GameInput.Create(time, RealGuitarAction.ChordNotes3, 0);
        engine.QueueInput(ref notes0);
        engine.QueueInput(ref notes1);
        engine.QueueInput(ref notes2);
        engine.QueueInput(ref notes3);
    }

    private static void QueueNoteLevels(RealGuitarEngine engine, double time,
        params (int Midi, int Level)[] notes)
    {
        var levels = new byte[128];
        foreach (var (midi, level) in notes)
        {
            levels[midi] = (byte) Math.Clamp(level, 0, 255);
        }

        var valid = GameInput.Create(time, RealGuitarAction.ChordLevelsValid, 1);
        engine.QueueInput(ref valid);

        for (int group = 0; group < 32; group++)
        {
            int offset = group * 4;
            int packed = levels[offset]
                | (levels[offset + 1] << 8)
                | (levels[offset + 2] << 16)
                | (levels[offset + 3] << 24);
            var level = GameInput.Create(time,
                (RealGuitarAction) ((int) RealGuitarAction.ChordLevels0 + group), packed);
            engine.QueueInput(ref level);
        }
    }

    private static void QueueFloatNoteLevels(RealGuitarEngine engine, double time,
        params (int Midi, float Level)[] notes)
    {
        var levels = new float[128];
        foreach (var (midi, level) in notes)
        {
            levels[midi] = level;
        }

        var valid = GameInput.Create(time, RealGuitarAction.ChordLevelsValid, 1);
        engine.QueueInput(ref valid);
        for (int note = 0; note < 128; note++)
        {
            var input = GameInput.Create(time,
                (RealGuitarAction) ((int) RealGuitarAction.ChordFloatLevels + note),
                BitConverter.SingleToInt32Bits(levels[note]));
            engine.QueueInput(ref input);
        }
    }

    private static void QueueRootPitch(RealGuitarEngine engine, double time,
        int rootNote, float pitch)
    {
        var root = GameInput.Create(time, RealGuitarAction.ChordRoot, rootNote);
        var pitchInput = GameInput.Create(time, RealGuitarAction.RootPitch,
            BitConverter.SingleToInt32Bits(pitch));
        engine.QueueInput(ref root);
        engine.QueueInput(ref pitchInput);
    }

    private static void QueueCandidatePitch(RealGuitarEngine engine, double time,
        int note, float pitch)
    {
        var count = GameInput.Create(time, RealGuitarAction.ChordCandidateCount, 1);
        var notes = GameInput.Create(time, RealGuitarAction.ChordCandidateNotes0,
            note);
        var pitchInput = GameInput.Create(time, RealGuitarAction.ChordCandidatePitch0,
            BitConverter.SingleToInt32Bits(pitch));
        // FUN_014a5d4f indexes this block by frame age, so mirror the same
        // pitch in slot 1 for tests that supply more than one history frame.
        var pitchInput1 = GameInput.Create(time, RealGuitarAction.ChordCandidatePitch1,
            BitConverter.SingleToInt32Bits(pitch));
        engine.QueueInput(ref count);
        engine.QueueInput(ref notes);
        engine.QueueInput(ref pitchInput);
        engine.QueueInput(ref pitchInput1);
    }

    private static void QueueCandidatesWithPitches(RealGuitarEngine engine,
        double time, params (int Midi, int Level, float Pitch)[] candidates)
    {
        int count = Math.Min(candidates.Length, 20);
        uint packedNotes = 0;
        for (int i = 0; i < count && i < 4; i++)
        {
            packedNotes |= (uint) (byte) candidates[i].Midi << (i * 8);
        }

        var countInput = GameInput.Create(time,
            RealGuitarAction.ChordCandidateCount, count);
        var notesInput = GameInput.Create(time,
            RealGuitarAction.ChordCandidateNotes0, unchecked((int) packedNotes));
        engine.QueueInput(ref countInput);
        engine.QueueInput(ref notesInput);

        for (int i = 0; i < count; i++)
        {
            var pitchInput = GameInput.Create(time,
                (RealGuitarAction) ((int) RealGuitarAction.ChordCandidatePitch0 + i),
                BitConverter.SingleToInt32Bits(candidates[i].Pitch));
            engine.QueueInput(ref pitchInput);
        }
    }

    private static void QueueFrameMeta(RealGuitarEngine engine, double time,
        bool onset, bool chordChanged, bool chordNew, float peak, float levelDb,
        float detectorLevelDb = 0f, float detectorQuality = 100f,
        int frameId = 0, bool immediateState = false)
    {
        var onsetInput = GameInput.Create(time, RealGuitarAction.ChordOnset,
            onset ? 1 : 0);
        var peakInput = GameInput.Create(time, RealGuitarAction.ChordPeak,
            (int) (peak * 1000000f));
        var levelInput = GameInput.Create(time, RealGuitarAction.ChordLevel,
            (int) Math.Round(levelDb * 100f));
        int state = (chordChanged ? 1 : 0) | (chordNew ? 2 : 0);
        var stateInput = GameInput.Create(time, RealGuitarAction.ChordState, state);
        var frameIdInput = GameInput.Create(time, RealGuitarAction.ChordFrameId, frameId);
        var detectorLevelInput = GameInput.Create(time,
            RealGuitarAction.ChordDetectorLevel, (int) Math.Round(detectorLevelDb * 100f));
        var detectorQualityInput = GameInput.Create(time,
            RealGuitarAction.ChordDetectorQuality, (int) Math.Round(detectorQuality * 100f));
        var immediateStateInput = GameInput.Create(time,
            RealGuitarAction.ChordImmediateState, immediateState ? 1 : 0);
        engine.QueueInput(ref onsetInput);
        engine.QueueInput(ref peakInput);
        engine.QueueInput(ref levelInput);
        engine.QueueInput(ref stateInput);
        engine.QueueInput(ref frameIdInput);
        engine.QueueInput(ref detectorLevelInput);
        engine.QueueInput(ref detectorQualityInput);
        engine.QueueInput(ref immediateStateInput);
    }

    private static void QueueChordMaskNotes(RealGuitarEngine engine, double time,
        IReadOnlyList<int> notes)
    {
        uint[] masks = new uint[4];
        foreach (int midi in notes)
        {
            masks[midi >> 5] |= 1u << (midi & 31);
        }

        var notes0 = GameInput.Create(time, RealGuitarAction.ChordNotes0, unchecked((int) masks[0]));
        var notes1 = GameInput.Create(time, RealGuitarAction.ChordNotes1, unchecked((int) masks[1]));
        var notes2 = GameInput.Create(time, RealGuitarAction.ChordNotes2, unchecked((int) masks[2]));
        var notes3 = GameInput.Create(time, RealGuitarAction.ChordNotes3, unchecked((int) masks[3]));
        engine.QueueInput(ref notes0);
        engine.QueueInput(ref notes1);
        engine.QueueInput(ref notes2);
        engine.QueueInput(ref notes3);
    }

    private static void QueueStrongMask(RealGuitarEngine engine, double time, ulong mask)
    {
        var notes0 = GameInput.Create(time, RealGuitarAction.StrongChordNotes0,
            unchecked((int) (uint) mask));
        var notes1 = GameInput.Create(time, RealGuitarAction.StrongChordNotes1,
            unchecked((int) (uint) (mask >> 32)));
        var notes2 = GameInput.Create(time, RealGuitarAction.StrongChordNotes2, 0);
        var notes3 = GameInput.Create(time, RealGuitarAction.StrongChordNotes3, 0);
        engine.QueueInput(ref notes0);
        engine.QueueInput(ref notes1);
        engine.QueueInput(ref notes2);
        engine.QueueInput(ref notes3);
    }

    private static void QueueCandidates(RealGuitarEngine engine, double time,
        params (int Midi, int Level)[] candidates)
    {
        ulong midiPacked = 0;
        ulong levelPacked = 0;
        ulong midiPacked2 = 0;
        ulong levelPacked2 = 0;
        int count = Math.Min(candidates.Length, 16);
        for (int i = 0; i < count; i++)
        {
            int shift = (i & 7) * 8;
            if (i < 8)
            {
                midiPacked |= (ulong) (byte) candidates[i].Midi << shift;
                levelPacked |= (ulong) (byte) candidates[i].Level << shift;
            }
            else
            {
                midiPacked2 |= (ulong) (byte) candidates[i].Midi << shift;
                levelPacked2 |= (ulong) (byte) candidates[i].Level << shift;
            }
        }

        var midi0 = GameInput.Create(time, RealGuitarAction.ChordCandidatesMidi0,
            unchecked((int) (uint) midiPacked));
        var midi1 = GameInput.Create(time, RealGuitarAction.ChordCandidatesMidi1,
            unchecked((int) (uint) (midiPacked >> 32)));
        var level0 = GameInput.Create(time, RealGuitarAction.ChordCandidatesLevel0,
            unchecked((int) (uint) levelPacked));
        var level1 = GameInput.Create(time, RealGuitarAction.ChordCandidatesLevel1,
            unchecked((int) (uint) (levelPacked >> 32)));
        var midi2 = GameInput.Create(time, RealGuitarAction.ChordCandidatesMidi2,
            unchecked((int) (uint) midiPacked2));
        var midi3 = GameInput.Create(time, RealGuitarAction.ChordCandidatesMidi3,
            unchecked((int) (uint) (midiPacked2 >> 32)));
        var level2 = GameInput.Create(time, RealGuitarAction.ChordCandidatesLevel2,
            unchecked((int) (uint) levelPacked2));
        var level3 = GameInput.Create(time, RealGuitarAction.ChordCandidatesLevel3,
            unchecked((int) (uint) (levelPacked2 >> 32)));
        var countInput = GameInput.Create(time, RealGuitarAction.ChordCandidateCount, count);
        for (int group = 0; group < 5; group++)
        {
            uint packedNotes = 0;
            for (int j = 0; j < 4; j++)
            {
                int index = group * 4 + j;
                int note = index < candidates.Length ? candidates[index].Midi : 0;
                packedNotes |= (uint) (byte) note << (j * 8);
            }

            var noteGroup = GameInput.Create(time,
                (RealGuitarAction) ((int) RealGuitarAction.ChordCandidateNotes0 + group),
                unchecked((int) packedNotes));
            engine.QueueInput(ref noteGroup);
        }

        engine.QueueInput(ref midi0);
        engine.QueueInput(ref midi1);
        engine.QueueInput(ref level0);
        engine.QueueInput(ref level1);
        engine.QueueInput(ref midi2);
        engine.QueueInput(ref midi3);
        engine.QueueInput(ref level2);
        engine.QueueInput(ref level3);
        engine.QueueInput(ref countInput);
    }

    private static void QueueChordState(RealGuitarEngine engine, double time,
        bool changed, bool newChord, int ageFrames)
    {
        int state = (changed ? 1 : 0) | (newChord ? 2 : 0) | (ageFrames << 2);
        var input = GameInput.Create(time, RealGuitarAction.ChordState, state);
        engine.QueueInput(ref input);
    }

    private static void QueueClipped(RealGuitarEngine engine, double time, bool clipped)
    {
        var input = GameInput.Create(time, RealGuitarAction.Clipped, clipped ? 1 : 0);
        engine.QueueInput(ref input);
    }

    private static void QueuePitchAndAttack(RealGuitarEngine engine, double attackTime, int midi)
    {
        // Real mic frames arrive slightly apart: a pitch frame followed by an
        // onset frame. Use separate timestamps so both are processed in one
        // engine update.
        var pitch = GameInput.Create(attackTime - 0.01, RealGuitarAction.Pitch, midi * 10);
        var attack = GameInput.Create(attackTime, RealGuitarAction.Attack, midi * 10);
        engine.QueueInput(ref pitch);
        engine.QueueInput(ref attack);
    }

    [Test]
    public void OneStrum_DoesNotHitLaterChord()
    {
        var first = Note(ProGuitarString.Red, 0, 960); // 1.0 s, E2+B2
        first.AddChildNote(Note(ProGuitarString.Green, 2, 960));
        var second = Note(ProGuitarString.Red, 0, 1248); // 1.3 s, same shape
        second.AddChildNote(Note(ProGuitarString.Green, 2, 1248));

        var engine = CreateEngineWithWindow(0.4, first, second);
        engine.Update(0.0);
        QueueFrameWithMeta(engine, 0.5, Array.Empty<int>(),
            onset: false, chordChanged: false);
        engine.Update(0.5);

        QueueStrum(engine, 1.0, (40, 220), (47, 180));
        engine.Update(1.0);
        Assert.That(first.WasHit, Is.True);

        QueueRing(engine, 1.15, (40, 180), (47, 140));
        engine.Update(1.15);
        QueueRing(engine, 1.3, (40, 150), (47, 110));
        engine.Update(1.3);

        Assert.That(second.WasHit, Is.False,
            "A single strum at 1.0 must not satisfy the chord at 1.3.");
    }

    [Test]
    public void OneStrum_DoesNotHitChordInsideThePreWindow()
    {
        var first = Note(ProGuitarString.Red, 0, 960); // 1.0 s
        first.AddChildNote(Note(ProGuitarString.Green, 2, 960));
        var second = Note(ProGuitarString.Red, 0, 1104); // 1.15 s, inside 0.2 s
        second.AddChildNote(Note(ProGuitarString.Green, 2, 1104));

        var engine = CreateEngineWithWindow(0.4, first, second);
        engine.Update(0.0);
        QueueFrameWithMeta(engine, 0.5, Array.Empty<int>(),
            onset: false, chordChanged: false);
        engine.Update(0.5);

        QueueStrum(engine, 1.0, (40, 220), (47, 180));
        engine.Update(1.0);
        Assert.That(first.WasHit, Is.True);

        QueueRing(engine, 1.05, (40, 190), (47, 150));
        engine.Update(1.05);
        QueueRing(engine, 1.15, (40, 170), (47, 130));
        engine.Update(1.15);

        Assert.That(second.WasHit, Is.False,
            "The 1.0 s changed frame is consumed by the first chord and must not satisfy the 1.15 s chord.");
    }

    [Test]
    public void TwoStrums_HitTwoChords()
    {
        var first = Note(ProGuitarString.Red, 0, 960);
        first.AddChildNote(Note(ProGuitarString.Green, 2, 960));
        var second = Note(ProGuitarString.Red, 0, 1248);
        second.AddChildNote(Note(ProGuitarString.Green, 2, 1248));

        var engine = CreateEngineWithWindow(0.4, first, second);
        engine.Update(0.0);
        QueueFrameWithMeta(engine, 0.5, Array.Empty<int>(),
            onset: false, chordChanged: false);
        engine.Update(0.5);

        QueueStrum(engine, 1.0, (40, 220), (47, 180));
        engine.Update(1.0);
        QueueRing(engine, 1.15, (40, 180), (47, 140));
        engine.Update(1.15);
        QueueStrum(engine, 1.3, (40, 210), (47, 170));
        engine.Update(1.3);

        Assert.Multiple(() =>
        {
            Assert.That(first.WasHit, Is.True);
            Assert.That(second.WasHit, Is.True);
        });
    }

    [Test]
    public void RapidRepeatedChords_DoNotReuseRetriggerEdge()
    {
        var first = Note(ProGuitarString.Red, 0, 960); // 1.0 s
        first.AddChildNote(Note(ProGuitarString.Green, 2, 960));
        var second = Note(ProGuitarString.Red, 0, 1008); // 1.05 s, same shape
        second.AddChildNote(Note(ProGuitarString.Green, 2, 1008));

        var engine = CreateEngineWithWindow(0.4, first, second);
        engine.Update(0.0);
        QueueFrameWithMeta(engine, 0.5, Array.Empty<int>(),
            onset: false, chordChanged: false);
        engine.Update(0.5);

        QueueStrum(engine, 1.0, (40, 220), (47, 180));
        engine.Update(1.0);
        Assert.That(first.WasHit, Is.True);

        // A re-trigger edge 10 ms after the hit is inside the 30 ms
        // refractory and must not score the next repeated chord.
        QueueStrum(engine, 1.01, (40, 210), (47, 170));
        engine.Update(1.01);
        Assert.That(second.WasHit, Is.False);

        engine.Update(1.05);
        Assert.That(second.WasHit, Is.False,
            "The consumed re-trigger edge must not satisfy the 1.05 s chord.");

        QueueStrum(engine, 1.05, (40, 220), (47, 180));
        engine.Update(1.05);
        Assert.That(second.WasHit, Is.True);
    }

    [Test]
    public void MissedChord_ConsumesWrongStrum()
    {
        var first = Note(ProGuitarString.Red, 0, 960); // 1.0 s, E2+B2
        first.AddChildNote(Note(ProGuitarString.Green, 2, 960));
        var second = Note(ProGuitarString.Green, 0, 1104); // 1.15 s, A2+E3
        second.AddChildNote(Note(ProGuitarString.Orange, 2, 1104));

        var engine = CreateEngineWithWindow(0.1, first, second);
        engine.Update(0.0);
        QueueFrameWithMeta(engine, 0.5, Array.Empty<int>(),
            onset: false, chordChanged: false);
        engine.Update(0.5);

        // The wrong strum matches the second chord but not the first.
        QueueStrum(engine, 1.0, (45, 220), (52, 180));
        engine.Update(1.0);
        Assert.That(first.WasHit, Is.False);

        // Expire the first note so MissChord consumes the wrong strum.
        engine.Update(1.12);
        Assert.That(first.WasMissed, Is.True);

        // Without miss consumption the 1.0 s strum is still inside the
        // source 0.2 s fallback and could satisfy the 1.15 s chord.
        engine.Update(1.15);
        Assert.That(second.WasHit, Is.False,
            "A wrong strum consumed by the missed chord must not hit the next chord.");

        QueueStrum(engine, 1.15, (45, 220), (52, 180));
        engine.Update(1.15);
        Assert.That(second.WasHit, Is.True);
    }

    [Test]
    public void AttackEdgeConsumption_CanBeDisabled()
    {
        bool original = RealGuitarEngine.EnableAttackEdgeConsumption;
        try
        {
            RealGuitarEngine.EnableAttackEdgeConsumption = false;

            var first = Note(ProGuitarString.Red, 0, 960); // 1.0 s
            first.AddChildNote(Note(ProGuitarString.Green, 2, 960));
            var second = Note(ProGuitarString.Red, 0, 1104); // 1.15 s
            second.AddChildNote(Note(ProGuitarString.Green, 2, 1104));

            var engine = CreateEngineWithWindow(0.4, first, second);
            engine.Update(0.0);
            QueueFrameWithMeta(engine, 0.5, Array.Empty<int>(),
                onset: false, chordChanged: false);
            engine.Update(0.5);

            QueueStrum(engine, 1.0, (40, 220), (47, 180));
            engine.Update(1.0);
            Assert.That(first.WasHit, Is.True);

            QueueRing(engine, 1.05, (40, 190), (47, 150));
            engine.Update(1.05);
            QueueRing(engine, 1.15, (40, 170), (47, 130));
            engine.Update(1.15);

            Assert.That(second.WasHit, Is.True,
                "Without Layer 2 the source 0.2 s window reuses the 1.0 s changed frame.");
        }
        finally
        {
            RealGuitarEngine.EnableAttackEdgeConsumption = original;
        }
    }

    private static void QueueStrum(RealGuitarEngine engine, double time,
        params (int Midi, int Level)[] notes)
    {
        QueueChordMaskNotes(engine, time, notes.Select(n => n.Midi).ToArray());
        QueueNoteLevels(engine, time, notes);
        QueueFrameMeta(engine, time, onset: true, chordChanged: true,
            chordNew: true, peak: 1.0f, levelDb: -10f);
    }

    private static void QueueRing(RealGuitarEngine engine, double time,
        params (int Midi, int Level)[] notes)
    {
        QueueChordMaskNotes(engine, time, notes.Select(n => n.Midi).ToArray());
        QueueNoteLevels(engine, time, notes);
        QueueFrameMeta(engine, time, onset: false, chordChanged: false,
            chordNew: false, peak: 0.6f, levelDb: -16f);
    }

    private static ProGuitarNote Note(ProGuitarString proString, int fret, uint tick,
        uint tickLength = 0, ProGuitarNoteFlags proFlags = ProGuitarNoteFlags.None)
    {
        return new ProGuitarNote(
            proString,
            fret,
            ProGuitarNoteType.Strum,
            proFlags,
            NoteFlags.None,
            tick / TICKS_PER_SEC,
            tickLength / TICKS_PER_SEC,
            tick,
            tickLength);
    }
}
