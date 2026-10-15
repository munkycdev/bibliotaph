using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Bibliotaph.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace Bibliotaph.App.Views;

/// <summary>
/// The online reader's view: drives the browser for its <see cref="OnlineReaderViewModel"/>. Made again each time the
/// reader shows (going Back to it reopens the page that was in view), and lets go of the browser when it goes.
/// </summary>
public partial class OnlineReaderView
{
    bool _started;
    bool _gone;
    string? _readingScript;

    public OnlineReaderView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await StartAsync();
        Unloaded += (_, _) =>
        {
            _gone = true;
            if (Model is { } model)
            {
                model.Sessions.PropertyChanged -= Sessions_PropertyChanged;
                model.PropertyChanged -= Model_PropertyChanged;
            }
            Web.Dispose();
        };
    }

    OnlineReaderViewModel? Model => DataContext as OnlineReaderViewModel;

    async Task StartAsync()
    {
        if (_started || Model is not { } model) return;
        _started = true;
        model.Sessions.PropertyChanged += Sessions_PropertyChanged;
        if (model.Browsers.OnlyFiles && !model.Start.IsFile)
        {
            // The smoke test reads no real site.
            model.Notice = "The smoke test doesn't open web pages.";
            return;
        }
        try
        {
            await Web.EnsureCoreWebView2Async(await model.Browsers.EnvironmentAsync());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            model.Notice = "Bibliotaph's browser needs the Microsoft Edge WebView2 Runtime, which Windows 11 includes. Open in your browser still works.";
            return;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            model.Notice = $"Bibliotaph's browser couldn't start: {ex.Message}";
            return;
        }

        if (_gone) return;
        var core = Web.CoreWebView2;
        core.NavigationStarting += (_, e) =>
        {
            // A link the user follows off the service goes to the everyday browser; a site's own redirects (signing in) stay.
            if (!e.IsUserInitiated || model.Follows(e.Uri)) return;
            e.Cancel = true;
            model.OpenOutside(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (model.Follows(e.Uri)) core.Navigate(e.Uri);
            else model.OpenOutside(e.Uri);
        };
        core.SourceChanged += (_, _) => model.Address = core.Source;
        core.DocumentTitleChanged += (_, _) => model.PageTitle = core.DocumentTitle;
        core.DownloadStarting += async (_, e) =>
        {
            // A store's Download button on the book's page: the reader says how it goes.
            var download = await model.Browsers.Downloads.TakeAsync(e, model.Request.Place ?? model.Title);
            model.Notice = $"{download.Name}: {download.Status}";
            download.PropertyChanged += (_, p) =>
            {
                if (p.PropertyName == nameof(Services.OnlineDownload.Status)) model.Notice = $"{download.Name}: {download.Status}";
            };
        };
        core.HistoryChanged += (_, _) => (model.CanGoBack, model.CanGoForward) = (core.CanGoBack, core.CanGoForward);
        model.PropertyChanged += Model_PropertyChanged;
        await ShowReadingModeAsync(core, model);
        // The reader can go (Back, Pop out) while the stylesheet goes on, and its browser with it.
        if (_gone) return;
        core.Navigate(model.Start.AbsoluteUri);
    }

    async void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OnlineReaderViewModel.ReadingMode) && Web.CoreWebView2 is { } core && Model is { } model)
            await ShowReadingModeAsync(core, model);
    }

    /// <summary>
    /// Puts Reading mode's stylesheet on every page of the service from now on, and on the page in view, or takes it
    /// off both. It goes only on the service's own site, not the sites it signs in on.
    /// </summary>
    async Task ShowReadingModeAsync(CoreWebView2 core, OnlineReaderViewModel model)
    {
        try
        {
            var wasOn = _readingScript is not null;
            if (_readingScript is { } id)
            {
                core.RemoveScriptToExecuteOnDocumentCreated(id);
                _readingScript = null;
            }
            if (model.ReadingMode && model.ReadingStyle is { } style && Services.OnlineReaders.SiteOf(model.Request.Link) is { } site)
            {
                var script = ReadingScript(style, site);
                _readingScript = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
                if (!_gone) await core.ExecuteScriptAsync(script);
            }
            else if (wasOn) await core.ExecuteScriptAsync("document.getElementById('bibliotaph-reading')?.remove()");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // The browser closed meanwhile.
        }
    }

    static string ReadingScript(string style, string site) => $$"""
        (() => {
          const site = {{JsonSerializer.Serialize(site)}};
          const host = location.hostname.replace(/^www\./, '');
          if (host !== site && !host.endsWith('.' + site)) return;
          const add = () => {
            if (document.getElementById('bibliotaph-reading')) return;
            const style = document.createElement('style');
            style.id = 'bibliotaph-reading';
            style.textContent = {{JsonSerializer.Serialize(style)}};
            (document.head ?? document.documentElement).appendChild(style);
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', add);
          else add();
        })();
        """;

    /// <summary>The browser draws over the binder dialog, so it is hidden while the dialog is open.</summary>
    void Sessions_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionActions.Dialog) && sender is SessionActions sessions)
            Web.Visibility = sessions.Dialog is null ? Visibility.Visible : Visibility.Hidden;
    }

    void AddPageMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void Back_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.GoBack();

    void Forward_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.GoForward();

    void Reload_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Reload();

    async void NoteBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Model is { } model) await model.SaveNoteAsync();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Model?.Sessions.Dialog is not null)
        {
            Model.Sessions.Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && Web.CoreWebView2 is { } core)
        {
            core.Reload();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
}
