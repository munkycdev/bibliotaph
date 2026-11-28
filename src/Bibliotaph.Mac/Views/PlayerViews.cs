using AppKit;
using Bibliotaph.Core;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Foundation;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// What's playing, in the middle of the toolbar as Music has it: the track's cover and title with Play and Next beside
/// them, hidden while nothing plays. A click on the title opens the player itself, in a popover under it.
/// </summary>
sealed class NowPlayingItem : IDisposable
{
    public const string Identifier = "nowPlaying";

    readonly PlayerViewModel _player;
    readonly MacCovers _covers;
    readonly Bindings _bindings = new();
    readonly PlayerPopover _popover;

    public NowPlayingItem(PlayerViewModel player, MacCovers covers)
    {
        _player = player;
        _covers = covers;
        _popover = new PlayerPopover(player, covers);
        Item = new NSToolbarItem(Identifier) { Label = "Now Playing", ToolTip = "What's playing" };

        var cover = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown, WantsLayer = true };
        cover.Layer!.CornerRadius = 3;
        cover.Layer.MasksToBounds = true;
        cover.WidthAnchor.ConstraintEqualTo(24).Active = true;
        cover.HeightAnchor.ConstraintEqualTo(24).Active = true;
        var title = NSButton.CreateButton("", () => _popover.ShowUnder(Item.View!));
        title.Bordered = false;
        title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        title.WidthAnchor.ConstraintLessThanOrEqualTo(240).Active = true;
        title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var ambience = new NSImageView { Image = NSImage.GetSystemSymbol("waveform", "Ambience")!, ToolTip = "Ambience is looping" };
        ambience.ContentTintColor = NSColor.ControlAccent;
        var playPause = Symbol("play.fill", "Play");
        var next = Symbol("forward.fill", "Next");
        _bindings.Command(playPause, player.PlayPauseCommand);
        _bindings.Command(next, player.NextCommand);
        var row = NSStackView.FromViews([cover, title, ambience, playPause, next]);
        row.Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        row.Spacing = 6;
        Item.View = row;

        _bindings.Properties(player, [nameof(PlayerViewModel.Current), nameof(PlayerViewModel.IsShowing), nameof(PlayerViewModel.IsPlaying),
            nameof(PlayerViewModel.HasAmbience), nameof(PlayerViewModel.CanGoNext), nameof(PlayerViewModel.Ambience)], () =>
        {
            Item.Hidden = !player.IsShowing;
            var showing = player.Current?.Title ?? (player.Ambience is { } loop ? $"Ambience: {loop.Title}" : "");
            title.Title = showing;
            title.ToolTip = player.Current is { } track ? $"{track.Title}\n{track.Byline}" : showing;
            playPause.Image = NSImage.GetSystemSymbol(player.IsPlaying ? "pause.fill" : "play.fill", player.PlayPauseLabel)!;
            playPause.ToolTip = player.PlayPauseLabel;
            playPause.Hidden = next.Hidden = !player.HasMusic;
            next.Enabled = player.CanGoNext;
            ambience.Hidden = !player.HasAmbience || !player.HasMusic;
            ShowCover(cover, player.Current?.Cover);
        });
    }

    public NSToolbarItem Item { get; }

    public void Dispose()
    {
        _bindings.Dispose();
        _popover.Dispose();
    }

    void ShowCover(NSImageView view, string? name)
    {
        view.Image = name is null ? NSImage.GetSystemSymbol("music.note", "Track")! : _covers.TryGet(name) ?? NSImage.GetSystemSymbol("music.note", "Track")!;
        if (name is not null && _covers.TryGet(name) is null)
            _ = LoadCoverAsync(view, name);
    }

    async Task LoadCoverAsync(NSImageView view, string name)
    {
        if (await _covers.LoadAsync(name) is { } image && _player.Current?.Cover == name) view.Image = image;
    }

    internal static NSButton Symbol(string symbol, string tip)
    {
        var button = NSButton.CreateButton(NSImage.GetSystemSymbol(symbol, tip)!, () => { });
        button.Bordered = false;
        button.ToolTip = tip;
        return button;
    }
}

