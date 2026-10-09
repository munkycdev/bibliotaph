using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Views;

/// <summary>
/// The About popup: the version, the GPL's notices and links, and a reader for the licence files that ship in the
/// <c>licenses</c> folder beside the app. The GPL asks for these notices in the UI, so a fork keeps them too.
/// </summary>
public partial class AboutDialog
{
    public const string LicenceFile = "LICENSE.txt";
    public const string NoticesFile = "THIRD-PARTY-NOTICES.md";

    readonly AboutInfo _info;
    readonly string _licenses = Path.Combine(AppContext.BaseDirectory, "licenses");

    public AboutDialog(AboutInfo info)
    {
        InitializeComponent();
        _info = info;
        // An Image takes an .ico's first frame, the 16 px one; a large frame scaled down stays sharp at any DPI.
        var icon = new IconBitmapDecoder(new Uri("pack://application:,,,/Assets/Bibliotaph.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        AppIcon.Source = icon.Frames.OrderBy(frame => frame.PixelWidth).FirstOrDefault(frame => frame.PixelWidth >= 96) ?? icon.Frames[^1];
        VersionLine.Text = $"Version {info.VersionText}";
        CopyrightLine.Text = AboutInfo.Copyright;
    }

    public AboutInfo Info => _info;

    /// <summary>True while the licence reader, rather than the summary, is showing.</summary>
    public bool ShowingLicences => Licences.Visibility == Visibility.Visible;

    public string LicenceTextShown => LicenceText.Text;

    void Licence_Click(object sender, RoutedEventArgs e) => ShowLicence(LicenceFile);

    void Notices_Click(object sender, RoutedEventArgs e) => ShowLicence(NoticesFile);

    /// <summary>Opens the licence reader at one file, listing every file in the licenses folder to switch between.</summary>
    public void ShowLicence(string relativePath)
    {
        // Fonts' and icons' licences sit in subfolders; the app's own two files come first.
        var files = Directory.Exists(_licenses)
            ? Directory.GetFiles(_licenses, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_licenses, path))
                .OrderBy(path => path switch { LicenceFile => 0, NoticesFile => 1, _ => 2 })
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new Choice<string>(path, path.Replace('\\', '/')))
                .ToList()
            : [];
        LicenceFiles.ItemsSource = files;
        LicenceFiles.Visibility = files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Summary.Visibility = Visibility.Collapsed;
        Licences.Visibility = Visibility.Visible;
        var wanted = files.FirstOrDefault(f => f.Value == relativePath) ?? files.FirstOrDefault();
        if (wanted is null) LicenceText.Text = $"The licence files weren't found beside Bibliotaph. They're also in its source code: {AboutInfo.SourceUrl}";
        else LicenceFiles.SelectedItem = wanted;
        LicenceText.Focus();
    }

    void LicenceFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LicenceFiles.SelectedItem is not Choice<string> file) return;
        try
        {
            LicenceText.Text = File.ReadAllText(Path.Combine(_licenses, file.Value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LicenceText.Text = $"{file.Label} couldn't be read: {ex.Message}";
        }
        LicenceText.ScrollToHome();
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        Licences.Visibility = Visibility.Collapsed;
        Summary.Visibility = Visibility.Visible;
    }

    void Source_Click(object sender, RoutedEventArgs e) => OpenInBrowser(AboutInfo.SourceUrl);

    void Issues_Click(object sender, RoutedEventArgs e) => OpenInBrowser(AboutInfo.IssuesUrl);

    static void OpenInBrowser(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_info.Details);
            CopyButton.Content = "Copied";
        }
        catch (COMException)
        {
            // Another app has the clipboard open; a second click usually works.
            CopyButton.Content = "Try again";
        }
    }
}
