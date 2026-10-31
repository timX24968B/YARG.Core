// pattern: Imperative Shell

using System.Drawing;
using System.IO;
using System.Text;
using NUnit.Framework;
using YARG.Core.Game;

namespace YARG.Core.UnitTests.Game;

public class ColorProfileTests
{
    [Test]
    public void BinarySerialization_PreservesFiveFretGuitarAppearanceFields()
    {
        var source = new ColorProfile("Source");
        source.FiveFretGuitar.TapStripEmission = 37.5f;
        source.FiveFretGuitar.OpenHopoNote = Color.FromArgb(255, 12, 34, 56);
        source.FiveFretGuitar.OpenHopoNoteStarPower = Color.FromArgb(255, 78, 90, 12);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            source.Serialize(writer);
        }

        stream.Position = 0;
        var restored = new ColorProfile("Restored");
        using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            restored.Deserialize(reader);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.FiveFretGuitar.TapStripEmission,
                Is.EqualTo(source.FiveFretGuitar.TapStripEmission));
            Assert.That(restored.FiveFretGuitar.OpenHopoNote,
                Is.EqualTo(source.FiveFretGuitar.OpenHopoNote));
            Assert.That(restored.FiveFretGuitar.OpenHopoNoteStarPower,
                Is.EqualTo(source.FiveFretGuitar.OpenHopoNoteStarPower));
        }
    }

    [Test]
    public void RealGuitarColors_UseRocksmithStringOrder()
    {
        var profile = new ColorProfile("Source");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(profile.RealGuitar.GetNoteColor(0), Is.EqualTo(ColorProfile.DefaultRed));
            Assert.That(profile.RealGuitar.GetNoteColor(1), Is.EqualTo(ColorProfile.DefaultYellow));
            Assert.That(profile.RealGuitar.GetNoteColor(2), Is.EqualTo(ColorProfile.DefaultBlue));
            Assert.That(profile.RealGuitar.GetNoteColor(3), Is.EqualTo(ColorProfile.DefaultOrange));
            Assert.That(profile.RealGuitar.GetNoteColor(4), Is.EqualTo(ColorProfile.DefaultGreen));
            Assert.That(profile.RealGuitar.GetNoteColor(5), Is.EqualTo(ColorProfile.DefaultPurple));
        }
    }

    [Test]
    public void BinarySerialization_PreservesRealGuitarColors()
    {
        var source = new ColorProfile("Source");
        source.RealGuitar.LowEFret = Color.FromArgb(255, 10, 20, 30);
        source.RealGuitar.ANote = Color.FromArgb(255, 40, 50, 60);
        source.RealGuitar.DParticles = Color.FromArgb(255, 70, 80, 90);
        source.RealGuitar.HighENoteStarPower = Color.FromArgb(255, 100, 110, 120);
        source.RealGuitar.Metal = Color.FromArgb(255, 130, 140, 150);
        source.RealGuitar.Miss = Color.FromArgb(255, 160, 170, 180);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            source.Serialize(writer);
        }

        stream.Position = 0;
        var restored = new ColorProfile("Restored");
        using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            restored.Deserialize(reader);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.RealGuitar.LowEFret, Is.EqualTo(source.RealGuitar.LowEFret));
            Assert.That(restored.RealGuitar.ANote, Is.EqualTo(source.RealGuitar.ANote));
            Assert.That(restored.RealGuitar.DParticles, Is.EqualTo(source.RealGuitar.DParticles));
            Assert.That(restored.RealGuitar.HighENoteStarPower,
                Is.EqualTo(source.RealGuitar.HighENoteStarPower));
            Assert.That(restored.RealGuitar.Metal, Is.EqualTo(source.RealGuitar.Metal));
            Assert.That(restored.RealGuitar.Miss, Is.EqualTo(source.RealGuitar.Miss));
        }
    }

    [Test]
    public void Deserialize_Version2Profile_LeavesRealGuitarDefaults()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            // Version 2 predates the Real Guitar color section.
            writer.Write(2);
            writer.Write("Legacy");

            new ColorProfile("Source").FiveFretGuitar.Serialize(writer);
            new ColorProfile("Source").SixFretGuitar.Serialize(writer);
            new ColorProfile("Source").FourLaneDrums.Serialize(writer);
            new ColorProfile("Source").FiveLaneDrums.Serialize(writer);
            new ColorProfile("Source").ProKeys.Serialize(writer);
        }

        stream.Position = 0;
        var restored = new ColorProfile("Restored");
        using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            restored.Deserialize(reader);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.Name, Is.EqualTo("Legacy"));
            Assert.That(restored.RealGuitar, Is.Not.Null);
            Assert.That(restored.RealGuitar.LowENote, Is.EqualTo(ColorProfile.DefaultRed));
            Assert.That(restored.RealGuitar.HighENote, Is.EqualTo(ColorProfile.DefaultPurple));
        }
    }
}