/// <summary>
/// The player, in a popover from the toolbar: the track and its place, the controls, volume, Up Next, and the ambience
/// layer looping under the music with its own volume.
/// </summary>
sealed class PlayerPopover : NSViewController
{
    readonly PlayerViewModel _player;
    readonly MacCovers _covers;
    readonly Bindings _bindings = new();
    readonly NSPopover _popover;
    NSStackView _queue = null!;
    Bindings _rows = new();

    public PlayerPopover(PlayerViewModel player, MacCovers covers)
    {
        _player = player;
        _covers = covers;
        _popover = new NSPopover { ContentViewController = this, Behavior = NSPopoverBehavior.Transient };
    }

    public void ShowUnder(NSView anchor)
    {
        _ = View;
        if (_popover.Shown) _popover.Close();
        else _popover.Show(anchor.Bounds, anchor, NSRectEdge.MaxYEdge);
    }

    public override void LoadView()
    {
        var player = _player;
        var content = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 10,
            EdgeInsets = new NSEdgeInsets(14, 14, 14, 14),
        };
        content.WidthAnchor.ConstraintEqualTo(360).Active = true;
        View = content;

        // The track: its cover, title and who made it, or why the last one didn't play.
        var cover = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown, WantsLayer = true };
        cover.Layer!.CornerRadius = 4;
        cover.Layer.MasksToBounds = true;
        cover.WidthAnchor.ConstraintEqualTo(56).Active = true;
        cover.HeightAnchor.ConstraintEqualTo(56).Active = true;
        var title = NSTextField.CreateLabel("");
        title.Font = NSFont.BoldSystemFontOfSize(13)!;
        title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var byline = NSTextField.CreateLabel("");
        byline.TextColor = NSColor.SecondaryLabel;
        byline.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var texts = NSStackView.FromViews([title, byline]);
        texts.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        texts.Alignment = NSLayoutAttribute.Leading;
        texts.Spacing = 2;
        var now = NSStackView.FromViews([cover, texts]);
        now.Spacing = 10;

        // The controls, over the track's place in it.
        var shuffle = NowPlayingItem.Symbol("shuffle", "Shuffle");
        var previous = NowPlayingItem.Symbol("backward.fill", "Previous");
        var playPause = NowPlayingItem.Symbol("play.fill", "Play");
        var next = NowPlayingItem.Symbol("forward.fill", "Next");
        var repeat = NowPlayingItem.Symbol("repeat", "Repeat");
        _bindings.Command(shuffle, player.ToggleShuffleCommand);
        _bindings.Command(previous, player.PreviousCommand);
        _bindings.Command(playPause, player.PlayPauseCommand);
        _bindings.Command(next, player.NextCommand);
        _bindings.Command(repeat, player.CycleRepeatCommand);
        var buttons = NSStackView.FromViews([shuffle, previous, playPause, next, repeat]);
        buttons.Spacing = 18;
        var position = NSSlider.FromValue(0, 0, 1, () => { });
        position.Activated += (_, _) => player.Position = position.DoubleValue;
        position.WidthAnchor.ConstraintEqualTo(240).Active = true;
        var at = NSTextField.CreateLabel("0:00");
        var length = NSTextField.CreateLabel("");
        foreach (var label in new[] { at, length })
        {
            label.Font = NSFont.MonospacedDigitSystemFontOfSize(11, NSFontWeight.Regular)!;
            label.TextColor = NSColor.SecondaryLabel;
        }
        var time = NSStackView.FromViews([at, position, length]);
        time.Spacing = 8;

        var speaker = new NSImageView { Image = NSImage.GetSystemSymbol("speaker.wave.2", "Volume")! };
        var volume = NSSlider.FromValue(player.Volume, 0, 100, () => player.Volume = 0);
        volume.Activated += (_, _) => player.Volume = volume.DoubleValue;
        volume.WidthAnchor.ConstraintEqualTo(160).Active = true;
        var loop = NSButton.CreateButton("Loop as Ambience", () => { });
        loop.Image = NSImage.GetSystemSymbol("waveform", "Loop as Ambience")!;
        loop.ImagePosition = NSCellImagePosition.ImageLeading;
        loop.ToolTip = "Loop this track as ambience, under whatever plays next";
        _bindings.Command(loop, player.LoopCurrentAsAmbienceCommand);
        var sound = NSStackView.FromViews([speaker, volume, loop]);
        sound.Spacing = 8;

        var upNext = Heading("Up Next");
        _queue = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 2 };
        var scroll = new NSScrollView { HasVerticalScroller = true, DrawsBackground = false, DocumentView = new FlippedView(_queue) };
        scroll.HeightAnchor.ConstraintLessThanOrEqualTo(200).Active = true;
        scroll.HeightAnchor.ConstraintGreaterThanOrEqualTo(40).Active = true;
        scroll.WidthAnchor.ConstraintEqualTo(332).Active = true;
        var music = NSStackView.FromViews([now, buttons, time, sound, upNext, scroll]);
        music.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        music.Alignment = NSLayoutAttribute.Leading;
        music.Spacing = 10;

        // The ambience layer, under the music: what loops, its own pause and volume, and Stop.
        var ambienceTitle = NSTextField.CreateLabel("");
        ambienceTitle.LineBreakMode = NSLineBreakMode.TruncatingTail;
        ambienceTitle.WidthAnchor.ConstraintLessThanOrEqualTo(200).Active = true;
        var ambiencePause = NowPlayingItem.Symbol("pause.fill", "Pause Ambience");
        _bindings.Command(ambiencePause, player.ToggleAmbienceCommand);
        var ambienceVolume = NSSlider.FromValue(player.AmbienceVolume, 0, 100, () => { });
        ambienceVolume.Activated += (_, _) => player.AmbienceVolume = ambienceVolume.DoubleValue;
        ambienceVolume.WidthAnchor.ConstraintEqualTo(120).Active = true;
        var ambienceStop = NowPlayingItem.Symbol("xmark", "Stop Ambience");
        _bindings.Command(ambienceStop, player.StopAmbienceCommand);
        var wave = new NSImageView { Image = NSImage.GetSystemSymbol("waveform", "Ambience")!, ContentTintColor = NSColor.ControlAccent };
        var ambienceRow = NSStackView.FromViews([wave, ambienceTitle, ambiencePause, ambienceVolume, ambienceStop]);
        ambienceRow.Spacing = 8;
        var ambience = NSStackView.FromViews([Heading("Ambience"), ambienceRow]);
        ambience.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        ambience.Alignment = NSLayoutAttribute.Leading;

        var problem = NSTextField.CreateWrappingLabel("");
        problem.TextColor = NSColor.SystemOrange;
        problem.PreferredMaxLayoutWidth = 332;
        var close = NSButton.CreateButton("Stop and Close Player", () => { });
        _bindings.Command(close, player.CloseCommand);

        content.AddArrangedSubview(music);
        content.AddArrangedSubview(ambience);
        content.AddArrangedSubview(problem);
        content.AddArrangedSubview(close);

        _bindings.Properties(player, [nameof(PlayerViewModel.Current), nameof(PlayerViewModel.IsPlaying), nameof(PlayerViewModel.Shuffle),
            nameof(PlayerViewModel.Repeat), nameof(PlayerViewModel.Problem), nameof(PlayerViewModel.Length), nameof(PlayerViewModel.HasMusic)], () =>
        {
            music.Hidden = !player.HasMusic;
            title.StringValue = player.Current?.Title ?? "";
            byline.StringValue = player.Current?.Byline ?? "";
            ShowCover(cover, player.Current?.Cover);
            playPause.Image = NSImage.GetSystemSymbol(player.IsPlaying ? "pause.fill" : "play.fill", player.PlayPauseLabel)!;
            playPause.ToolTip = player.PlayPauseLabel;
            shuffle.ContentTintColor = player.Shuffle ? NSColor.ControlAccent : null;
            shuffle.ToolTip = player.ShuffleLabel;
            repeat.Image = NSImage.GetSystemSymbol(player.RepeatsOne ? "repeat.1" : "repeat", player.RepeatLabel)!;
            repeat.ContentTintColor = player.IsRepeating ? NSColor.ControlAccent : null;
            repeat.ToolTip = player.RepeatLabel;
            next.Enabled = player.CanGoNext;
            position.MaxValue = Math.Max(player.Length, 1);
            length.StringValue = player.LengthText;
            problem.StringValue = player.Problem ?? "";
            problem.Hidden = player.Problem is null;
        });
        _bindings.Property(player, nameof(PlayerViewModel.Position), () =>
        {
            // Not while the slider is being dragged, which would pull it back under the pointer.
            if (NSApplication.SharedApplication.CurrentEvent?.Type is not NSEventType.LeftMouseDragged) position.DoubleValue = player.Position;
            at.StringValue = player.PositionText;
        });
        _bindings.Property(player, nameof(PlayerViewModel.Volume), () => volume.DoubleValue = player.Volume);
        _bindings.Properties(player, [nameof(PlayerViewModel.Ambience), nameof(PlayerViewModel.IsAmbiencePlaying), nameof(PlayerViewModel.AmbienceVolume)], () =>
        {
            ambience.Hidden = !player.HasAmbience;
            ambienceTitle.StringValue = player.Ambience?.Title ?? "";
            ambiencePause.Image = NSImage.GetSystemSymbol(player.IsAmbiencePlaying ? "pause.fill" : "play.fill", player.AmbiencePlayPauseLabel)!;
            ambiencePause.ToolTip = player.AmbiencePlayPauseLabel;
            ambienceVolume.DoubleValue = player.AmbienceVolume;
        });
        _bindings.Collection(player.Queue, _ => ShowQueue());
        ShowQueue();
    }

    void ShowQueue()
    {
        _rows.Dispose();
        _rows = new Bindings();
        foreach (var view in _queue.ArrangedSubviews) view.RemoveFromSuperview();
        foreach (var track in _player.Queue)
        {
            var row = NSButton.CreateButton(track.LengthText.Length > 0 ? $"{track.Title}  ·  {track.LengthText}" : track.Title, () => _player.PlayTrackCommand.Execute(track));
            row.Bordered = false;
            row.Alignment = NSTextAlignment.Left;
            row.LineBreakMode = NSLineBreakMode.TruncatingTail;
            row.WidthAnchor.ConstraintLessThanOrEqualTo(320).Active = true;
            _rows.Property(track, nameof(PlayerTrackViewModel.IsCurrent), () =>
                row.ContentTintColor = track.IsCurrent ? NSColor.ControlAccent : null);
            row.Font = NSFont.SystemFontOfSize(12)!;
            _queue.AddArrangedSubview(row);
        }
    }

    void ShowCover(NSImageView view, string? name)
    {
        var placeholder = NSImage.GetSystemSymbol("music.note", "Track")!;
        if (name is null)
        {
            view.Image = placeholder;
            return;
        }
        view.Image = _covers.TryGet(name) ?? placeholder;
        if (view.Image == placeholder) _ = LoadAsync();

        async Task LoadAsync()
        {
            if (await _covers.LoadAsync(name) is { } image && _player.Current?.Cover == name) view.Image = image;
        }
    }

    static NSTextField Heading(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.BoldSystemFontOfSize(11)!;
        label.TextColor = NSColor.SecondaryLabel;
        return label;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bindings.Dispose();
            _rows.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The queue's rows from the top down, as a scroll view's document reads them.</summary>
    sealed class FlippedView : NSView
    {
        public FlippedView(NSView content)
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            content.TranslatesAutoresizingMaskIntoConstraints = false;
            AddSubview(content);
            content.LeadingAnchor.ConstraintEqualTo(LeadingAnchor).Active = true;
            content.TrailingAnchor.ConstraintEqualTo(TrailingAnchor).Active = true;
            content.TopAnchor.ConstraintEqualTo(TopAnchor).Active = true;
            content.BottomAnchor.ConstraintEqualTo(BottomAnchor).Active = true;
            WidthAnchor.ConstraintEqualTo(332).Active = true;
        }

        public override bool IsFlipped => true;
    }
}
