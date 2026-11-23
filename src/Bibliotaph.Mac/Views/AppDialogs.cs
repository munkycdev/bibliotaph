using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Foundation;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Settings › AI's Test with a Book, as a sheet on the window it was asked from: it starts as it opens, shows each
/// step and what the model found, and stops if it's closed before it ends, as on Windows.
/// </summary>
static class AiTestSheet
{
    public static void Show(AiTestViewModel test)
    {
        // The window it was asked from; with the app in the background, its first window showing.
        var host = NSApplication.SharedApplication.KeyWindow ?? NSApplication.SharedApplication.MainWindow
            ?? Enumerable.FirstOrDefault<NSWindow>(NSApplication.SharedApplication.DangerousWindows, w => w.IsVisible && w.AttachedSheet is null);
        var sheet = new Sheet(540);
        var bindings = new Bindings();
        sheet.Text(test.Heading, size: 17).Font = NSFont.SystemFontOfSize(17, NSFontWeight.Semibold)!;

        var step = NSTextField.CreateWrappingLabel("");
        step.Font = NSFont.SystemFontOfSize(13)!;
        var elapsed = NSTextField.CreateLabel("");
        elapsed.Font = NSFont.SystemFontOfSize(11)!;
        elapsed.TextColor = NSColor.SecondaryLabel;
        var stepRow = NSStackView.FromViews([step, elapsed]);
        stepRow.Distribution = NSStackViewDistribution.Fill;
        step.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Bar, Indeterminate = true };
        var detail = NSTextField.CreateWrappingLabel("");
        detail.Font = NSFont.SystemFontOfSize(11)!;
        detail.TextColor = NSColor.SecondaryLabel;
        var running = NSStackView.FromViews([stepRow, spinner, detail]);
        running.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        running.Alignment = NSLayoutAttribute.Width;
        running.Spacing = 8;
        sheet.AddWide(running);

        var summary = NSTextField.CreateWrappingLabel("");
        var problem = NSTextField.CreateWrappingLabel("");
        problem.TextColor = NSColor.SystemOrange;
        var kept = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 8 };
        var dropped = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 4 };
        var results = NSStackView.FromViews([summary, problem, kept, dropped]);
        results.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        results.Alignment = NSLayoutAttribute.Leading;
        results.Spacing = 10;
        // A long answer scrolls rather than making the sheet taller than the screen.
        var scroll = new NSScrollView { HasVerticalScroller = true, DrawsBackground = false, DocumentView = new Flipped() };
        results.TranslatesAutoresizingMaskIntoConstraints = false;
        scroll.DocumentView.AddSubview(results);
        scroll.DocumentView.TranslatesAutoresizingMaskIntoConstraints = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            results.TopAnchor.ConstraintEqualTo(scroll.DocumentView.TopAnchor),
            results.LeadingAnchor.ConstraintEqualTo(scroll.DocumentView.LeadingAnchor),
            results.TrailingAnchor.ConstraintEqualTo(scroll.DocumentView.TrailingAnchor),
            results.BottomAnchor.ConstraintEqualTo(scroll.DocumentView.BottomAnchor),
            scroll.DocumentView.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor),
        ]);
        var height = scroll.HeightAnchor.ConstraintEqualTo(10);
        height.Active = true;
        sheet.AddWide(scroll);

        void Fit()
        {
            results.LayoutSubtreeIfNeeded();
            height.Constant = (nfloat)Math.Min(440, results.FittingSize.Height);
            sheet.Fit();
        }
        void Lines()
        {
            foreach (var view in kept.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var line in test.Kept)
            {
                var text = NSTextField.CreateWrappingLabel(line.Text);
                text.Font = NSFont.SystemFontOfSize(12)!;
                var why = NSTextField.CreateWrappingLabel(line.Detail);
                why.Font = NSFont.SystemFontOfSize(11)!;
                why.TextColor = NSColor.SecondaryLabel;
                var pair = NSStackView.FromViews([text, why]);
                pair.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
                pair.Alignment = NSLayoutAttribute.Leading;
                pair.Spacing = 2;
                kept.AddArrangedSubview(pair);
            }
            foreach (var view in dropped.ArrangedSubviews) view.RemoveFromSuperview();
            if (test.Dropped.Count > 0) dropped.AddArrangedSubview(PageParts.Secondary("Thrown away"));
            foreach (var line in test.Dropped)
            {
                var text = PageParts.Secondary($"{line.Text} · {line.Detail}");
                text.Font = NSFont.SystemFontOfSize(11)!;
                dropped.AddArrangedSubview(text);
            }
            Fit();
        }
        bindings.Collection(test.Kept, _ => Lines());
        bindings.Collection(test.Dropped, _ => Lines());

        var again = NSButton.CreateButton("Test Again", () => { });
        bindings.Command(again, test.TestAgainCommand);
        var close = NSButton.CreateButton("Close", () => { });
        close.KeyEquivalent = "\u001b";
        sheet.Buttons(again, close);
        close.KeyEquivalent = "\r";

        bindings.Properties(test, [nameof(AiTestViewModel.IsRunning), nameof(AiTestViewModel.StepText), nameof(AiTestViewModel.StepDetail),
            nameof(AiTestViewModel.ElapsedText), nameof(AiTestViewModel.Summary), nameof(AiTestViewModel.Problem)], () =>
        {
            running.Hidden = !test.IsRunning;
            again.Hidden = test.IsRunning;
            if (test.IsRunning) spinner.StartAnimation(null);
            else spinner.StopAnimation(null);
            step.StringValue = test.StepText;
            elapsed.StringValue = test.ElapsedText;
            detail.StringValue = test.StepDetail ?? "";
            detail.Hidden = test.StepDetail is null;
            scroll.Hidden = test.IsRunning;
            summary.StringValue = test.Summary ?? "";
            summary.Hidden = test.Summary is null;
            problem.StringValue = test.Problem ?? "";
            problem.Hidden = test.Problem is null;
            Fit();
        });
        close.Activated += (_, _) =>
        {
            test.Stop();
            bindings.Dispose();
            sheet.Close();
            sheet.Dispose();
        };

        if (host is not null) sheet.Show(host);
        else
        {
            sheet.Window.Center();
            sheet.Window.MakeKeyAndOrderFront(null);
        }
        test.TestAgainCommand.ExecuteAsync(null).FireAndForget();
    }

    sealed class Flipped : NSView
    {
        public override bool IsFlipped => true;
    }
}

