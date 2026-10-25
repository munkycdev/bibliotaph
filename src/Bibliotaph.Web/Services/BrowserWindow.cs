using Microsoft.JSInterop;

namespace Bibliotaph.Web.Services;

/// <summary>The browser's own window: a reader opened in a window of its own, and Back.</summary>
public static class BrowserWindow
{
    /// <summary>A window of its own, sized as the Windows app's pop-out reader.</summary>
    public static async Task OpenAsync(IJSRuntime js, string url)
    {
        try
        {
            await js.InvokeVoidAsync("open", url, "_blank", "popup,width=960,height=900");
        }
        catch (JSException)
        {
            // A blocked pop-up opens nothing; the page stays as it was.
        }
    }

    public static async Task BackAsync(IJSRuntime js)
    {
        try
        {
            await js.InvokeVoidAsync("history.back");
        }
        catch (JSException)
        {
            // Nothing to go back to.
        }
    }
}
