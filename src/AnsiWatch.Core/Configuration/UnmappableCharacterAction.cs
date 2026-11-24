namespace AnsiWatch.Core.Configuration;

/// <summary>Wat te doen met tekens die niet in de doel-codepage bestaan.</summary>
public enum UnmappableCharacterAction
{
    /// <summary>Bestand niet converteren, waarschuwing loggen en optioneel naar quarantaine verplaatsen (standaard).</summary>
    Skip = 0,
    /// <summary>Converteren; niet-representeerbare tekens worden '?'.</summary>
    Replace = 1,
    /// <summary>Converteren met Windows best-fit mapping (bijv. Ł → L); wat geen best-fit heeft wordt '?'.</summary>
    BestFit = 2,
}
