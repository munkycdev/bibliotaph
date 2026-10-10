using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Views;

/// <summary>
/// Connects the page surface, which is driven by calls rather than bindings, to the viewer's view model: it loads the
/// open PDF, shows the highlights, scrolls where the model asks and reports the page in view.
/// </summary>
public partial class ViewerView
{
    ViewerViewModel? _model;
    OpenPdf? _shown;

    public ViewerView()
    {
        InitializeComponent();
        PagesView.CurrentPageChanged += (_, _) => _model?.CurrentPageIndex = PagesView.CurrentPageIndex;
        PagesView.Notice += (_, text) => _model?.ShowNotice(text);
        DataContextChanged += (_, _) => Attach(DataContext as ViewerViewModel);
        Loaded += (_, _) => Attach(DataContext as ViewerViewModel);
        Unloaded += (_, _) => Attach(null);
        PreviewKeyDown += OnPreviewKeyDown;
        CommandBindings.Add(new CommandBinding(NavigationCommands.IncreaseZoom, (_, _) => StepZoom(up: true), CanZoom));
        CommandBindings.Add(new CommandBinding(NavigationCommands.DecreaseZoom, (_, _) => StepZoom(up: false), CanZoom));
    }

    void Attach(ViewerViewModel? model)
    {
        if (ReferenceEquals(_model, model)) return;
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.GoToRequested -= OnGoToRequested;
        }
        _model = model;
        if (model is null)
        {
            ShowPdf(null);
            return;
        }
        model.PropertyChanged += OnModelChanged;
        model.GoToRequested += OnGoToRequested;
        ShowPdf(model.Pdf);
        ShowHighlights();
    }

    void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewerViewModel.Pdf):
                ShowPdf(_model?.Pdf);
                break;
            case nameof(ViewerViewModel.Highlights):
                ShowHighlights();
                break;
            case nameof(ViewerViewModel.IsPdf) when _model?.IsPdf == true:
                // The pages take keyboard focus once they're shown, so Ctrl+A and the arrow keys work straight away.
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PagesView.Focus());
                CommandManager.InvalidateRequerySuggested();
                break;
            case nameof(ViewerViewModel.IsPdf) or nameof(ViewerViewModel.IsImage):
                // The zoom buttons and keys follow what's open.
                CommandManager.InvalidateRequerySuggested();
                break;
        }
    }

    void ShowPdf(OpenPdf? pdf)
    {
        if (ReferenceEquals(_shown, pdf)) return;
        _shown = pdf;
        if (pdf is null) PagesView.Unload();
        else PagesView.Load(pdf.Renderer, pdf.Doc, pdf.Text, pdf.PageIndex, pdf.PdfTop);
    }

    void ShowHighlights()
    {
        if (_model is { } model) PagesView.ShowHighlights(model.Highlights.Items, model.Highlights.Current);
    }

    void OnGoToRequested(object? sender, PageTarget target) => PagesView.GoToPage(target.PageIndex, target.PdfTop);

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _model?.IsPdf == true)
        {
            FindBox.Focus();
            FindBox.SelectAll();
            e.Handled = true;
        }
        // In a pack, the arrow keys step through its images (F4 plan, choice 7), ahead of the image's scroll bars.
        else if (e.Key is Key.Left or Key.Right && Keyboard.Modifiers == ModifierKeys.None && _model is { IsImage: true, IsInPack: true }
            && e.OriginalSource is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            var command = e.Key == Key.Left ? _model.PreviousImageCommand : _model.NextImageCommand;
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        }
    }

    // The step starts from the size on screen, which only the surface knows when the pages are fitted.
    void StepZoom(bool up)
    {
        if (_model?.IsPdf == true) PagesView.StepZoom(up);
        else if (_model?.IsImage == true) ImageSurface.StepZoom(up);
    }

    void CanZoom(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _model is { IsPdf: true } or { IsImage: true };

    void PageBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => PageBox.SelectAll();
}
