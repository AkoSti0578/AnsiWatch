namespace AnsiWatch.Core.Detection;

/// <summary>Resultaat van de encodingdetectie.</summary>
public enum TextEncodingKind
{
    /// <summary>Bestand van 0 bytes: niets te converteren.</summary>
    Empty,
    /// <summary>UTF-16 little endian met BOM FF FE.</summary>
    Utf16LeBom,
    /// <summary>UTF-16 little endian zonder BOM (heuristisch herkend).</summary>
    Utf16LeNoBom,
    /// <summary>UTF-16 big endian met BOM FE FF (wordt niet geconverteerd).</summary>
    Utf16BeBom,
    /// <summary>UTF-8 met BOM EF BB BF (wordt niet geconverteerd).</summary>
    Utf8Bom,
    /// <summary>UTF-32 little endian met BOM FF FE 00 00 (wordt niet geconverteerd).</summary>
    Utf32LeBom,
    /// <summary>UTF-32 big endian met BOM 00 00 FE FF (wordt niet geconverteerd).</summary>
    Utf32BeBom,
    /// <summary>Iets anders: ANSI, UTF-8 zonder BOM, binair, ... (wordt niet geconverteerd).</summary>
    Other,
}
