using System;
using System.Collections.Generic;
using System.Linq;
using ManagedBass;
using NUnit.Framework;
using YARG.Core.Chart;
using YARG.DeepScan;

namespace YARG.Core.UnitTests.DeepScan;

public class DeepScanAnalyzerTests
{
    private const double TicksPerSecond = 960.0;

    [Test]
    public void OrderScanWindows_ReturnsAllScannableNotesAndStartsWithDiscriminatingNotes()
    {
        var early = new List<ProGuitarNote>
        {
            Note(ProGuitarString.Orange, 3, 1.0),
            Note(ProGuitarString.Orange, 5, 2.0),
            Note(ProGuitarString.Orange, 7, 3.0),
        };

        var chord = Note(ProGuitarString.Red, 0, 10.0);
        chord.AddChildNote(Note(ProGuitarString.Green, 2, 10.0));
        var blue = Note(ProGuitarString.Blue, 3, 11.0);
        var yellow = Note(ProGuitarString.Yellow, 2, 12.0);
        var purple = Note(ProGuitarString.Purple, 0, 13.0);
        var muted = Note(ProGuitarString.Red, 5, 14.0, ProGuitarNoteFlags.Muted);

        var chart = new List<ProGuitarNote>();
        chart.AddRange(early);
        chart.Add(chord);
        chart.Add(blue);
        chart.Add(yellow);
        chart.Add(purple);
        chart.Add(muted);

        var ordered = DeepScanAnalyzer.OrderScanWindows(chart);

        Assert.Multiple(() =>
        {
            Assert.That(ordered, Has.Count.EqualTo(7),
                "Every non-muted note should be returned.");
            Assert.That(ordered, Has.None.EqualTo(muted),
                "Muted notes must not be scanned.");
            Assert.That(ordered.Distinct().Count(), Is.EqualTo(ordered.Count),
                "The order must not contain duplicate notes.");
            Assert.That(ordered[0].Time, Is.GreaterThan(early[0].Time),
                "The first scan note must be selected for discrimination, not chart order.");
            bool firstTestsLowString = false;
            foreach (var note in ordered[0].AllNotes)
            {
                if (note.String <= 1)
                {
                    firstTestsLowString = true;
                    break;
                }
            }

            Assert.That(firstTestsLowString, Is.True,
                "The first note should test a low string that separates the tuning candidates.");
        });
    }

    [Test]
    public void OrderScanWindows_WithFewerThanSixNotes_ReturnsAll()
    {
        var chart = new List<ProGuitarNote>
        {
            Note(ProGuitarString.Red, 0, 1.0),
            Note(ProGuitarString.Green, 2, 2.0),
            Note(ProGuitarString.Purple, 3, 3.0),
        };

        var ordered = DeepScanAnalyzer.OrderScanWindows(chart);

        Assert.That(ordered, Has.Count.EqualTo(3));
    }

    [Test]
    public void OrderScanWindows_IsBasedOnChartInformationOnly()
    {
        // The method has no audio/evidence parameter by design.  A chart with
        // one early low-string note and many later notes must still put the
        // discriminating low-string note first.
        var chart = new List<ProGuitarNote>
        {
            Note(ProGuitarString.Red, 0, 50.0),
            Note(ProGuitarString.Orange, 4, 1.0),
            Note(ProGuitarString.Orange, 5, 2.0),
            Note(ProGuitarString.Orange, 6, 3.0),
            Note(ProGuitarString.Green, 2, 4.0),
            Note(ProGuitarString.Blue, 2, 5.0),
        };

        var ordered = DeepScanAnalyzer.OrderScanWindows(chart);

        Assert.That(ordered[0].Time, Is.GreaterThan(1.0),
            "The earliest note is non-discriminating; the scan must not follow chart order.");

        bool lowString = false;
        foreach (var note in ordered[0].AllNotes)
        {
            if (note.String <= 1)
            {
                lowString = true;
                break;
            }
        }

        Assert.That(lowString, Is.True,
            "The first note should test a low string that separates candidates.");
    }

    [Test]
    public void ShouldStopScan_RequiresSixWindowsAndConfidence()
    {
        var top = Candidate("E Standard", 10, 8, 10, 0.8,
            new[] { 4, 4, 0, 0, 0, 0 },
            new[] { 0.8, 0.8, 0, 0, 0, 0 });
        var second = Candidate("Eb Standard", 4, 3, 10, 0.3,
            new[] { 2, 2, 0, 0, 0, 0 },
            new[] { 0.3, 0.3, 0, 0, 0, 0 });

        Assert.Multiple(() =>
        {
            Assert.That(DeepScanAnalyzer.ShouldStopScan(5, top, second), Is.False,
                "The six-note minimum must be met before confidence can stop the scan.");
            Assert.That(DeepScanAnalyzer.ShouldStopScan(6, top, second), Is.True);
            Assert.That(DeepScanAnalyzer.ShouldStopScan(6, null, null), Is.False);
        });
    }

