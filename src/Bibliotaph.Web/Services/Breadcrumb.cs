namespace Bibliotaph.Web.Services;

/// <summary>
/// What the top bar names after the section, as the Windows app's breadcrumb: "Library / " and the book open in the
/// reader. A page sets it while it shows and clears it as it goes.
/// </summary>
public sealed class Breadcrumb
{
    public string? Title { get; private set; }

    public event Action? Changed;

    public void Show(string? title)
    {
        if (title == Title) return;
        Title = title;
        Changed?.Invoke();
    }
}
