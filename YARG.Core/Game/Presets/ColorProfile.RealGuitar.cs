using System.Drawing;
using System.IO;
using YARG.Core.Chart;
using YARG.Core.Extensions;
using YARG.Core.Utility;

namespace YARG.Core.Game
{
    public partial class ColorProfile
    {
        /// <summary>
        /// String colors for audio-input Real Guitar. The order follows the
        /// physical strings from low E to high E:
        /// low E red, A yellow, D blue, G orange, B green, high E purple.
        ///
        /// Pro Guitar's string enum uses color names rather than note names, so
        /// the lookup below deliberately maps by string index instead of by
        /// the enum's display name.
        /// </summary>
        public class RealGuitarColors : IFretColorProvider, IBinarySerializable
        {
            #region Frets

            public Color LowEFret   = DefaultRed;
            public Color AFret      = DefaultYellow;
            public Color DFret      = DefaultBlue;
            public Color GFret      = DefaultOrange;
            public Color BFret      = DefaultGreen;
            public Color HighEFret  = DefaultPurple;

            public Color GetFretColor(int index)
            {
                return index switch
                {
                    (int) ProGuitarString.Red    => LowEFret,
                    (int) ProGuitarString.Green  => AFret,
                    (int) ProGuitarString.Orange => DFret,
                    (int) ProGuitarString.Blue   => GFret,
                    (int) ProGuitarString.Yellow => BFret,
                    (int) ProGuitarString.Purple => HighEFret,
                    _ => default
                };
            }

            public Color LowEFretInner   = DefaultRed;
            public Color AFretInner      = DefaultYellow;
            public Color DFretInner      = DefaultBlue;
            public Color GFretInner      = DefaultOrange;
            public Color BFretInner      = DefaultGreen;
            public Color HighEFretInner  = DefaultPurple;

            public Color GetFretInnerColor(int index)
            {
                return index switch
                {
                    (int) ProGuitarString.Red    => LowEFretInner,
                    (int) ProGuitarString.Green  => AFretInner,
                    (int) ProGuitarString.Orange => DFretInner,
                    (int) ProGuitarString.Blue   => GFretInner,
                    (int) ProGuitarString.Yellow => BFretInner,
                    (int) ProGuitarString.Purple => HighEFretInner,
                    _ => default
                };
            }

            public Color LowEParticles  = DefaultRed;
            public Color AParticles     = DefaultYellow;
            public Color DParticles     = DefaultBlue;
            public Color GParticles     = DefaultOrange;
            public Color BParticles     = DefaultGreen;
            public Color HighEParticles = DefaultPurple;

            public Color GetParticleColor(int index)
            {
                return index switch
                {
                    (int) ProGuitarString.Red    => LowEParticles,
                    (int) ProGuitarString.Green  => AParticles,
                    (int) ProGuitarString.Orange => DParticles,
                    (int) ProGuitarString.Blue   => GParticles,
                    (int) ProGuitarString.Yellow => BParticles,
                    (int) ProGuitarString.Purple => HighEParticles,
                    _ => default
                };
            }

            #endregion

            #region Notes

            public Color LowENote  = DefaultRed;
            public Color ANote     = DefaultYellow;
            public Color DNote     = DefaultBlue;
            public Color GNote     = DefaultOrange;
            public Color BNote     = DefaultGreen;
            public Color HighENote = DefaultPurple;

            public Color GetNoteColor(int index)
            {
                return index switch
                {
                    (int) ProGuitarString.Red    => LowENote,
                    (int) ProGuitarString.Green  => ANote,
                    (int) ProGuitarString.Orange => DNote,
                    (int) ProGuitarString.Blue   => GNote,
                    (int) ProGuitarString.Yellow => BNote,
                    (int) ProGuitarString.Purple => HighENote,
                    _ => default
                };
            }

