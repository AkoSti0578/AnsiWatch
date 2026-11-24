namespace AnsiWatch.Core.Detection;

/// <summary>
/// Bepaalt of een bestand UTF-16LE is. Bewust conservatief: alleen wat zeker UTF-16LE is
/// wordt geconverteerd. ANSI-output van AnsiWatch zelf bevat nooit een FF FE-BOM en vrijwel
/// nooit 0x00-bytes, waardoor de service zijn eigen output nooit opnieuw oppakt (geen loop).
/// </summary>
public static class EncodingDetector
{
    /// <summary>Aantal bytes dat de detector bij voorkeur ontvangt.</summary>
    public const int SampleSize = 4096;

    /// <summary>Minimale fractie tekens met 0x00 op de oneven (hoge) byte voor de heuristiek.</summary>
    public const double MinOddZeroRatio = 0.60;

    /// <summary>Maximale fractie tekens met 0x00 op de even (lage) byte voor de heuristiek.</summary>
    public const double MaxEvenZeroRatio = 0.02;

    /// <summary>Detecteert de codering op basis van de eerste bytes van een bestand.</summary>
    /// <param name="sample">De eerste bytes van het bestand (bij voorkeur <see cref="SampleSize"/>).</param>
    /// <param name="totalLength">De totale bestandsgrootte in bytes.</param>
    /// <param name="detectWithoutBom">Ook UTF-16LE zonder BOM herkennen met een heuristiek.</param>
    public static DetectionResult Detect(ReadOnlySpan<byte> sample, long totalLength, bool detectWithoutBom)
    {
        if (totalLength == 0 || sample.Length == 0)
        {
            return new DetectionResult(TextEncodingKind.Empty, 0);
        }

        // Volgorde is belangrijk: FF FE 00 00 (UTF-32LE) begint óók met FF FE.
        if (sample.Length >= 4 && sample[0] == 0xFF && sample[1] == 0xFE && sample[2] == 0x00 && sample[3] == 0x00)
        {
            return new DetectionResult(TextEncodingKind.Utf32LeBom, 4);
        }

        if (sample.Length >= 4 && sample[0] == 0x00 && sample[1] == 0x00 && sample[2] == 0xFE && sample[3] == 0xFF)
        {
            return new DetectionResult(TextEncodingKind.Utf32BeBom, 4);
        }

        if (sample.Length >= 2 && sample[0] == 0xFF && sample[1] == 0xFE)
        {
            return new DetectionResult(TextEncodingKind.Utf16LeBom, 2);
        }

        if (sample.Length >= 2 && sample[0] == 0xFE && sample[1] == 0xFF)
        {
            return new DetectionResult(TextEncodingKind.Utf16BeBom, 2);
        }

        if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
        {
            return new DetectionResult(TextEncodingKind.Utf8Bom, 3);
        }

        if (detectWithoutBom && LooksLikeUtf16LeWithoutBom(sample, totalLength))
        {
            return new DetectionResult(TextEncodingKind.Utf16LeNoBom, 0);
        }

        return new DetectionResult(TextEncodingKind.Other, 0);
    }

    /// <summary>
    /// Heuristiek: in UTF-16LE-tekst met (overwegend) Latijnse tekens is de hoge byte van elk teken 0x00,
    /// dus staan er veel nullen op oneven posities en (bijna) geen op even posities.
    /// </summary>
    internal static bool LooksLikeUtf16LeWithoutBom(ReadOnlySpan<byte> sample, long totalLength)
    {
        if (totalLength % 2 != 0 || sample.Length < 4)
        {
            return false;
        }

        var pairs = sample.Length / 2;
        var oddZero = 0;
        var evenZero = 0;
        for (var i = 0; i < pairs; i++)
        {
            if (sample[(2 * i) + 1] == 0x00)
            {
                oddZero++;
            }

            if (sample[2 * i] == 0x00)
            {
                evenZero++;
            }
        }

        return oddZero >= pairs * MinOddZeroRatio && evenZero <= pairs * MaxEvenZeroRatio;
    }
}
