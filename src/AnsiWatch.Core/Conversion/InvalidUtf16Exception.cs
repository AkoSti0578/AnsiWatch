namespace AnsiWatch.Core.Conversion;

/// <summary>Het bestand claimt UTF-16LE te zijn maar bevat ongeldige byte-reeksen.</summary>
public sealed class InvalidUtf16Exception : Exception
{
    /// <summary>Maakt een nieuwe instantie.</summary>
    public InvalidUtf16Exception(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>Maakt een nieuwe instantie.</summary>
    public InvalidUtf16Exception() : this("Ongeldige UTF-16LE-data.") { }

    /// <summary>Maakt een nieuwe instantie.</summary>
    public InvalidUtf16Exception(string message) : this(message, null) { }
}
