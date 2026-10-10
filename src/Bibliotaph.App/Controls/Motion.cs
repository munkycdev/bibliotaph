using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace Bibliotaph.App.Controls;

/// <summary>
/// Windows' "Animation effects" setting, for templates to bind to as <c>{Binding IsOn, Source={x:Static c:Motion.Current}}</c>.
/// Every animation in the app goes through this, so with the setting off nothing fades, slides or sweeps: what an
/// animation would end on shows at once. Bindings follow the setting live, as Windows announces a change.
/// </summary>
public sealed class Motion : INotifyPropertyChanged
{
    Motion() => SystemParameters.StaticPropertyChanged += (_, e) =>
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation) || IsOn == SystemParameters.ClientAreaAnimation) return;
        IsOn = SystemParameters.ClientAreaAnimation;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOn)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PopupAnimation)));
    };

    public static Motion Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True when Windows' animation effects are on.</summary>
    public bool IsOn { get; private set; } = SystemParameters.ClientAreaAnimation;

    /// <summary>How a popup opens: a fade, or at once with animation effects off.</summary>
    public PopupAnimation PopupAnimation => IsOn ? PopupAnimation.Fade : PopupAnimation.None;
}
