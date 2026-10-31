using System;

namespace YARG.Core.Audio
{
    /// <summary>
    /// Rocksmith detector frame quality bits (+0x7b6 and +0x7c4).
    /// Weak marks a moderate root-level rise after an attack; Strong marks a
    /// larger rise.  They are confidence hints, not note identities.
    /// </summary>
    [Flags]
    public enum ChordQualityFlags : byte
    {
        None = 0,
        /// <summary>Root-level rise is 10-30% of the stored attack level.</summary>
        Weak = 1,
        /// <summary>Root-level rise is greater than 30% of the stored attack level.</summary>
        Strong = 2,
    }
}
