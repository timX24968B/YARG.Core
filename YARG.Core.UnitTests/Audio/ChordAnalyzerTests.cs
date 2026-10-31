using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YARG.Audio.PitchDetection;
using YARG.Core.Engine.RealGuitar;

namespace YARG.Core.UnitTests.Audio;

[TestFixture]
public class ChordAnalyzerTests
{
    private const int SampleRate = 44100;

    [Test]
    public void Analyzer_RefinesCandidateAndRootPitch()
    {
        var analyzer = new ChordAnalyzer(SampleRate);
        var frames = new List<ChordAnalysisFrame>();

        AnalyzeTone(analyzer, frames, 82.41f, 1.5f);

        var frame = frames
            .Where(f => f.Live && f.CandidateCount > 0)
            .LastOrDefault(f => f.RootNote == 40);
        Assert.That(frame.CandidateCount, Is.GreaterThan(0),
            "E2 should produce a live candidate frame with root MIDI 40.");

        Assert.That(frame.RootPitch, Is.EqualTo(40f).Within(0.25f),
            "FUN_014a15b8 should refine the root pitch inside the note's bin window.");

        var notes = frame.CandidateNotes;
        var pitches = frame.CandidatePitches;
        Assert.That(notes, Is.Not.Null);
        Assert.That(pitches, Is.Not.Null);

        int index = Array.IndexOf(notes!, 40);
        Assert.That(index, Is.GreaterThanOrEqualTo(0),
            "E2 should be present in the local-maximum candidate table.");
        Assert.That(pitches![index], Is.EqualTo(40f).Within(0.25f),
            "The candidate pitch should not be the source's -1 sentinel.");
    }

    [Test]
    public void LagTable_MatchesSourceReferenceFrame()
    {
        // Reference values captured from a live FUN_0149e1ba injection of a
        // 48 kHz, 4096-sample, 110 Hz / 0.1-amplitude frame.  The source peak
        // is lag 432 = 83.2178 and the lag table is zero at 218 and 872.
        const int sampleRate = 48000;
        var analyzer = new ChordAnalyzer(sampleRate);
        var frames = new List<ChordAnalysisFrame>();
        var block = new float[4096];
        for (int i = 0; i < block.Length; i++)
        {
            block[i] = (float) (0.1 * Math.Sin(2.0 * Math.PI * 110.0 * i / sampleRate));
        }

        int hop = analyzer.HopSize;
        for (int offset = 0; offset < block.Length; offset += hop)
        {
            int count = Math.Min(hop, block.Length - offset);
            analyzer.Process(block.AsSpan(offset, count),
                (offset + count) / (double) sampleRate, frames);
        }

        ReadOnlySpan<float> lag = analyzer.LagTable;
        Assert.That(lag[432], Is.EqualTo(83.2178f).Within(0.25f),
            "The 110 Hz lag peak must match the live source value.");
        Assert.That(lag[431], Is.EqualTo(83.2117f).Within(0.25f));
        Assert.That(lag[433], Is.EqualTo(83.2012f).Within(0.25f));
        Assert.That(lag[218], Is.EqualTo(0f).Within(1e-6f),
            "The half-period lag is removed by the envelope.");
        Assert.That(lag[872], Is.EqualTo(0f).Within(1e-6f));
    }

    [Test]
    public void SourceNoteFrequency_UsesPrecisionTunerConditionalBias()
    {
        float precision = ChordAnalyzer.SourceNoteFrequency(69, true);
        float biased = ChordAnalyzer.SourceNoteFrequency(69, false);

        Assert.That(precision, Is.EqualTo(440f).Within(1e-4f),
            "UsePrecisionTuner=true must use the unshifted MIDI frequency.");
        Assert.That(biased, Is.EqualTo((float) (440.0 *
            Math.Pow(2.0, -0.15 / 12.0))).Within(1e-3f),
            "The normal detector path must use note - 0.15 semitones.");
        Assert.That(biased, Is.LessThan(precision));
    }

