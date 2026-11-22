using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.Presentation.ViewModels;

/// <summary>
/// Settings › Online's book catalogues: the user's own RPGGeek token and Google Books key, for a computer that looks
/// books up on its own rather than through bibliotaph.online. Each is kept where the app keeps secrets; a book none of
/// the catalogues found is asked again once one is saved.
/// </summary>
public sealed partial class CatalogueKeysViewModel(OwnCatalogues catalogues) : ObservableObject
{
    /// <summary>Where a token comes from: an app registered with BoardGameGeek.</summary>
    public static Uri RpgGeekRegistration { get; } = new("https://boardgamegeek.com/applications");

    /// <summary>Where a key comes from: the Books API in the user's own Google Cloud project.</summary>
    public static Uri GoogleBooksConsole { get; } = new("https://console.cloud.google.com/apis/library/books.googleapis.com");

    public bool HasRpgGeekToken => catalogues.HasRpgGeekToken;

    public bool HasGoogleBooksKey => catalogues.HasGoogleBooksKey;

    public string RpgGeekState => HasRpgGeekToken ? "Saved. Books are looked up on RPGGeek too." : "Not set. RPGGeek isn't asked.";

    public string GoogleBooksState => HasGoogleBooksKey ? "Saved. Books are looked up on Google Books too." : "Not set. Google Books isn't asked.";

    /// <summary>Where this computer looks books up on its own now: "RPGGeek or AdventureLookup or Open Library".</summary>
    public string Catalogues => catalogues.Name;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRpgGeekTokenCommand))]
    public partial string NewRpgGeekToken { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveGoogleBooksKeyCommand))]
    public partial string NewGoogleBooksKey { get; set; } = "";

    /// <summary>Why a token or key couldn't be kept.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    bool CanSaveRpgGeekToken => NewRpgGeekToken.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSaveRpgGeekToken))]
    void SaveRpgGeekToken()
    {
        Problem = catalogues.SaveRpgGeekToken(NewRpgGeekToken) ? null : "The token couldn't be kept. Try again.";
        if (Problem is null) NewRpgGeekToken = "";
        Changed();
    }

    [RelayCommand]
    void ForgetRpgGeekToken()
    {
        catalogues.ForgetRpgGeekToken();
        Changed();
    }

    bool CanSaveGoogleBooksKey => NewGoogleBooksKey.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSaveGoogleBooksKey))]
    void SaveGoogleBooksKey()
    {
        Problem = catalogues.SaveGoogleBooksKey(NewGoogleBooksKey) ? null : "The key couldn't be kept. Try again.";
        if (Problem is null) NewGoogleBooksKey = "";
        Changed();
    }

    [RelayCommand]
    void ForgetGoogleBooksKey()
    {
        catalogues.ForgetGoogleBooksKey();
        Changed();
    }

    void Changed()
    {
        OnPropertyChanged(nameof(HasRpgGeekToken));
        OnPropertyChanged(nameof(HasGoogleBooksKey));
        OnPropertyChanged(nameof(RpgGeekState));
        OnPropertyChanged(nameof(GoogleBooksState));
        OnPropertyChanged(nameof(Catalogues));
    }
}
