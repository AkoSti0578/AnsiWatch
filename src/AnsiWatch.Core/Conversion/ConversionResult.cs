using System.Globalization;
using AnsiWatch.Core.Detection;

namespace AnsiWatch.Core.Conversion;

/// <summary>Volledig resultaat van <see cref="FileConverter.ConvertAsync"/>.</summary>
public sealed class ConversionResult
{
    /// <summary>Pad van het bestand.</summary>
    public required string Path { get; init; }

    /// <summary>Uitkomst.</summary>
    public ConversionStatus Status { get; internal set; }

    /// <summary>Gedetecteerde codering (indien bepaald).</summary>
    public DetectionResult? Detection { get; internal set; }

    /// <summary>Grootte van het origineel in bytes.</summary>
    public long OriginalSize { get; internal set; }

    /// <summary>Grootte na conversie in bytes.</summary>
    public long NewSize { get; internal set; }

    /// <summary>Totale duur, inclusief wachten op het bestand.</summary>
    public TimeSpan Duration { get; internal set; }

    /// <summary>Tijd besteed aan wachten tot het bestand vrij en stabiel was.</summary>
    public TimeSpan WaitDuration { get; internal set; }

    /// <summary>Niet-representeerbare tekens (indien gevonden).</summary>
    public IReadOnlyList<UnmappableCharacter> Issues { get; internal set; } = Array.Empty<UnmappableCharacter>();

    /// <summary>Er waren meer problemen dan gerapporteerd.</summary>
    public bool IssuesTruncated { get; internal set; }

    /// <summary>Pad van de back-up (indien gemaakt).</summary>
    public string? BackupPath { get; internal set; }

    /// <summary>Pad in de quarantainemap (indien verplaatst).</summary>
    public string? QuarantinePath { get; internal set; }

    /// <summary>Of de conversie na het schrijven geverifieerd is.</summary>
    public bool Verified { get; internal set; }

    /// <summary>Aanvullende meldingen (fallbacks, waarschuwingen).</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Foutmelding bij een mislukte conversie.</summary>
    public string? Error { get; internal set; }

    /// <summary>Onderliggende exceptie bij een mislukte conversie.</summary>
    public Exception? Exception { get; internal set; }

    /// <summary>Samenvatting van de problemen.</summary>
    public string DescribeIssues(int max = 10)
    {
        if (Issues.Count == 0)
        {
            return "geen";
        }

        var shown = string.Join("; ", Issues.Take(max).Select(i => i.ToString()));
        var rest = Issues.Count - Math.Min(max, Issues.Count);
        return rest > 0 || IssuesTruncated
            ? string.Create(CultureInfo.InvariantCulture, $"{shown}; en nog {(IssuesTruncated ? "meer dan " : string.Empty)}{rest} andere")
            : shown;
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Path}: {Status.ToDutch()} ({OriginalSize} → {NewSize} bytes, {Duration.TotalMilliseconds:F0} ms)");
}
