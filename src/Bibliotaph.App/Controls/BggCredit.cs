using System.Diagnostics;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.Controls;

/// <summary>The "Powered by BGG" logo's link (<c>Bt.PoweredByBgg</c>), the same wherever the logo shows.</summary>
public static class BggCredit
{
    public const string Url = "https://boardgamegeek.com";

    public static ICommand OpenCommand { get; } = new RelayCommand(() => Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true })?.Dispose());
}