    [Test]
    public void IsTuningConfident_RequiresEnoughCoveredStringsAndEvidence()
    {
        var top = Candidate("E Standard", 10, 8, 10, 0.8,
            new[] { 4, 4, 0, 0, 0, 0 },
            new[] { 0.8, 0.8, 0, 0, 0, 0 });
        var second = Candidate("Eb Standard", 4, 3, 10, 0.3,
            new[] { 2, 2, 0, 0, 0, 0 },
            new[] { 0.3, 0.3, 0, 0, 0, 0 });

        Assert.That(DeepScanAnalyzer.IsTuningConfident(top, second), Is.True);

        var tooLittle = Candidate("E Standard", 5, 3, 5, 0.6,
            new[] { 2, 1, 0, 0, 0, 0 },
            new[] { 0.6, 0.3, 0, 0, 0, 0 });
        Assert.That(DeepScanAnalyzer.IsTuningConfident(tooLittle, null), Is.False,
            "Fewer than six observations cannot be confident.");

        var oneString = Candidate("E Standard", 10, 8, 10, 0.8,
            new[] { 10, 0, 0, 0, 0, 0 },
            new[] { 0.8, 0, 0, 0, 0, 0 });
        Assert.That(DeepScanAnalyzer.IsTuningConfident(oneString, second), Is.False,
            "A single string cannot resolve the tuning.");

        var lowCoverage = Candidate("E Standard", 10, 3, 10, 0.2,
            new[] { 4, 4, 0, 0, 0, 0 },
            new[] { 0.2, 0.2, 0, 0, 0, 0 });
        Assert.That(DeepScanAnalyzer.IsTuningConfident(lowCoverage, second), Is.False);
    }

    [Test]
    public void IsTuningConfident_AcceptsADecisivePerStringLead()
    {
        var top = Candidate("Eb Standard", 5.0, 6, 20, 0.30,
            new[] { 10, 10, 0, 0, 0, 0 },
            new[] { 0.80, 0.20, 0, 0, 0, 0 });
        var second = Candidate("Drop Db", 4.8, 5, 20, 0.24,
            new[] { 10, 10, 0, 0, 0, 0 },
            new[] { 0.40, 0.20, 0, 0, 0, 0 });

        Assert.That(DeepScanAnalyzer.IsTuningConfident(top, second), Is.True);
    }

    [Test]
    public void ChooseStem_PrefersGuitarThenRhythmThenSong()
    {
        var guitar = new DeepScanAnalyzer.StemCandidate("guitar", "guitar.ogg", false);
        var rhythm = new DeepScanAnalyzer.StemCandidate("rhythm", "rhythm.ogg", false);
        var song = new DeepScanAnalyzer.StemCandidate("song", "song.ogg", false);

        Assert.Multiple(() =>
        {
            Assert.That(DeepScanAnalyzer.ChooseStem(new[] { guitar, rhythm, song }),
                Is.EqualTo("guitar.ogg"));

            var silentGuitar = new DeepScanAnalyzer.StemCandidate("guitar", "guitar.ogg", true);
            Assert.That(DeepScanAnalyzer.ChooseStem(new[] { silentGuitar, rhythm, song }),
                Is.EqualTo("rhythm.ogg"));

            var silentRhythm = new DeepScanAnalyzer.StemCandidate("rhythm", "rhythm.ogg", true);
            Assert.That(DeepScanAnalyzer.ChooseStem(new[] { silentGuitar, silentRhythm, song }),
                Is.EqualTo("song.ogg"));

            var silentSong = new DeepScanAnalyzer.StemCandidate("song", "song.ogg", true);
            Assert.That(DeepScanAnalyzer.ChooseStem(
                new[] { silentGuitar, silentRhythm, silentSong }), Is.Null);

            var missingGuitar = new DeepScanAnalyzer.StemCandidate("guitar", null, true);
            Assert.That(DeepScanAnalyzer.ChooseStem(new[] { missingGuitar, rhythm, song }),
                Is.EqualTo("rhythm.ogg"));
        });
    }

    [Test]
    public void StemDecodeFlags_UseRawFloatDecodeOnly()
    {
        Assert.That(DeepScanAnalyzer.StemDecodeFlags,
            Is.EqualTo(BassFlags.Decode | BassFlags.Float),
            "Deep Scan must decode raw float samples without EQ or DSP effects.");
    }

    private static ProGuitarNote Note(ProGuitarString proString, int fret, double time,
        ProGuitarNoteFlags proFlags = ProGuitarNoteFlags.None)
    {
        uint tick = (uint) Math.Round(time * TicksPerSecond);
        return new ProGuitarNote(proString, fret, ProGuitarNoteType.Strum, proFlags,
            NoteFlags.None, time, 0, tick, 0);
    }

    private static DeepScanCandidate Candidate(string name, double score, int matches,
        int total, double coverage, int[] stringTotals, double[] stringCoverage)
    {
        return new DeepScanCandidate
        {
            Name = name,
            Tuning = Array.Empty<int>(),
            Score = score,
            Matches = matches,
            Total = total,
            Coverage = coverage,
            StringTotal = stringTotals,
            StringMatches = stringTotals.Select((t, i) =>
                (int) Math.Round(t * stringCoverage[i])).ToArray(),
            StringCoverage = stringCoverage,
        };
    }
}
