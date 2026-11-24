using System.Text;

namespace AnsiWatch.Core.Text;

/// <summary>
/// Centrale toegang tot de Windows-codepages. In .NET 8 zijn de "ANSI"-codepages
/// (zoals 1252) pas beschikbaar nadat <see cref="CodePagesEncodingProvider"/> is geregistreerd.
/// </summary>
public static class CodePageSupport
{
    private static int _registered;

    /// <summary>Codepages die nooit als doel mogen dienen: hun output zou weer als Unicode herkend kunnen worden.</summary>
    private static readonly HashSet<int> UnicodeCodePages = new() { 1200, 1201, 12000, 12001, 65000, 65001 };

    /// <summary>Registreert de CodePagesEncodingProvider (idempotent en thread-safe).</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }

    /// <summary>Geeft de doel-encoding met de opgegeven encoder-fallback.</summary>
    public static Encoding GetEncoding(int codePage, EncoderFallback encoderFallback)
    {
        EnsureRegistered();
        return Encoding.GetEncoding(codePage, encoderFallback, DecoderFallback.ReplacementFallback);
    }

    /// <summary>Geeft de standaard-encoding (met Windows best-fit mapping waar de codepage die kent).</summary>
    public static Encoding GetDefaultEncoding(int codePage)
    {
        EnsureRegistered();
        return Encoding.GetEncoding(codePage);
    }

    /// <summary>Controleert of de codepage bruikbaar is als ANSI-doel.</summary>
    public static bool IsValidTargetCodePage(int codePage, out string? error)
    {
        if (UnicodeCodePages.Contains(codePage))
        {
            error = $"Codepage {codePage} is een Unicode-codering en kan niet als ANSI-doel gebruikt worden.";
            return false;
        }

        try
        {
            GetDefaultEncoding(codePage);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error = $"Codepage {codePage} wordt niet ondersteund.";
            return false;
        }
    }

    /// <summary>Veelgebruikte ANSI-codepages, voor keuzelijsten in de configuratietool.</summary>
    public static IReadOnlyList<(int CodePage, string Name)> CommonAnsiCodePages { get; } = new[]
    {
        (1252, "Windows-1252 – West-Europees (standaard)"),
        (1250, "Windows-1250 – Centraal-Europees"),
        (1251, "Windows-1251 – Cyrillisch"),
        (1253, "Windows-1253 – Grieks"),
        (1254, "Windows-1254 – Turks"),
        (1255, "Windows-1255 – Hebreeuws"),
        (1256, "Windows-1256 – Arabisch"),
        (1257, "Windows-1257 – Baltisch"),
        (1258, "Windows-1258 – Vietnamees"),
        (874, "Windows-874 – Thai"),
        (932, "Shift-JIS (932) – Japans"),
        (936, "GBK (936) – Vereenvoudigd Chinees"),
        (949, "UHC (949) – Koreaans"),
        (950, "Big5 (950) – Traditioneel Chinees"),
        (437, "OEM 437 – VS (DOS)"),
        (850, "OEM 850 – West-Europees (DOS)"),
        (28591, "ISO-8859-1 – Latin-1"),
        (28605, "ISO-8859-15 – Latin-9"),
    };
}
