using Bibliotaph.Api.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Bibliotaph.Web.Services;

/// <summary>
/// The web's Open in another app: a browser can't hand a file to another app, so the file opens from the user's
/// storage in a new tab, where the browser shows it or saves it for an app that can read it. The link is the reader's
/// short-lived one, so the server never handles the book's bytes for storage that gives such links.
/// </summary>
public static class FileDownload
{
    public static async Task OpenAsync(ApiClient api, IJSRuntime js, long documentId, EventCallback<ActionResult> done)
    {
        var link = await api.ReaderLinkAsync(documentId);
        if (link.Url is not { } url)
        {
            await done.InvokeAsync(new ActionResult(link.Problem?.Title ?? "The file couldn't be found."));
            return;
        }
        try
        {
            await js.InvokeVoidAsync("open", url, "_blank", "noopener");
        }
        catch (JSException)
        {
            await done.InvokeAsync(new ActionResult("The browser didn't open a new tab. Allow pop-ups for Bibliotaph, then try again."));
        }
    }
}
