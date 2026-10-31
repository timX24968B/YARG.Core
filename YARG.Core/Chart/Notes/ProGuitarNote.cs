using System;

namespace YARG.Core.Chart
{
    public class ProGuitarNote : Note<ProGuitarNote>
    {
        private ProGuitarNoteFlags _proFlags;
        public ProGuitarNoteFlags ProFlags;

        public int String   { get; }
        public int Fret     { get; }

        public ProGuitarNoteType Type { get; set; }

        public bool IsStrum => Type == ProGuitarNoteType.Strum;
        public bool IsHopo  => Type == ProGuitarNoteType.Hopo;
        public bool IsTap   => Type == ProGuitarNoteType.Tap;

        public bool IsSustain => TickLength > 0;

        public bool IsExtendedSustain => (ProFlags & ProGuitarNoteFlags.ExtendedSustain) != 0;
        public bool IsDisjoint        => (ProFlags & ProGuitarNoteFlags.Disjoint) != 0;

        public bool IsMuted => (ProFlags & ProGuitarNoteFlags.Muted) != 0;

        /// <summary>
        /// Rocksmith NOTE_MASK_FRETHANDMUTE (0x00000008).  On a chord this
        /// uses the immediate detector-state query, not the normal chord
        /// matcher.
        /// </summary>
        public bool IsFretHandMute => (ProFlags & ProGuitarNoteFlags.FretHandMute) != 0;

        /// <summary>
        /// Rocksmith NOTE_MASK_IGNORE (0x00040000).  On a chord this uses the
        /// immediate detector-state query; on a single note it uses the
        /// onset-only query.
        /// </summary>
        public bool IsIgnored => (ProFlags & ProGuitarNoteFlags.Ignore) != 0;

        /// <summary>Rocksmith NOTE_MASK_HARMONIC (0x00000020).</summary>
        public bool IsHarmonic => (ProFlags & ProGuitarNoteFlags.Harmonic) != 0;

        /// <summary>Rocksmith unpitched-slide mask (0x00400000).</summary>
        public bool IsSlideUnpitched => (ProFlags & ProGuitarNoteFlags.SlideUnpitched) != 0;

        /// <summary>
        /// Whether this note is a palm mute (Rocksmith NOTE_MASK_PALMMUTE).
        /// This is separate from <see cref="IsMuted"/>: the source uses the
        /// palm-mute 6 dB drop query here and the onset-only mute query for
        /// NOTE_MASK_MUTE.
        /// </summary>
        public bool IsPalmMuted => (ProFlags & ProGuitarNoteFlags.PalmMuted) != 0;

        /// <summary>
        /// Whether this note lies inside a Pro Guitar arpeggio phrase.
        /// </summary>
        public bool IsArpeggio => (ProFlags & ProGuitarNoteFlags.Arpeggio) != 0;

        public ProGuitarNote(ProGuitarString proString, int proFret, ProGuitarNoteType type, ProGuitarNoteFlags proFlags,
            NoteFlags flags, double time, double timeLength, uint tick, uint tickLength)
            : this((int) proString, proFret, type, proFlags, flags, time, timeLength, tick, tickLength)
        {
        }

        public ProGuitarNote(int proString, int proFret, ProGuitarNoteType type, ProGuitarNoteFlags proFlags,
            NoteFlags flags, double time, double timeLength, uint tick, uint tickLength)
            : base(flags, time, timeLength, tick, tickLength)
        {
            String = proString;
            Fret = proFret;
            Type = type;

            ProFlags = _proFlags = proFlags;
        }

        public ProGuitarNote(ProGuitarNote other) : base(other)
        {
            String = other.String;
            Fret = other.Fret;
            Type = other.Type;

            ProFlags = _proFlags = other._proFlags;
        }

        public override void AddChildNote(ProGuitarNote note)
        {
            // TODO Check if string+fret already exists in the parent and skip adding if it does

            base.AddChildNote(note);
        }

        public override void ResetNoteState()
        {
            base.ResetNoteState();
            ProFlags = _proFlags;
        }

        protected override void CopyFlags(ProGuitarNote other)
        {
            _proFlags = other._proFlags;
            ProFlags = other.ProFlags;

            Type = other.Type;
        }

        protected override ProGuitarNote CloneNote()
        {
            return new(this);
        }
    }

    public enum ProGuitarString
    {
        Red,
        Green,
        Orange,
        Blue,
        Yellow,
        Purple,
    }

    public enum ProGuitarNoteType
    {
        Strum,
        Hopo,
        Tap,
    }

    [Flags]
    public enum ProGuitarNoteFlags
    {
        None = 0,

        ExtendedSustain = 1 << 0,
        Disjoint        = 1 << 1,

        Muted = 1 << 2, // Rocksmith NOTE_MASK_MUTE: source uses the onset-only query.
        PalmMuted = 1 << 4, // Rocksmith NOTE_MASK_PALMMUTE: source uses the 6 dB query.
        FretHandMute = 1 << 5, // Rocksmith NOTE_MASK_FRETHANDMUTE (0x8).
        Ignore = 1 << 6, // Rocksmith NOTE_MASK_IGNORE (0x40000).
        Harmonic = 1 << 7, // Rocksmith NOTE_MASK_HARMONIC (0x20).
        SlideUnpitched = 1 << 8, // Rocksmith 0x400000 immediate-state branch.

        /// <summary>
        /// Arpeggio note branch (Rocksmith note mask 0x20000000).  The audio
        /// query uses the root/onset pitch path rather than the top-two note
        /// candidate path.
        /// </summary>
        Arpeggio = 1 << 3,
    }
}