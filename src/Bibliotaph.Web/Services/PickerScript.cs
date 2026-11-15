using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Bibliotaph.Web.Services;

/// <summary>A book picker's box, kept above the on-screen keyboard on a phone or tablet so the books it finds show.</summary>
public static class PickerScript
{
    public static async Task AttachAsync(IJSRuntime js, ElementReference box)
    {
        try
        {
            await js.InvokeVoidAsync("bibliotaphPicker.attach", box);
        }
        catch (JSException)
        {
            // details.js didn't load: the picker still works, as the browser scrolls it.
        }
    }
}
