using System.Windows.Data;

namespace Bibliotaph.App.Views;

public partial class SettingsView
{
    public SettingsView() => InitializeComponent();

    /// <summary>A newly chosen section starts at its top, not where the last one was scrolled to.</summary>
    void Section_TargetUpdated(object? sender, DataTransferEventArgs e) => SectionScroller.ScrollToTop();
}
