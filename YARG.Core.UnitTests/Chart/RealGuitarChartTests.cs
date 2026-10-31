using System.Collections.Generic;
using NUnit.Framework;
using YARG.Core.Chart;

namespace YARG.Core.UnitTests.Chart;

public class RealGuitarChartTests
{
    [Test]
    public void RealGuitar_Prefers22FretWhenAvailable()
    {
        var chart = new SongChart(480);
        chart.ProGuitar_17Fret.AddDifficulty(Difficulty.Expert, CreateTrack(Instrument.ProGuitar_17Fret));
        chart.ProGuitar_22Fret.AddDifficulty(Difficulty.Expert, CreateTrack(Instrument.ProGuitar_22Fret));

        var track = chart.GetRealGuitarTrack(Instrument.RealGuitar, Difficulty.Expert);

        Assert.That(track.Instrument, Is.EqualTo(Instrument.ProGuitar_22Fret));
    }

    [Test]
    public void RealGuitar_FallsBackTo17Fret()
    {
        var chart = new SongChart(480);
        chart.ProGuitar_17Fret.AddDifficulty(Difficulty.Expert, CreateTrack(Instrument.ProGuitar_17Fret));

        var track = chart.GetRealGuitarTrack(Instrument.RealGuitar, Difficulty.Expert);

        Assert.That(track.Instrument, Is.EqualTo(Instrument.ProGuitar_17Fret));
    }

    [Test]
    public void RealGuitar_FallsBackTo17FretWhen22LacksDifficulty()
    {
        var chart = new SongChart(480);
        chart.ProGuitar_17Fret.AddDifficulty(Difficulty.Easy, CreateTrack(Instrument.ProGuitar_17Fret));
        chart.ProGuitar_22Fret.AddDifficulty(Difficulty.Expert, CreateTrack(Instrument.ProGuitar_22Fret));

        var track = chart.GetRealGuitarTrack(Instrument.RealGuitar, Difficulty.Easy);

        Assert.That(track.Instrument, Is.EqualTo(Instrument.ProGuitar_17Fret));
    }

    [Test]
    public void RealGuitar_IgnoresEmpty22FretDifficulty()
    {
        var chart = new SongChart(480);
        chart.ProGuitar_17Fret.AddDifficulty(Difficulty.Expert, CreateTrack(Instrument.ProGuitar_17Fret));
        chart.ProGuitar_22Fret.AddDifficulty(Difficulty.Expert, CreateEmptyTrack(Instrument.ProGuitar_22Fret));

        var track = chart.GetRealGuitarTrack(Instrument.RealGuitar, Difficulty.Expert);

        Assert.That(track.Instrument, Is.EqualTo(Instrument.ProGuitar_17Fret));
    }

    [Test]
    public void RealGuitar_IgnoresEmpty17FretDifficulty()
    {
        var chart = new SongChart(480);
        chart.ProGuitar_17Fret.AddDifficulty(Difficulty.Expert, CreateEmptyTrack(Instrument.ProGuitar_17Fret));
        chart.ProGuitar_22Fret.AddDifficulty(Difficulty.Expert, CreateTrack(Instrument.ProGuitar_22Fret));

        var track = chart.GetRealGuitarTrack(Instrument.RealGuitar, Difficulty.Expert);

        Assert.That(track.Instrument, Is.EqualTo(Instrument.ProGuitar_22Fret));
    }

    [Test]
    public void ExplicitProGuitarInstrument_KeepsItsFretCount()
    {
        var chart = new SongChart(480);
        chart.ProGuitar_17Fret.AddDifficulty(Difficulty.Expert, CreateTrack(Instrument.ProGuitar_17Fret));
        chart.ProGuitar_22Fret.AddDifficulty(Difficulty.Expert, CreateTrack(Instrument.ProGuitar_22Fret));

        var track = chart.GetRealGuitarTrack(Instrument.ProGuitar_17Fret, Difficulty.Expert);

        Assert.That(track.Instrument, Is.EqualTo(Instrument.ProGuitar_17Fret));
    }

    private static InstrumentDifficulty<ProGuitarNote> CreateTrack(Instrument instrument)
    {
        var note = new ProGuitarNote(
            ProGuitarString.Red,
            0,
            ProGuitarNoteType.Strum,
            ProGuitarNoteFlags.None,
            NoteFlags.None,
            0,
            0,
            0,
            0);

        return new InstrumentDifficulty<ProGuitarNote>(
            instrument,
            Difficulty.Expert,
            new List<ProGuitarNote> { note },
            new List<Phrase>(),
            new List<TextEvent>());
    }

    private static InstrumentDifficulty<ProGuitarNote> CreateEmptyTrack(Instrument instrument)
    {
        return new InstrumentDifficulty<ProGuitarNote>(
            instrument,
            Difficulty.Expert,
            new List<ProGuitarNote>(),
            new List<Phrase>(),
            new List<TextEvent>());
    }
}
