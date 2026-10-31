using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Components.Reader;

/// <summary>Adding or changing a page note: From and To as the reader names pages, To left blank for one page.</summary>
public sealed class NoteDraft
{
    public required string Heading { get; init; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Text { get; set; } = "";
    public ReaderNote? Editing { get; init; }
}

/// <summary>Add pages…: From and To as the reader names pages, a label, and which binder and where in it.</summary>
public sealed class PagesDraft
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Label { get; set; } = "";
    public required IReadOnlyList<BinderChoice> Binders { get; init; }
    public long BinderId { get; set; }
    public IReadOnlyList<BinderSection> Sections { get; set; } = [];

    /// <summary>"" for the end, "0" for before the first section, or a section's id.</summary>
    public string Where { get; set; } = "";

    /// <summary>The section the pages go before, or null for the end.</summary>
    public long? SectionId => long.TryParse(Where, out var where) ? where : null;
}

/// <summary>Mark as adventure, or a contents line's Index as its own adventure…: a title and its pages.</summary>
public sealed class AdventureDraft
{
    public required string Heading { get; init; }
    public string Title { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? Problem { get; set; }
    public bool Saving { get; set; }
}

/// <summary>The password dialog's ticks, kept while a wrong password is typed again.</summary>
public sealed class PasswordTicks
{
    public bool Searchable { get; set; } = true;
    public bool Remember { get; set; }

    /// <summary>Remembering it lets indexing use it too, so a remembered password always makes the text searchable.</summary>
    public bool MakesSearchable => Searchable || Remember;
}
