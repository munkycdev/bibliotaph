using AppKit;
using Bibliotaph.Presentation.Services;
using Foundation;
using UniformTypeIdentifiers;

namespace Bibliotaph.Mac.Platform;

/// <summary>Questions and notices as alerts, over the main window as a sheet would block the same way.</summary>
public sealed class MacUserPrompts : IUserPrompts
{
    public bool Confirm(string message, string title, PromptKind kind, bool okByDefault)
    {
        using var alert = Alert(message, title, kind);
        var ok = alert.AddButton("OK");
        var cancel = alert.AddButton("Cancel");
        // Return presses the default button; Escape always cancels.
        if (!okByDefault)
        {
            ok.KeyEquivalent = "";
            cancel.KeyEquivalent = "\r";
        }
        return alert.RunModal() == (nint)(long)NSAlertButtonReturn.First;
    }

    public bool? Choose(string message, string title, PromptKind kind)
    {
        using var alert = Alert(message, title, kind);
        alert.AddButton("Yes");
        alert.AddButton("No");
        alert.AddButton("Cancel").KeyEquivalent = "\u001b";
        return alert.RunModal() switch
        {
            (nint)(long)NSAlertButtonReturn.First => true,
            (nint)(long)NSAlertButtonReturn.Second => false,
            _ => null,
        };
    }

    public void Tell(string message, string title, PromptKind kind)
    {
        using var alert = Alert(message, title, kind);
        alert.AddButton("OK");
        alert.RunModal();
    }

    static NSAlert Alert(string message, string title, PromptKind kind) => new()
    {
        MessageText = title,
        InformativeText = message,
        AlertStyle = kind is PromptKind.Warning or PromptKind.Error ? NSAlertStyle.Warning : NSAlertStyle.Informational,
    };
}

/// <summary>Open and save panels. A filter in Windows' "Name|*.ext;*.ext2" form becomes the panel's allowed types.</summary>
public sealed class MacFilePicker : IFilePicker
{
    public string? OpenFile(string title, string filter, string? initialFolder = null)
    {
        using var panel = NSOpenPanel.OpenPanel;
        panel.Message = title;
        panel.CanChooseFiles = true;
        panel.CanChooseDirectories = false;
        panel.AllowsMultipleSelection = false;
        if (Types(filter) is { Length: > 0 } types) panel.AllowedContentTypes = types;
        if (initialFolder is not null) panel.DirectoryUrl = NSUrl.CreateFileUrl(initialFolder, true, null);
        return panel.RunModal() == (nint)(long)NSModalResponse.OK ? panel.Url?.Path : null;
    }

    public string? SaveFile(string title, string suggestedName, string filter, string defaultExtension, string? initialFolder = null)
    {
        using var panel = NSSavePanel.SavePanel;
        panel.Message = title;
        panel.NameFieldStringValue = suggestedName;
        if (Types(filter) is { Length: > 0 } types) panel.AllowedContentTypes = types;
        else if (UTType.CreateFromExtension(defaultExtension.TrimStart('.')) is { } type) panel.AllowedContentTypes = [type];
        if (initialFolder is not null) panel.DirectoryUrl = NSUrl.CreateFileUrl(initialFolder, true, null);
        return panel.RunModal() == (nint)(long)NSModalResponse.OK ? panel.Url?.Path : null;
    }

    public string? PickFolder(string title) => Folders(title, multiple: false) is [var folder, ..] ? folder : null;

    public IReadOnlyList<string> PickFolders(string title) => Folders(title, multiple: true);

    static IReadOnlyList<string> Folders(string title, bool multiple)
    {
        using var panel = NSOpenPanel.OpenPanel;
        panel.Message = title;
        panel.Prompt = "Choose";
        panel.CanChooseFiles = false;
        panel.CanChooseDirectories = true;
        panel.CanCreateDirectories = false;
        panel.AllowsMultipleSelection = multiple;
        if (panel.RunModal() != (nint)(long)NSModalResponse.OK) return [];
        return [.. panel.Urls.Select(u => u.Path).OfType<string>()];
    }

    static UTType[] Types(string filter) =>
    [
        .. filter.Split('|').Where((_, i) => i % 2 == 1)
            .SelectMany(patterns => patterns.Split(';'))
            .Select(pattern => pattern.Trim().TrimStart('*').TrimStart('.'))
            .Where(extension => extension.Length > 0 && extension != "*")
            .Select(UTType.CreateFromExtension)
            .OfType<UTType>(),
    ];
}

/// <summary>Finder and the default apps, through NSWorkspace.</summary>
public sealed class MacDesktopShell : IDesktopShell
{
    public void ShowInFolder(string path) =>
        NSWorkspace.SharedWorkspace.ActivateFileViewer([NSUrl.CreateFileUrl(path, Directory.Exists(path), null)]);

    public void OpenFolder(string folder) => NSWorkspace.SharedWorkspace.OpenUrl(NSUrl.CreateFileUrl(folder, true, null));

    public Exception? Open(string target)
    {
        var url = Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile
            ? new NSUrl(uri.AbsoluteUri)
            : NSUrl.CreateFileUrl(target, Directory.Exists(target), null);
        return NSWorkspace.SharedWorkspace.OpenUrl(url) ? null : new InvalidOperationException($"Nothing on this Mac could open {target}.");
    }
}

public sealed class MacClipboard : IClipboard
{
    public void SetText(string text)
    {
        var board = NSPasteboard.GeneralPasteboard;
        board.ClearContents();
        board.SetStringForType(text, NSPasteboardType.String.GetConstant()!);
    }
}
