namespace AnsiWatch.Core.Configuration;

/// <summary>Eén bewaakte map.</summary>
public sealed class WatchPathOptions
{
    /// <summary>Lokaal pad (D:\Export) of UNC-pad (\\server\share\map).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Ook submappen bewaken.</summary>
    public bool IncludeSubfolders { get; set; }

    /// <summary>Uitgeschakelde paden worden genegeerd maar blijven in de config staan.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maakt een kopie.</summary>
    public WatchPathOptions Clone() => new() { Path = Path, IncludeSubfolders = IncludeSubfolders, Enabled = Enabled };

    /// <inheritdoc />
    public override string ToString() => $"{Path} (submappen: {(IncludeSubfolders ? "ja" : "nee")}, {(Enabled ? "actief" : "uit")})";
}
