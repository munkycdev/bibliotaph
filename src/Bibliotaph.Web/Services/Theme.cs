using Microsoft.JSInterop;

namespace Bibliotaph.Web.Services;

/// <summary>
/// Light, dark or the device's choice, as the Windows app's Theme button cycles them. wwwroot/js/theme.js applies it
/// before the client starts, so the page never flashes the wrong colours.
/// </summary>
public sealed class Theme(IJSRuntime js)
{
    public static readonly string[] Modes = ["system", "light", "dark"];

    public async Task<string> CurrentAsync()
    {
        try
        {
            return await js.InvokeAsync<string>("bibliotaphTheme.get");
        }
        catch (JSException)
        {
            return "system";
        }
    }

    /// <summary>Raised with the new mode, so the top bar's button and Settings › Appearance agree.</summary>
    public event Action<string>? Changed;

    /// <summary>Moves to the next mode and returns it.</summary>
    public async Task<string> CycleAsync() => await SetAsync(Modes[(Array.IndexOf(Modes, await CurrentAsync()) + 1) % Modes.Length]);

    public async Task<string> SetAsync(string mode)
    {
        try
        {
            await js.InvokeVoidAsync("bibliotaphTheme.set", mode);
        }
        catch (JSException)
        {
            // theme.js didn't load; the page keeps the device's colours.
        }
        Changed?.Invoke(mode);
        return mode;
    }

    public static string Label(string mode) => mode switch
    {
        "light" => "Theme: light",
        "dark" => "Theme: dark",
        _ => "Theme: same as this device",
    };
}
