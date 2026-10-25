using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>
/// The sidebar's Pinned group, kept for the sidebar, Home and every Pin button, as the Windows app's PinDirectory: the
/// pins in order with their targets' names. It reloads after a pin changes, and when a page says a name may have.
/// </summary>
public sealed class Pins(ApiClient api)
{
    public IReadOnlyList<PinItem> Items { get; private set; } = [];

    /// <summary>What the sidebar says while there are none.</summary>
    public string Hint { get; private set; } = "";

    public event Action? Changed;

    public async Task LoadAsync() => await ShowAsync(api.PinsAsync());

    public bool IsPinned(string kind, long? id = null) => Items.Any(p => p.Kind == kind && p.Id == id);

    public string PinLabel(string kind, long? id = null) => IsPinned(kind, id) ? "Unpin" : "Pin";

    /// <summary>Pins it at the bottom, or unpins it.</summary>
    public Task ToggleAsync(string kind, long? id = null) =>
        ShowAsync(IsPinned(kind, id) ? api.UnpinAsync(new PinRef(kind, id)) : api.PinAsync(new PinRef(kind, id)));

    public Task UnpinAsync(PinItem pin) => ShowAsync(api.UnpinAsync(new PinRef(pin.Kind, pin.Id)));

    /// <summary>Moves a pin to that place, counted once it has been taken out.</summary>
    public Task MoveAsync(PinItem pin, int index) => ShowAsync(api.MovePinAsync(new PinMove(pin.Kind, pin.Id, index)));

    async Task ShowAsync(Task<PinList> change)
    {
        try
        {
            var list = await change;
            Items = list.Items;
            Hint = list.Hint;
        }
        catch (Exception e) when (e is ApiProblemException or HttpRequestException)
        {
            // The pins stay as they were; the next page that loads them tries again.
        }
        Changed?.Invoke();
    }
}