    [Test]
    public void Analyzer_DoesNotUseStableRootForChordAttackQuality()
    {
        var analyzer = new ChordAnalyzer(SampleRate);
        var frames = new List<ChordAnalysisFrame>();

        // E2 + B3 with the upper voice loud enough to become the top lag-table
        // candidate.  The B3/C4 candidate fails the FUN_014a272f template
        // coverage test, so the literal FUN_014a07c9 top-candidate gate
        // rejects the frame.  A stable-root (E2) quality fallback would
        // incorrectly admit it.
        AnalyzeChord(analyzer, frames, 82.41f, 246.94f, 1.2f);

        var rejectedTopFrames = frames
            .Where(f => f.Live && f.CandidateCount > 0 &&
                f.CandidateNotes is { Length: > 0 } notes &&
                f.NoteLevelsFloat is { Length: >= 128 } levels &&
                !RealGuitarEngine.IsRocksmithRootQuality(notes[0], levels))
            .ToList();

        Assert.That(rejectedTopFrames, Is.Not.Empty,
            "The synthetic chord must exercise a rejected top candidate.");
        Assert.That(rejectedTopFrames.Any(f => f.ChordAttack), Is.False,
            "FUN_014a07c9 must not accept a failed top-candidate quality via the stable root.");
    }

    [Test]
    public void Analyzer_CandidateTablesAreSnapshottedPerFrame()
    {
        var analyzer = new ChordAnalyzer(SampleRate);
        var frames = new List<ChordAnalysisFrame>();

        AnalyzeTone(analyzer, frames, 82.41f, 0.8f);
        var first = frames.Last(f => f.Live && f.CandidateCount > 0);
        Assert.That(first.CandidateNotes, Is.Not.Null);
        Assert.That(first.CandidatePitches, Is.Not.Null);

        int[] notes = first.CandidateNotes!.ToArray();
        float[] pitches = first.CandidatePitches!.ToArray();

        AnalyzeTone(analyzer, frames, 220f, 0.8f);
        var later = frames.Last(f => f.Live && f.CandidateCount > 0);

        Assert.That(object.ReferenceEquals(first.CandidateNotes, later.CandidateNotes),
            Is.False, "Each detector frame must own its candidate table.");
        Assert.That(first.CandidateNotes, Is.EqualTo(notes),
            "A later hop must not mutate an earlier frame's candidate notes.");
        Assert.That(first.CandidatePitches, Is.EqualTo(pitches),
            "A later hop must not mutate an earlier frame's candidate pitches.");
    }

    private static void AnalyzeTone(ChordAnalyzer analyzer,
        List<ChordAnalysisFrame> frames, float frequency, double seconds)
    {
        int sampleCount = (int) (SampleRate * seconds);
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double t = i / (double) SampleRate;
            double local = t - 0.15;
            if (local < 0.0)
            {
                continue;
            }

            // The source level envelope needs a varying signal: a perfectly
            // steady tone converges maxDb and minDb to the same value and the
            // +0x750 quality gate stays at zero.  A plucked note after a
            // silent lead-in produces the attack frame the detector gates on.
            double envelope = Math.Min(1.0, local / 0.01) * Math.Exp(-local * 4.0);
            samples[i] = (float) (envelope * (
                0.35 * Math.Sin(2.0 * Math.PI * frequency * t) +
                0.10 * Math.Sin(4.0 * Math.PI * frequency * t)));
        }

        int hop = Math.Max(1, analyzer.HopSize);
        for (int offset = 0; offset < samples.Length; offset += hop)
        {
            int count = Math.Min(hop, samples.Length - offset);
            analyzer.Process(samples.AsSpan(offset, count),
                (offset + count) / (double) SampleRate, frames);
        }
    }

    private static void AnalyzeChord(ChordAnalyzer analyzer,
        List<ChordAnalysisFrame> frames, float firstFrequency,
        float secondFrequency, double seconds)
    {
        int sampleCount = (int) (SampleRate * seconds);
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double t = i / (double) SampleRate;
            double local = t - 0.15;
            if (local < 0.0)
            {
                continue;
            }

            double envelope = Math.Min(1.0, local / 0.01) * Math.Exp(-local * 4.0);
            samples[i] = (float) (envelope * (
                0.35 * Math.Sin(2.0 * Math.PI * firstFrequency * t) +
                0.35 * Math.Sin(2.0 * Math.PI * secondFrequency * t)));
        }

        int hop = Math.Max(1, analyzer.HopSize);
        for (int offset = 0; offset < samples.Length; offset += hop)
        {
            int count = Math.Min(hop, samples.Length - offset);
            analyzer.Process(samples.AsSpan(offset, count),
                (offset + count) / (double) SampleRate, frames);
        }
    }
}
