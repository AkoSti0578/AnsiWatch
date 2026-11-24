using System.Text;
using AnsiWatch.Core.Text;

namespace AnsiWatch.Tests;

/// <summary>Tijdelijke testmap die na de test opgeruimd wordt.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ansiwatch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class TestText
{
    static TestText() => CodePageSupport.EnsureRegistered();

    /// <summary>Nederlandse tekst met diakrieten, € en gemengde regeleinden; volledig representeerbaar in 1252.</summary>
    public const string Dutch =
        "Één café, één crème brûlée.\r\n" +
        "Ruïne, naïef, coördinatie, reünie – ïndex.\r\n" +
        "Façade en garçon: ç Ç.\n" +
        "Prijs: € 12,50 ‘quote’ “dubbel” … — •\r\n" +
        "  spaties blijven staan  \t\r\n" +
        "Laatste regel zonder regeleinde";

    public static Encoding Ansi1252 => Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    public static byte[] Utf16(string text, bool bom = true)
    {
        var body = Encoding.Unicode.GetBytes(text);
        return bom ? [0xFF, 0xFE, .. body] : body;
    }

    public static string WriteUtf16(string path, string text, bool bom = true)
    {
        System.IO.File.WriteAllBytes(path, Utf16(text, bom));
        return path;
    }
}

internal static class TestModuleInitializer
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => CodePageSupport.EnsureRegistered();
}
