using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Conversion;

namespace AnsiWatch.Tests;

public sealed class ConfigTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var path = _dir.File("config.json");
        var options = new AnsiWatchOptions
        {
            CodePage = 1250,
            UnmappableCharacterAction = UnmappableCharacterAction.BestFit,
            DetectWithoutBom = true,
            BackupFolder = @"D:\Backup",
            WatchPaths = { new WatchPathOptions { Path = @"\\server\share\export", IncludeSubfolders = true } },
        };

        ConfigStore.Save(path, options);
        var loaded = ConfigStore.Load(path);

        Assert.Equal(1250, loaded.CodePage);
        Assert.Equal(UnmappableCharacterAction.BestFit, loaded.UnmappableCharacterAction);
        Assert.True(loaded.DetectWithoutBom);
        Assert.Equal(@"\\server\share\export", Assert.Single(loaded.WatchPaths).Path);
        Assert.Contains("\"BestFit\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_IsLenient_CaseCommentsTrailingCommas()
    {
        const string json = """
            {
              // commentaar mag
              "codepage": 1252,
              "unmappableCharacterAction": "replace",
              "watchPaths": [ { "path": "C:\\Export", }, ],
            }
            """;
        var options = ConfigStore.Parse(json);
        Assert.Equal(UnmappableCharacterAction.Replace, options.UnmappableCharacterAction);
        Assert.Equal(@"C:\Export", options.WatchPaths[0].Path);
        Assert.True(options.WatchPaths[0].Enabled);
        Assert.True(options.PreserveTimestamps);
    }

    [Fact]
    public void Parse_InvalidJson_GivesLineNumber()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigStore.Parse("{\n  \"CodePage\": ,\n}"));
        Assert.Contains("regel 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingFile_GivesDefaults()
    {
        var options = ConfigStore.Load(_dir.File("bestaat-niet.json"));
        Assert.Equal(1252, options.CodePage);
        Assert.Equal(UnmappableCharacterAction.Skip, options.UnmappableCharacterAction);
        Assert.Equal(30, options.RescanIntervalSeconds);
    }

    [Theory]
    [InlineData(65001)]
    [InlineData(1200)]
    [InlineData(99999)]
    public void Validate_RejectsUnicodeOrUnknownCodePages(int codePage)
    {
        var issues = OptionsValidator.Validate(new AnsiWatchOptions { CodePage = codePage });
        Assert.False(OptionsValidator.IsValid(issues));
    }

    [Fact]
    public void Validate_DefaultsAreValid_WithWarningForNoPaths()
    {
        var issues = OptionsValidator.Validate(new AnsiWatchOptions());
        Assert.True(OptionsValidator.IsValid(issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void Validate_DetectsDuplicatesAndRelativePaths()
    {
        var root = _dir.Path;
        var options = new AnsiWatchOptions
        {
            WatchPaths =
            {
                new WatchPathOptions { Path = root },
                new WatchPathOptions { Path = root + Path.DirectorySeparatorChar },
                new WatchPathOptions { Path = "relatief" },
            },
            RescanIntervalSeconds = 1,
            LogLevel = "Chatty",
        };
        var errors = OptionsValidator.Validate(options).Where(i => i.Severity == ValidationSeverity.Error).ToList();
        Assert.Contains(errors, e => e.Message.Contains("dubbel", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("relatief", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("RescanIntervalSeconds", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("LogLevel", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WarnsWhenQuarantineInsideWatchedPath()
    {
        var options = new AnsiWatchOptions
        {
            WatchPaths = { new WatchPathOptions { Path = _dir.Path } },
            QuarantineFolder = Path.Combine(_dir.Path, "q"),
        };
        Assert.Contains(OptionsValidator.Validate(options), i => i.Message.Contains("QuarantineFolder", StringComparison.Ordinal));
    }

    [Fact]
    public void InitialConfig_IsCreatedOnce_FromSemicolonList()
    {
        var path = _dir.File("init.json");
        Assert.True(ConfigInitializer.CreateInitialConfig(path, "\"D:\\Export\\\"; \\\\srv\\share ;D:\\Export", includeSubfolders: false, force: false));
        var options = ConfigStore.Load(path);
        Assert.Equal(new[] { @"D:\Export", @"\\srv\share" }, options.WatchPaths.Select(p => p.Path));

        // Bij een upgrade/herinstallatie blijft een bestaande config behouden.
        Assert.False(ConfigInitializer.CreateInitialConfig(path, @"E:\Anders", includeSubfolders: false, force: false));
        Assert.Equal(2, ConfigStore.Load(path).WatchPaths.Count);
    }

    [Fact]
    public void ToConversionSettings_MapsValues()
    {
        var s = new AnsiWatchOptions { CodePage = 1250, BackupFolder = " ", QuarantineFolder = @"Q:\x", LockTimeoutSeconds = 5 }.ToConversionSettings();
        Assert.Equal(1250, s.CodePage);
        Assert.Null(s.BackupFolder);
        Assert.Equal(@"Q:\x", s.QuarantineFolder);
        Assert.Equal(TimeSpan.FromSeconds(5), s.LockTimeout);
    }

    [Fact]
    public void PathUtil_Candidates()
    {
        Assert.True(PathUtil.IsCandidateFile("a.txt"));
        Assert.True(PathUtil.IsCandidateFile("A.TXT"));
        Assert.False(PathUtil.IsCandidateFile("a.txt.1234abcd.ansiwatch.tmp"));
        Assert.False(PathUtil.IsCandidateFile("a.csv"));
    }
}