/// <summary>
/// The keyboard shortcuts, by where they work, in a window of their own (⌘/), as Windows lists its own: the menus
/// show most of them too.
/// </summary>
static class ShortcutsWindow
{
    static NSWindow? _window;

    /// <summary>The window while it's open, for a test copy's screenshot.</summary>
    public static NSWindow? Current => _window;

    static readonly (string Where, (string Keys, string Action)[] Shortcuts)[] Groups =
    [
        ("Everywhere",
        [
            ("⌘F", "Search your library, or find in the book being read"),
            ("⌘K", "Search your library from anywhere"),
            ("⌥⌘F", "Ask your library"),
            ("⌘[, mouse back button, swipe right", "Go back"),
            ("⌘O", "Add a folder of books"),
            ("⌘,", "Settings"),
            ("⌃⌘S", "Show or hide the sidebar"),
            ("⌥⌘I", "Show or hide the book's details"),
            ("⌘W", "Close the window"),
            ("⌘/", "These shortcuts"),
        ]),
        ("Search field",
        [
            ("Return", "Search now"),
            ("⌥Esc, or ↓", "Show the search guide"),
            ("↓, ↑", "Move through the search guide"),
            ("Return or Tab", "Use the highlighted suggestion"),
            ("Esc", "Close the search guide, then clear the search"),
        ]),
        ("Library",
        [
            ("⌘1", "Show books as covers"),
            ("⌘2", "Show books as a list"),
            ("Return, ⌘↓", "Open the book in focus; in Organize, tick or untick it"),
            ("⇧Return", "Open the book in focus in a window of its own"),
            ("Click, Shift-click", "In Organize, tick a book, or every book up to it"),
            ("⌘A", "In Organize, tick every book shown"),
            ("Drag a card", "In Organize, onto its book, another copy of it, or a book in the Books panel"),
            ("⌘Z", "In Organize, take back the last change"),
            ("Esc", "Leave Organize"),
        ]),
        ("Reading a book",
        [
            ("⌘F", "Find in this book"),
            ("⌘G, ⇧⌘G", "The next or previous match"),
            ("⌘Home, ⌘End", "The first or last page"),
            ("⌘+, ⌘−", "Zoom in or out"),
            ("⌘0, ⌥⌘0", "Fit the width, or the whole page"),
            ("Drag", "Select text on a page"),
            ("Double-click, triple-click", "Select a word, or a line"),
            ("⌘C", "Copy the selected text"),
            ("⌘A", "Select the text on the page"),
            ("Esc", "Let the selection go"),
            ("Arrows, Page Up/Down", "Scroll the pages"),
        ]),
        ("A binder",
        [
            ("⌥⌘→, ⌥⌘←", "The next or previous item in the binder"),
            ("Return, ⇧Return", "Editing it, open the item in focus, or open it in a window of its own"),
            ("⌥↑, ⌥↓", "Editing it, move the item in focus up or down"),
            ("Delete", "Editing it, take the item in focus out"),
            ("Drag the grip", "Editing it, move an item to another place or section"),
        ]),
    ];

