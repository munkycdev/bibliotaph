using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Bibliotaph.App.ViewModels;
using Bibliotaph.App.Views;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

static partial class SmokeTest
{
    /// <summary>
    /// A new library opens on Home under the tour's first callout, Add a folder, with the window dimmed round the button.
    /// Skip tour ends it for good: Home opens plain afterwards.
    /// </summary>
    static async Task ShowFirstRunTourAsync(IServiceProvider services, Window window)
    {
        var navigation = services.GetRequiredService<INavigationService>();
        var tour = services.GetRequiredService<TourViewModel>();
        navigation.NavigateTo(Route.Home);
        await Settle(window);
        await WaitUntilAsync(window, () => tour.IsOpen, () => "The tour didn't open on Home with no folders.");
        var step = tour.Current ?? throw new InvalidOperationException("The tour is open without a step.");
        if (!step.Anchors.Contains(TourAnchor.AddFolder) || tour.NextText != "Add a folder")
            throw new InvalidOperationException($"The tour opened at {step.Title}, with {tour.NextText}, not at Add a folder.");
        if (tour.CanGoBack) throw new InvalidOperationException("The tour's first step offers Back.");
        await ExpectSpotlightAsync(window, tour, window.FindName("Tour") as TourView ?? throw new InvalidOperationException("The window has no tour view."));
        // Tab stays inside the callout while the tour is open, so only the names are audited here.
        CheckNames(window, "Home under the tour");

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "SkipTour"), "Skip tour");
        await WaitUntilAsync(window, () => !tour.IsOpen, () => "Skip tour left the tour open.");
        await ExpectStageAsync(services, "Skip tour");
        navigation.NavigateTo(Route.Library);
        await Settle(window);
        navigation.NavigateTo(Route.Home);
        await Settle(window);
        await Task.Delay(300);
        if (tour.IsOpen) throw new InvalidOperationException("The tour opened again on Home after Skip tour.");
    }

    /// <summary>
    /// Settings > Preferences > Show the tour again, with books in: Home, then every step of the Books chapter in order,
    /// each with its spotlight, Back once, and Done at the end.
    /// </summary>
    static async Task ReplayTourAsync(IServiceProvider services, Window window)
    {
        var navigation = services.GetRequiredService<INavigationService>();
        var shell = services.GetRequiredService<ShellViewModel>();
        var tour = services.GetRequiredService<TourViewModel>();
        navigation.NavigateTo(Route.Settings);
        await Settle(window);
        if (shell.CurrentPage is not SettingsViewModel settings) throw new InvalidOperationException("Settings didn't open.");
        settings.Show(SettingsSection.Preferences);
        await Settle(window);
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "ShowTour"), "Show the tour again");
        await WaitUntilAsync(window, () => tour.IsOpen && navigation.Current?.Route == Route.Home, () => "Show the tour again didn't open the tour on Home.");
        var view = window.FindName("Tour") as TourView ?? throw new InvalidOperationException("The window has no tour view.");

        var steps = Tour.Steps(TourChapter.Books, TourClient.Windows);
        for (var i = 0; i < steps.Count; i++)
        {
            await WaitUntilAsync(window, () => tour.Current?.Title == steps[i].Title, () => $"The tour shows {tour.Current?.Title}, not step {i + 1}, {steps[i].Title}.");
            if (tour.StepText != $"Step {i + 1} of {steps.Count}") throw new InvalidOperationException($"The tour says {tour.StepText} at step {i + 1}.");
            await ExpectSpotlightAsync(window, tour, view);
            if (i == 1)
            {
                Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "TourBack"), "Back");
                await WaitUntilAsync(window, () => tour.Current?.Title == steps[0].Title, () => "Back didn't return to the first step.");
                await ExpectSpotlightAsync(window, tour, view);
                Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "TourNext"), "Next");
                await WaitUntilAsync(window, () => tour.Current?.Title == steps[1].Title, () => "Next didn't return to the second step.");
            }
            var last = i == steps.Count - 1;
            if (tour.NextText != (last ? "Done" : "Next")) throw new InvalidOperationException($"Step {i + 1}'s button says {tour.NextText}.");
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "TourNext"), tour.NextText);
        }
        await WaitUntilAsync(window, () => !tour.IsOpen, () => "Done left the tour open.");
        await ExpectStageAsync(services, "Done");
        while (navigation.GoBack()) await Settle(window);
    }

    /// <summary>The setting is written after the overlay closes, so it gets a moment.</summary>
    static async Task ExpectStageAsync(IServiceProvider services, string what)
    {
        var settings = services.GetRequiredService<SettingsStore>();
        for (var i = 0; i < 50; i++)
        {
            if (await settings.GetAsync(SettingKeys.TourStage) == Tour.StageBooks) return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"{what} didn't remember that the tour is done.");
    }

    /// <summary>The dimmed window has a hole round the step's controls, and the callout sits inside the window.</summary>
    static async Task ExpectSpotlightAsync(Window window, TourViewModel tour, TourView view)
    {
        var backdrop = view.FindName("Backdrop") as Path ?? throw new InvalidOperationException("The tour view has no backdrop.");
        await WaitUntilAsync(window, () => backdrop.Data is CombinedGeometry { Geometry2: RectangleGeometry { Rect: { Width: > 0 } } },
            () => $"The tour's {tour.Current?.Title} step has no spotlight.");
        var callout = view.FindName("Callout") as Border ?? throw new InvalidOperationException("The tour view has no callout.");
        await Settle(window);
        var left = Canvas.GetLeft(callout);
        var top = Canvas.GetTop(callout);
        if (left < 0 || top < 0 || left + callout.ActualWidth > view.ActualWidth || top + callout.ActualHeight > view.ActualHeight)
            throw new InvalidOperationException($"The tour's {tour.Current?.Title} callout runs outside the window.");
        var hole = ((CombinedGeometry)backdrop.Data).Geometry2.Bounds;
        if (new Rect(left, top, callout.ActualWidth, callout.ActualHeight).Contains(new Point(hole.X + hole.Width / 2, hole.Y + hole.Height / 2)))
            throw new InvalidOperationException($"The tour's {tour.Current?.Title} callout covers its spotlight.");
    }
}
