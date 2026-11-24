namespace AnsiWatch.Core.Conversion;

/// <summary>Statistieken van één transcodeer- of analysepas.</summary>
public sealed class TranscodeReport
{
    /// <summary>Aantal gelezen UTF-16-code-units (chars).</summary>
    public long CharCount { get; internal set; }

    /// <summary>Aantal regels (aantal LF's + 1, of 0 bij lege tekst).</summary>
    public long LineCount { get; internal set; }

    /// <summary>Aantal geschreven bytes.</summary>
    public long BytesWritten { get; internal set; }

    /// <summary>Gevonden niet-representeerbare tekens (maximaal <c>maxIssues</c>).</summary>
    public List<UnmappableCharacter> Issues { get; } = new();

    /// <summary>True als er meer niet-representeerbare tekens waren dan in <see cref="Issues"/> staan.</summary>
    public bool IssuesTruncated { get; internal set; }

    /// <summary>True als er minstens één niet-representeerbaar teken is.</summary>
    public bool HasUnmappable => Issues.Count > 0;

    /// <summary>SHA-256 over de brontekst (UTF-16 code-units), indien berekend.</summary>
    public byte[]? SourceTextHash { get; internal set; }

    /// <summary>Korte samenvatting van de problemen voor logging.</summary>
    public string DescribeIssues(int max = 10)
    {
        if (Issues.Count == 0)
        {
            return "geen";
        }

        var shown = Issues.Take(max).Select(i => i.ToString());
        var rest = Issues.Count - Math.Min(max, Issues.Count);
        var suffix = rest > 0 || IssuesTruncated
            ? $"; en nog {(IssuesTruncated ? "meer dan " : string.Empty)}{rest} andere"
            : string.Empty;
        return string.Join("; ", shown) + suffix;
    }
}
