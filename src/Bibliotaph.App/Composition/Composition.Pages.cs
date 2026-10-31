using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App;

static partial class Composition
{
    /// <summary>The pages and their view models, navigation, the main window and the readers.</summary>
    public static IServiceCollection AddPages(this IServiceCollection services)
    {
        services.AddSingleton<INavigationService>(sp => new NavigationService(route => CreatePage(sp, route)));
        services.AddSingleton<Func<LibraryViewModel>>(sp => () => sp.GetRequiredService<LibraryViewModel>());
        services.AddSingleton<LibraryPages>();
        services.AddSingleton<CollectionDirectory>();
        services.AddSingleton<SmartViewDirectory>();
        services.AddSingleton<PinDirectory>();
        services.AddSingleton<PinOpener>();
        services.AddSingleton<InspectorFactory>();
        services.AddSingleton<DetailsPaneViewModel>();
        services.AddSingleton<LibrarySelectionFactory>();
        services.AddTransient<CollectionActions>();
        services.AddSingleton<SessionDirectory>();
        services.AddTransient<SessionActions>();
        services.AddSingleton<Func<SessionActions>>(sp => () => sp.GetRequiredService<SessionActions>());
        services.AddSingleton<Func<SessionPackViewModel>>(sp => () => sp.GetRequiredService<SessionPackViewModel>());
        services.AddSingleton<Func<SessionReaderViewModel>>(sp => () => sp.GetRequiredService<SessionReaderViewModel>());
        services.AddSingleton<SessionPages>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<TourViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<CollectionsViewModel>();
        services.AddTransient<SessionsViewModel>();
        services.AddTransient<SessionPackViewModel>();
        services.AddTransient<SessionReaderViewModel>();
        services.AddTransient<NeedsReviewViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<LibrarySectionViewModel>();
        services.AddTransient<ProcessingSectionViewModel>();
        services.AddTransient<PreferencesSectionViewModel>();
        services.AddTransient<VocabularyViewModel>();
        services.AddTransient<PasswordsSectionViewModel>();
        services.AddTransient<DevicesSectionViewModel>();
        services.AddTransient<StartOverSectionViewModel>();
        services.AddTransient<BackupSectionViewModel>();
        services.AddTransient<AiSettingsViewModel>();
        services.AddTransient<BuiltInAiViewModel>();
        services.AddTransient<AiReadingViewModel>();
        services.AddTransient<SummaryViewModel>();
        services.AddTransient<PilotPanelViewModel>();
        services.AddTransient<PilotReviewViewModel>();
        // One check a download page, so a check keeps running while the library is shown.
        services.AddSingleton<DownloadCheckViewModel>();
        // One Settings > Online libraries, so a preview is still there after a look at the Library.
        services.AddSingleton<OnlineLibrariesViewModel>();
        services.AddSingleton<SearchGuideViewModel>();
        services.AddSingleton<LibraryAskViewModel>();
        services.AddSingleton<PlainSearchViewModel>();
        services.AddSingleton<MainWindow>();
        // Readers, in the main window or a pop-out, are made by ReaderWindows with the book they open.
        services.AddSingleton<CoverPages>();
        services.AddSingleton<DocumentOpener>();
        services.AddSingleton<ViewerServices>();
        services.AddSingleton<ReaderWindows>();
        return services;
    }

    static PageViewModel CreatePage(IServiceProvider services, Route route) => route switch
    {
        Route.Home => services.GetRequiredService<HomeViewModel>(),
        Route.Library => services.GetRequiredService<LibraryViewModel>(),
        Route.Collections => services.GetRequiredService<CollectionsViewModel>(),
        // A binder's reader and its list page are opened with their binder (SessionPages); by route alone, the list of them.
        Route.Sessions or Route.SessionPack or Route.SessionEdit => services.GetRequiredService<SessionsViewModel>(),
        Route.NeedsReview => services.GetRequiredService<NeedsReviewViewModel>(),
        Route.Settings => services.GetRequiredService<SettingsViewModel>(),
        Route.PilotReview => services.GetRequiredService<PilotReviewViewModel>(),
        Route.DownloadCheck => services.GetRequiredService<DownloadCheckViewModel>(),
        Route.AiReading => services.GetRequiredService<AiReadingViewModel>(),
        Route.Summary => services.GetRequiredService<SummaryViewModel>(),
        // An online reader opens with its book (OnlineReaders); by route alone, the Library.
        Route.OnlineReader => services.GetRequiredService<LibraryViewModel>(),
        Route.Viewer => services.GetRequiredService<ReaderWindows>().Create(null),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
    };
}
