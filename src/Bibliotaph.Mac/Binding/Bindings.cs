using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using AppKit;
using Foundation;

namespace Bibliotaph.Mac.Binding;

/// <summary>
/// The little of WPF's binding the views need, over INotifyPropertyChanged: run an update now and whenever a property
/// changes, follow a collection, and drive a control from a command. Everything runs on the main thread, where a view
/// model's change from a background thread is moved. Disposing the bag unhooks it all, for a page that is left.
/// </summary>
public sealed class Bindings : IDisposable
{
    readonly List<Action> _undo = [];

    /// <summary>Runs <paramref name="update"/> now and whenever <paramref name="property"/> (or every property) changes.</summary>
    public void Property(INotifyPropertyChanged source, string property, Action update)
    {
        update();
        void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != property) return;
            OnMain(update);
        }
        source.PropertyChanged += Changed;
        _undo.Add(() => source.PropertyChanged -= Changed);
    }

    /// <summary>Runs <paramref name="update"/> for several properties of the same source.</summary>
    public void Properties(INotifyPropertyChanged source, string[] properties, Action update)
    {
        update();
        void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.PropertyName) && !properties.Contains(e.PropertyName)) return;
            OnMain(update);
        }
        source.PropertyChanged += Changed;
        _undo.Add(() => source.PropertyChanged -= Changed);
    }

    /// <summary>Runs <paramref name="changed"/> with each change to a collection.</summary>
    public void Collection(INotifyCollectionChanged source, Action<NotifyCollectionChangedEventArgs> changed)
    {
        void Changed(object? sender, NotifyCollectionChangedEventArgs e) => OnMain(() => changed(e));
        source.CollectionChanged += Changed;
        _undo.Add(() => source.CollectionChanged -= Changed);
    }

    /// <summary>A button or menu item that runs <paramref name="command"/>, enabled while it can.</summary>
    public void Command(NSControl control, ICommand command, Func<object?>? parameter = null)
    {
        void Refresh() => control.Enabled = command.CanExecute(parameter?.Invoke());
        void Changed(object? sender, EventArgs e) => OnMain(Refresh);
        void Activated(object? sender, EventArgs e)
        {
            var value = parameter?.Invoke();
            if (command.CanExecute(value)) command.Execute(value);
        }
        Refresh();
        control.Activated += Activated;
        command.CanExecuteChanged += Changed;
        _undo.Add(() =>
        {
            control.Activated -= Activated;
            command.CanExecuteChanged -= Changed;
        });
    }

    /// <summary>Something else to undo with the rest.</summary>
    public void Add(Action undo) => _undo.Add(undo);

    public void Dispose()
    {
        foreach (var undo in _undo) undo();
        _undo.Clear();
    }

    public static void OnMain(Action action)
    {
        if (NSThread.IsMain) action();
        else NSApplication.SharedApplication.BeginInvokeOnMainThread(action);
    }
}
