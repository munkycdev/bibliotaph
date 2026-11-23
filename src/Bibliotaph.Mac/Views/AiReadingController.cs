using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Processing;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// AI reading, from Settings › AI: the book the AI is reading now, its answer as it writes it, and what the evidence
/// check kept and threw away from the last books it read, as on Windows.
/// </summary>
public sealed class AiReadingController(AiReadingViewModel reading) : NSViewController
{
    readonly Bindings _bindings = new();

    public override void LoadView()
    {
        var (root, content) = PageParts.ScrollingPage();
        View = root;
        var subtitle = PageParts.Secondary(AiReadingText.Subtitle);

        var title = NSTextField.CreateLabel("");
        title.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var elapsed = Small("");
        var head = NSStackView.FromViews([title, elapsed]);
        head.Distribution = NSStackViewDistribution.Fill;
        title.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var step = PageParts.Secondary("");
        var progress = new NSProgressIndicator { Indeterminate = false, MinValue = 0, MaxValue = 100, Style = NSProgressIndicatorStyle.Bar };
        var answer = Column();
        answer.Spacing = 3;
        var now = Column(head, step, progress, answer);
        var nowBox = new Tinted(now, Palette.Panel, 10, new NSEdgeInsets(12, 14, 12, 14));
        var idle = PageParts.Secondary("");
        var settings = NSButton.CreateButton("AI Settings…", () => reading.OpenAiSettingsCommand.Execute(null));

        var nothing = PageParts.Secondary(AiReadingText.NothingRead);
        var recent = Column();
        recent.Spacing = 12;

        foreach (var view in new NSView[] { subtitle, PageParts.Section("Now"), nowBox, idle, settings,
            PageParts.Section("Last Read"), nothing, recent })
            content.AddArrangedSubview(view);
        content.SetCustomSpacing(22, subtitle);
        content.SetCustomSpacing(22, settings);
        foreach (var view in new NSView[] { subtitle, nowBox, idle, recent, nothing })
            view.WidthAnchor.ConstraintLessThanOrEqualTo(content.WidthAnchor, 1, -48).Active = true;
        foreach (var view in new NSView[] { nowBox, recent })
        {
            var wide = view.WidthAnchor.ConstraintEqualTo(860);
            // Below the split view's holding priorities, so this asks for room without taking it from the sidebar.
            wide.Priority = 200;
            wide.Active = true;
        }
        head.WidthAnchor.ConstraintEqualTo(now.WidthAnchor).Active = true;
        progress.WidthAnchor.ConstraintEqualTo(now.WidthAnchor).Active = true;

        _bindings.Properties(reading, [nameof(AiReadingViewModel.HasCurrent), nameof(AiReadingViewModel.CurrentTitle), nameof(AiReadingViewModel.StepText),
            nameof(AiReadingViewModel.ElapsedText), nameof(AiReadingViewModel.ShowReadProgress), nameof(AiReadingViewModel.ReadPercent),
            nameof(AiReadingViewModel.IdleText), nameof(AiReadingViewModel.HasRecent)], () =>
        {
            nowBox.Hidden = !reading.HasCurrent;
            idle.Hidden = reading.HasCurrent;
            title.StringValue = reading.CurrentTitle;
            elapsed.StringValue = reading.ElapsedText;
            step.StringValue = reading.StepText;
            progress.Hidden = !reading.ShowReadProgress;
            progress.DoubleValue = reading.ReadPercent;
            idle.StringValue = reading.IdleText;
            nothing.Hidden = reading.HasRecent;
        });
        void Answer()
        {
            foreach (var view in answer.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var line in reading.Fields) answer.AddArrangedSubview(Line(line.Text, line.Detail));
            answer.Hidden = reading.Fields.Count == 0;
        }
        void Recent()
        {
            foreach (var view in recent.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var done in reading.Recent) recent.AddArrangedSubview(Done(done));
        }
        Answer();
        Recent();
        _bindings.Collection(reading.Fields, _ => Answer());
        _bindings.Collection(reading.Recent, _ => Recent());
    }

    /// <summary>A book read: its title, what came of it, why not if it failed, then what was kept and thrown away.</summary>
    static Tinted Done(AiReadingDone done)
    {
        var title = NSTextField.CreateLabel(done.Title);
        title.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold)!;
        title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var column = Column(title, Small(done.Summary));
        column.Spacing = 3;
        if (done.Problem is { } problem) column.AddArrangedSubview(PageParts.Secondary(problem));
        foreach (var kept in done.Kept)
        {
            var text = NSTextField.CreateWrappingLabel(kept.Text);
            text.Font = NSFont.SystemFontOfSize(12)!;
            var pair = Column(text, PageParts.Secondary(kept.Detail));
            pair.Spacing = 1;
            column.AddArrangedSubview(pair);
        }
        foreach (var dropped in done.Dropped)
        {
            var line = PageParts.Secondary($"Thrown away: {dropped.Text} · {dropped.Detail}");
            line.Font = NSFont.SystemFontOfSize(11)!;
            column.AddArrangedSubview(line);
        }
        return new Tinted(column, Palette.Panel, 10, new NSEdgeInsets(10, 14, 10, 14));
    }

    static NSTextField Line(string name, string detail)
    {
        var line = NSTextField.CreateWrappingLabel("");
        var text = new Foundation.NSMutableAttributedString(name + ": ",
            new NSStringAttributes { Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold)!, ForegroundColor = NSColor.Label }.Dictionary);
        text.Append(new Foundation.NSAttributedString(detail, new NSStringAttributes { Font = NSFont.SystemFontOfSize(12)!, ForegroundColor = NSColor.SecondaryLabel }.Dictionary));
        line.AttributedStringValue = text;
        return line;
    }

    static NSTextField Small(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(11)!;
        label.TextColor = NSColor.SecondaryLabel;
        return label;
    }

    static NSStackView Column(params NSView[] views)
    {
        var column = NSStackView.FromViews(views);
        column.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        column.Alignment = NSLayoutAttribute.Leading;
        column.Spacing = 6;
        return column;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bindings.Dispose();
        base.Dispose(disposing);
    }
}
