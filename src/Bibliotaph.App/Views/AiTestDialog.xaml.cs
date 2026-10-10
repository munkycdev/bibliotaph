using System.ComponentModel;
using System.Windows;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Views;

/// <summary>Settings > AI's Test with a book, in a popup that starts the test as it opens and stops it as it closes.</summary>
public partial class AiTestDialog
{
    public AiTestDialog(AiTestViewModel test)
    {
        InitializeComponent();
        // Never taller than the screen (a 1080p one at 200% scaling has under 500): the results scroll instead.
        MaxHeight = Math.Min(MaxHeight, SystemParameters.WorkArea.Height * 0.9);
        Test = test;
        DataContext = test;
        Loaded += (_, _) => _ = test.TestAgainCommand.ExecuteAsync(null);
    }

    public AiTestViewModel Test { get; }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        Test.Stop();
        base.OnClosing(e);
    }
}