    public static void Show()
    {
        if (_window is { } open)
        {
            open.MakeKeyAndOrderFront(null);
            return;
        }
        var content = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 6,
            EdgeInsets = new NSEdgeInsets(20, 24, 24, 24),
        };
        var intro = PageParts.Secondary("Tab and Shift-Tab move between controls, the arrow keys move within a list, and Space presses what has focus. These work too:");
        content.AddArrangedSubview(intro);
        intro.WidthAnchor.ConstraintEqualTo(464).Active = true;
        foreach (var (where, shortcuts) in Groups)
        {
            var heading = NSTextField.CreateLabel(where);
            heading.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
            content.AddArrangedSubview(heading);
            content.SetCustomSpacing(6, heading);
            if (content.ArrangedSubviews.Length > 1) content.SetCustomSpacing(16, content.ArrangedSubviews[^2]);
            foreach (var (keys, action) in shortcuts)
            {
                var key = NSTextField.CreateLabel(keys);
                key.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Medium)!;
                key.Alignment = NSTextAlignment.Right;
                key.WidthAnchor.ConstraintEqualTo(180).Active = true;
                var what = NSTextField.CreateWrappingLabel(action);
                what.Font = NSFont.SystemFontOfSize(12)!;
                what.WidthAnchor.ConstraintEqualTo(270).Active = true;
                var row = NSStackView.FromViews([key, what]);
                row.Alignment = NSLayoutAttribute.FirstBaseline;
                row.Spacing = 14;
                content.AddArrangedSubview(row);
            }
        }
        var window = new NSWindow(new CoreGraphics.CGRect(0, 0, 480, 600), NSWindowStyle.Titled | NSWindowStyle.Closable, NSBackingStore.Buffered, false)
        {
            Title = "Keyboard Shortcuts",
            ContentView = content,
        };
        window.ReleaseWhenClosed(false);
        window.SetContentSize(content.FittingSize);
        window.Center();
        window.WillClose += (_, _) => _window = null;
        _window = window;
        window.MakeKeyAndOrderFront(null);
    }
}

/// <summary>
/// About Bibliotaph: the standard panel, with what Windows' About says under it: that it's free software under the GPL,
/// the licence and third-party notices that come with it, its source and where to report a problem, and the credit
/// BoardGameGeek's terms ask for where RPGGeek's data shows.
/// </summary>
static class AboutPanel
{
    public static void Show()
    {
        var text = new NSMutableAttributedString();
        var body = new NSStringAttributes { Font = NSFont.SystemFontOfSize(11)!, ForegroundColor = NSColor.SecondaryLabel };
        void Add(string words) => text.Append(new NSAttributedString(words, body));
        void Link(string words, string url)
        {
            var link = new NSMutableAttributedString(words, new NSStringAttributes { Font = NSFont.SystemFontOfSize(11)! }.Dictionary);
            link.AddAttribute(NSStringAttributeKey.Link, new NSUrl(url), new NSRange(0, link.Length));
            text.Append(link);
        }
        string File(string name) => NSBundle.MainBundle.PathForResource(System.IO.Path.GetFileNameWithoutExtension(name), System.IO.Path.GetExtension(name).TrimStart('.'))
            is { } path ? NSUrl.CreateFileUrl(path, false, null).AbsoluteString! : AboutInfo.SourceUrl;

        Add("Bibliotaph is free software: you can share and change it under the GNU General Public License, version 3 or any later version. It comes with no warranty.\n\n");
        Link("Licence", File("LICENSE.txt"));
        Add("  ·  ");
        Link("Third-party notices", File("THIRD-PARTY-NOTICES.md"));
        Add("  ·  ");
        Link("Source code", AboutInfo.SourceUrl);
        Add("  ·  ");
        Link("Report a problem", AboutInfo.IssuesUrl);
        Add("\n\nBook data looked up on RPGGeek comes from BoardGameGeek: ");
        Link("powered by BGG", "https://boardgamegeek.com");
        Add(". The built-in AI runs llama.cpp (MIT), downloaded from its own releases.");
        var centred = new NSMutableParagraphStyle { Alignment = NSTextAlignment.Center };
        text.AddAttribute(NSStringAttributeKey.ParagraphStyle, centred, new NSRange(0, text.Length));

        var options = new NSMutableDictionary
        {
            [new NSString("Credits")] = text,
            [new NSString("Copyright")] = new NSString(AboutInfo.Copyright),
        };
        NSApplication.SharedApplication.OrderFrontStandardAboutPanelWithOptions(options);
    }
}
