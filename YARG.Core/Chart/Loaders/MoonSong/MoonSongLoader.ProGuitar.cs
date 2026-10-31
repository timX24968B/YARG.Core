using System;
using System.Collections.Generic;
using MoonscraperChartEditor.Song;

namespace YARG.Core.Chart
{
    internal partial class MoonSongLoader : ISongLoader
    {
        public InstrumentTrack<ProGuitarNote> LoadProGuitarTrack(Instrument instrument)
        {
            if (instrument.ToNativeGameMode() != GameMode.ProGuitar)
                throw new ArgumentException($"Instrument {instrument} is not a Pro guitar instrument!", nameof(instrument));

            var difficulties = new Dictionary<Difficulty, InstrumentDifficulty<ProGuitarNote>>()
            {
                { Difficulty.Easy, LoadDifficulty<ProGuitarNote>(instrument, Difficulty.Easy, CreateProGuitarNote) },
                { Difficulty.Medium, LoadDifficulty<ProGuitarNote>(instrument, Difficulty.Medium, CreateProGuitarNote) },
                { Difficulty.Hard, LoadDifficulty<ProGuitarNote>(instrument, Difficulty.Hard, CreateProGuitarNote) },
                { Difficulty.Expert, LoadDifficulty<ProGuitarNote>(instrument, Difficulty.Expert, CreateProGuitarNote) },
            };
            return new(instrument, difficulties);
        }

        private ProGuitarNote CreateProGuitarNote(MoonNote moonNote, Dictionary<MoonPhrase.Type, MoonPhrase> currentPhrases,
            List<ProGuitarNote> notes)
        {
            var proString = GetProGuitarString(moonNote);
            int proFret = GetProGuitarFret(moonNote);
            var noteType = GetProGuitarNoteType(moonNote);
            var generalFlags = GetGeneralFlags(moonNote, currentPhrases);
            var proFlags = GetProGuitarNoteFlags(moonNote, currentPhrases);

            double time = _moonSong.TickToTime(moonNote.tick);
            return new ProGuitarNote(proString, proFret, noteType, proFlags, generalFlags, time, GetLengthInTime(moonNote), moonNote.tick, moonNote.length);
        }

        private ProGuitarString GetProGuitarString(MoonNote moonNote)
        {
            return moonNote.proGuitarString switch
            {
                MoonNote.ProGuitarString.Red    => ProGuitarString.Red,
                MoonNote.ProGuitarString.Green  => ProGuitarString.Green,
                MoonNote.ProGuitarString.Orange => ProGuitarString.Orange,
                MoonNote.ProGuitarString.Blue   => ProGuitarString.Blue,
                MoonNote.ProGuitarString.Yellow => ProGuitarString.Yellow,
                MoonNote.ProGuitarString.Purple => ProGuitarString.Purple,
                _ => throw new InvalidOperationException($"Unhandled Moonscraper Pro guitar string {moonNote.proGuitarString}!")
            };
        }

        private int GetProGuitarFret(MoonNote moonNote)
        {
            return moonNote.proGuitarFret;
        }

        private ProGuitarNoteType GetProGuitarNoteType(MoonNote moonNote)
        {
            var type = moonNote.GetGuitarNoteType(_moonSong.hopoThreshold, _moonSong.UsesMidiNaturalHopoSubsetException);
            return type switch
            {
                MoonNote.MoonNoteType.Strum => ProGuitarNoteType.Strum,
                MoonNote.MoonNoteType.Hopo  => ProGuitarNoteType.Hopo,
                MoonNote.MoonNoteType.Tap   => ProGuitarNoteType.Tap,
                _ => throw new InvalidOperationException($"Unhandled Moonscraper note type {type}!")
            };
        }

        private ProGuitarNoteFlags GetProGuitarNoteFlags(MoonNote moonNote,
            Dictionary<MoonPhrase.Type, MoonPhrase> currentPhrases)
        {
            var flags = ProGuitarNoteFlags.None;

            // Standard guitar flags
            var guitarFlags = GetGuitarNoteFlags(moonNote);
            if ((guitarFlags & GuitarNoteFlags.ExtendedSustain) != 0)
                flags |= ProGuitarNoteFlags.ExtendedSustain;
            if ((guitarFlags & GuitarNoteFlags.Disjoint) != 0)
                flags |= ProGuitarNoteFlags.Disjoint;

            // Muted notes.  MIDI channel 3 is the Rock Band muted channel
            // and maps to Rocksmith NOTE_MASK_MUTE (the onset-only query),
            // not NOTE_MASK_PALMMUTE.
            if ((moonNote.flags & MoonNote.Flags.ProGuitar_Muted) != 0)
            {
                flags |= ProGuitarNoteFlags.Muted;
            }

            // RS-native palm-mute marker.  MIDI channel 3 does not set this;
            // a future SNG/RS chart importer can provide it.
            if ((moonNote.flags & MoonNote.Flags.ProGuitar_PalmMute) != 0)
            {
                flags |= ProGuitarNoteFlags.PalmMuted;
            }

            // Rocksmith technique/article masks.  Rock Band MIDI does not
            // encode these channels, but RS-native importers can set them.
            if ((moonNote.flags & MoonNote.Flags.ProGuitar_FretHandMute) != 0)
            {
                flags |= ProGuitarNoteFlags.FretHandMute;
            }
            if ((moonNote.flags & MoonNote.Flags.ProGuitar_Ignore) != 0)
            {
                flags |= ProGuitarNoteFlags.Ignore;
            }
            if ((moonNote.flags & MoonNote.Flags.ProGuitar_Harmonic) != 0)
            {
                flags |= ProGuitarNoteFlags.Harmonic;
            }
            if ((moonNote.flags & MoonNote.Flags.ProGuitar_SlideUnpitched) != 0)
            {
                flags |= ProGuitarNoteFlags.SlideUnpitched;
            }

            // Arpeggio phrase markers (MIDI notes 104/80/56/32 on channel 1).
            // The marker covers every chart note inside its range; those notes
            // use the root-pitch query instead of the normal attack query.
            if (currentPhrases.TryGetValue(MoonPhrase.Type.ProGuitar_Arpeggio, out var arpeggio) &&
                IsEventInPhrase(moonNote, arpeggio))
            {
                flags |= ProGuitarNoteFlags.Arpeggio;
            }

            return flags;
        }
    }
}