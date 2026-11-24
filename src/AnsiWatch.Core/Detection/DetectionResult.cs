namespace AnsiWatch.Core.Detection;

/// <summary>Uitkomst van <see cref="EncodingDetector"/>.</summary>
/// <param name="Kind">De herkende codering.</param>
/// <param name="PayloadOffset">Aantal bytes (BOM) dat vóór de tekst staat.</param>
public readonly record struct DetectionResult(TextEncodingKind Kind, int PayloadOffset)
{
    /// <summary>True als het bestand als UTF-16LE geconverteerd moet worden.</summary>
    public bool IsUtf16Le => Kind is TextEncodingKind.Utf16LeBom or TextEncodingKind.Utf16LeNoBom;

    /// <summary>Leesbare omschrijving voor logs en de configuratietool.</summary>
    public string Description => Kind switch
    {
        TextEncodingKind.Empty => "leeg bestand",
        TextEncodingKind.Utf16LeBom => "UTF-16LE (BOM FF FE)",
        TextEncodingKind.Utf16LeNoBom => "UTF-16LE zonder BOM (heuristiek)",
        TextEncodingKind.Utf16BeBom => "UTF-16BE (BOM FE FF)",
        TextEncodingKind.Utf8Bom => "UTF-8 (BOM EF BB BF)",
        TextEncodingKind.Utf32LeBom => "UTF-32LE (BOM FF FE 00 00)",
        TextEncodingKind.Utf32BeBom => "UTF-32BE (BOM 00 00 FE FF)",
        _ => "geen UTF-16LE (ANSI, UTF-8 of anders)",
    };
}
