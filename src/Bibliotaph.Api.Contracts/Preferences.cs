namespace Bibliotaph.Api.Contracts;

/// <summary>
/// Settings › Library below the folders: the folder names read as labels and the folder rules
/// answered in Needs review, each with its switch, and where Bibliotaph starts, which Preferences shows.
/// </summary>
public sealed record LibraryPreferences(bool StartOnLibrary, IReadOnlyList<FolderRuleSwitch> FolderRules);


/// <summary>A folder rule answered in Needs review: "Fey Court" → "system Pathfinder 2e", and where that folder is.</summary>
public sealed record FolderRuleSwitch(long Id, string Folder, string Meaning, string Detail, bool Enabled);

public sealed record StartPageChoice(bool Library);

/// <summary>How far the first-run tour has gone: null before it, then "folders" and "books".</summary>
public sealed record TourProgress(string? Stage);


public sealed record RuleSwitch(bool Enabled);
