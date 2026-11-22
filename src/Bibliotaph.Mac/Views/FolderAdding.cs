using AppKit;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Adding a folder, as Settings › Library does it on Windows: pick folders, see what each holds, then add it or not.
/// Adding starts indexing it at once. The folder and its files are only ever read.
/// </summary>
public sealed class FolderAdding(IServiceProvider services)
{
    /// <summary>A folder was added; the sidebar and the library list folders again.</summary>
    public event EventHandler? Added;

    public async Task PickAsync()
    {
        var section = services.GetRequiredService<LibrarySectionViewModel>();
        try
        {
            await section.LoadAsync();
            await section.AddFolderCommand.ExecuteAsync(null);
            foreach (var pending in section.Pending.ToList())
            {
                if (!Confirm(pending))
                {
                    section.CancelAddCommand.Execute(pending);
                    continue;
                }
                await section.ConfirmAddCommand.ExecuteAsync(pending);
                Added?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            section.Unload();
        }
    }

    static bool Confirm(PendingFolder pending)
    {
        using var alert = new NSAlert
        {
            MessageText = pending.CanAdd ? $"Add “{Path.GetFileName(pending.Shown.TrimEnd('/'))}” to your library?" : "This folder can't be added",
            InformativeText = $"{pending.Shown}\n\n{pending.Detail}",
            AlertStyle = NSAlertStyle.Informational,
        };
        if (!pending.CanAdd)
        {
            alert.AddButton("OK");
            alert.RunModal();
            return false;
        }
        alert.AddButton("Add folder");
        alert.AddButton("Cancel");
        return alert.RunModal() == (nint)(long)NSAlertButtonReturn.First;
    }
}
