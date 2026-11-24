using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnsiWatch.Core.Configuration;

/// <summary>Lezen en (atomair) schrijven van config.json.</summary>
public static class ConfigStore
{
    /// <summary>JSON-opties die door service én configuratietool gebruikt worden.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    /// <summary>Leest de configuratie. Ontbreekt het bestand, dan volgen de standaardwaarden.</summary>
    /// <exception cref="ConfigException">Het bestand is geen geldige JSON.</exception>
    public static AnsiWatchOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AnsiWatchOptions();
        }

        string json;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true))
        {
            json = reader.ReadToEnd();
        }

        return Parse(json, path);
    }

    /// <summary>Parseert JSON-tekst naar opties.</summary>
    public static AnsiWatchOptions Parse(string json, string source = "config.json")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AnsiWatchOptions();
        }

        try
        {
            var options = JsonSerializer.Deserialize<AnsiWatchOptions>(json, JsonOptions) ?? new AnsiWatchOptions();
            options.WatchPaths ??= new List<WatchPathOptions>();
            options.WatchPaths.RemoveAll(p => p is null);
            return options;
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (regel {line + 1}, positie {ex.BytePositionInLine + 1})" : string.Empty;
            throw new ConfigException($"{source} bevat ongeldige JSON{where}: {ex.Message}", ex);
        }
    }

    /// <summary>Serialiseert opties naar JSON.</summary>
    public static string Serialize(AnsiWatchOptions options) => JsonSerializer.Serialize(options, JsonOptions);

    /// <summary>Schrijft atomair: eerst naar een tijdelijk bestand, daarna vervangen.</summary>
    public static void Save(string path, AnsiWatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, Serialize(options) + Environment.NewLine);
            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>Fout in de configuratie.</summary>
public sealed class ConfigException : Exception
{
    /// <summary>Maakt een nieuwe instantie.</summary>
    public ConfigException(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>Maakt een nieuwe instantie.</summary>
    public ConfigException() : this("Ongeldige configuratie.") { }

    /// <summary>Maakt een nieuwe instantie.</summary>
    public ConfigException(string message) : this(message, null) { }
}