            public Color LowENoteStarPower  = DefaultStarpower;
            public Color ANoteStarPower     = DefaultStarpower;
            public Color DNoteStarPower     = DefaultStarpower;
            public Color GNoteStarPower     = DefaultStarpower;
            public Color BNoteStarPower     = DefaultStarpower;
            public Color HighENoteStarPower = DefaultStarpower;

            public Color GetNoteStarPowerColor(int index)
            {
                return index switch
                {
                    (int) ProGuitarString.Red    => LowENoteStarPower,
                    (int) ProGuitarString.Green  => ANoteStarPower,
                    (int) ProGuitarString.Orange => DNoteStarPower,
                    (int) ProGuitarString.Blue   => GNoteStarPower,
                    (int) ProGuitarString.Yellow => BNoteStarPower,
                    (int) ProGuitarString.Purple => HighENoteStarPower,
                    _ => default
                };
            }

            #endregion

            #region General

            public Color Metal          = DefaultMetal;
            public Color MetalStarPower = DefaultMetalStarPower;
            public Color Miss           = DefaultMiss;

            public Color GetMetalColor(bool isForStarPower)
            {
                return isForStarPower ? MetalStarPower : Metal;
            }

            #endregion

            #region Serialization

            public RealGuitarColors Copy()
            {
                return (RealGuitarColors) MemberwiseClone();
            }

            public void Serialize(BinaryWriter writer)
            {
                writer.Write(LowEFret);
                writer.Write(AFret);
                writer.Write(DFret);
                writer.Write(GFret);
                writer.Write(BFret);
                writer.Write(HighEFret);

                writer.Write(LowEFretInner);
                writer.Write(AFretInner);
                writer.Write(DFretInner);
                writer.Write(GFretInner);
                writer.Write(BFretInner);
                writer.Write(HighEFretInner);

                writer.Write(LowEParticles);
                writer.Write(AParticles);
                writer.Write(DParticles);
                writer.Write(GParticles);
                writer.Write(BParticles);
                writer.Write(HighEParticles);

                writer.Write(LowENote);
                writer.Write(ANote);
                writer.Write(DNote);
                writer.Write(GNote);
                writer.Write(BNote);
                writer.Write(HighENote);

                writer.Write(LowENoteStarPower);
                writer.Write(ANoteStarPower);
                writer.Write(DNoteStarPower);
                writer.Write(GNoteStarPower);
                writer.Write(BNoteStarPower);
                writer.Write(HighENoteStarPower);

                writer.Write(Metal);
                writer.Write(MetalStarPower);
                writer.Write(Miss);
            }

            public void Deserialize(BinaryReader reader, int version = 0)
            {
                LowEFret = reader.ReadColor();
                AFret = reader.ReadColor();
                DFret = reader.ReadColor();
                GFret = reader.ReadColor();
                BFret = reader.ReadColor();
                HighEFret = reader.ReadColor();

                LowEFretInner = reader.ReadColor();
                AFretInner = reader.ReadColor();
                DFretInner = reader.ReadColor();
                GFretInner = reader.ReadColor();
                BFretInner = reader.ReadColor();
                HighEFretInner = reader.ReadColor();

                LowEParticles = reader.ReadColor();
                AParticles = reader.ReadColor();
                DParticles = reader.ReadColor();
                GParticles = reader.ReadColor();
                BParticles = reader.ReadColor();
                HighEParticles = reader.ReadColor();

                LowENote = reader.ReadColor();
                ANote = reader.ReadColor();
                DNote = reader.ReadColor();
                GNote = reader.ReadColor();
                BNote = reader.ReadColor();
                HighENote = reader.ReadColor();

                LowENoteStarPower = reader.ReadColor();
                ANoteStarPower = reader.ReadColor();
                DNoteStarPower = reader.ReadColor();
                GNoteStarPower = reader.ReadColor();
                BNoteStarPower = reader.ReadColor();
                HighENoteStarPower = reader.ReadColor();

                Metal = reader.ReadColor();
                MetalStarPower = reader.ReadColor();
                Miss = reader.ReadColor();
            }

            #endregion
        }
    }
}
