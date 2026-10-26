namespace Bibliotaph.Api.Contracts;

/// <summary>
/// Settings › Library below the folders, as the Windows app's: where Bibliotaph starts, the folder names read as labels,
/// and the folder rules answered in Needs review, each with its switch.
/// </summary>
public sealed record LibraryPreferences(bool StartOnLibrary, IReadOnlyList<FolderNameLabel> FolderNames, IReadOnlyList<FolderRuleSwitch> FolderRules);

/// <summary>A folder name read as a label: "Adventures" → "Type: Adventure", under how many documents.</summary>
public sealed record FolderNameLabel(string Folder, string Vocabulary, string Term, string Meaning, string Detail, bool Enabled);

/// <summary>A folder rule answered in Needs review: "Fey Court" → "system Pathfinder 2e", and where that folder is.</summary>
public sealed record FolderRuleSwitch(long Id, string Folder, string Meaning, string Detail, bool Enabled);

public sealed record StartPageChoice(bool Library);

public sealed record FolderNameSwitch(string Folder, string Vocabulary, string Term, bool Enabled);

public sealed record RuleSwitch(bool Enabled);
