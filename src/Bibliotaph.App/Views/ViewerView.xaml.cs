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
    }

    void PageBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => PageBox.SelectAll();
}
