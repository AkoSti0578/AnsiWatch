using System.Globalization;

namespace AnsiWatch.Core.Conversion;

/// <summary>Een teken dat niet in de doel-codepage past.</summary>
/// <param name="Line">Regelnummer (1-based).</param>
/// <param name="Column">Kolom in UTF-16-tekens (1-based).</param>
/// <param name="CodePoint">Unicode-codepoint van het teken.</param>
public readonly record struct UnmappableCharacter(long Line, long Column, int CodePoint)
{
    /// <summary>Het teken zelf als tekst.</summary>
    public string Character => char.ConvertFromUtf32(CodePoint);

    /// <summary>Notatie zoals U+0141.</summary>
    public string CodePointText => "U+" + CodePoint.ToString("X4", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"regel {Line}, positie {Column}: '{Character}' ({CodePointText})");
}
