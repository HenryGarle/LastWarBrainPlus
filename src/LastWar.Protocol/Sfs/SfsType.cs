namespace LastWar.Protocol.Sfs;

/// <summary>SFS2X value type tags, as they appear on the wire.</summary>
public enum SfsType : byte
{
    Null = 0,
    Bool = 1,
    Byte = 2,
    Short = 3,
    Int = 4,
    Long = 5,
    Float = 6,
    Double = 7,
    UtfString = 8,
    BoolArray = 9,
    ByteArray = 10,
    ShortArray = 11,
    IntArray = 12,
    LongArray = 13,
    FloatArray = 14,
    DoubleArray = 15,
    UtfStringArray = 16,
    SfsArray = 17,
    SfsObject = 18,
    /// <summary>Declared by the protocol, never sent by this game.</summary>
    Class = 19,
    Text = 20,
}
