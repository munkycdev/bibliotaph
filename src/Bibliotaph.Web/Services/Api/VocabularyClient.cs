using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Settings › Vocabulary and Tags.</summary>
public sealed class VocabularyClient(ApiHttp http)
{
    public Task<AiTermChoice> AiTermChoiceAsync(string vocabulary, CancellationToken ct = default) =>
        http.SendAsync<AiTermChoice>(HttpMethod.Get, $"api/vocabulary/{vocabulary}/ai-terms", null, ct);

    public Task<AiTermChange> SetAiTermChoiceAsync(string vocabulary, bool add, CancellationToken ct = default) =>
        http.SendAsync<AiTermChange>(HttpMethod.Put, $"api/vocabulary/{vocabulary}/ai-terms", new AiTermChoice(add), ct);

    public Task<List<VocabularyTerm>> VocabularyAsync(string vocabulary, CancellationToken ct = default) =>
        http.SendAsync<List<VocabularyTerm>>(HttpMethod.Get, $"api/vocabulary/{Uri.EscapeDataString(vocabulary)}", null, ct);

    public Task<VocabularyChange> AddTermAsync(string vocabulary, string label, CancellationToken ct = default) =>
        http.SendAsync<VocabularyChange>(HttpMethod.Post, $"api/vocabulary/{Uri.EscapeDataString(vocabulary)}", new NewTerm(label), ct);

    public Task<VocabularyChange> RenameTermAsync(long id, string label, string? shortLabel, CancellationToken ct = default) =>
        http.SendAsync<VocabularyChange>(HttpMethod.Put, $"api/vocabulary/terms/{id}", new TermRename(label, shortLabel), ct);

    public Task<VocabularyChange> AddAliasAsync(long termId, string text, CancellationToken ct = default) =>
        http.SendAsync<VocabularyChange>(HttpMethod.Post, $"api/vocabulary/terms/{termId}/aliases", new NewAlias(text), ct);

    public Task RemoveAliasAsync(long aliasId, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Delete, $"api/vocabulary/aliases/{aliasId}", null, ct);

    public Task<TermOverlapPage> TermOverlapsAsync(string vocabulary, CancellationToken ct = default) =>
        http.SendAsync<TermOverlapPage>(HttpMethod.Get, $"api/vocabulary/{Uri.EscapeDataString(vocabulary)}/overlaps", null, ct);

    /// <summary>Find overlaps: compares the names now and has the AI compare them in the background.</summary>
    public Task<TermOverlapPage> FindTermOverlapsAsync(string vocabulary, CancellationToken ct = default) =>
        http.SendAsync<TermOverlapPage>(HttpMethod.Post, $"api/vocabulary/{Uri.EscapeDataString(vocabulary)}/overlaps", null, ct);

    public Task ForgetKeptApartAsync(string vocabulary, long id, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Delete, $"api/vocabulary/{Uri.EscapeDataString(vocabulary)}/kept-apart/{id}", null, ct);

    public Task<List<UserTag>> UserTagsAsync(CancellationToken ct = default) => http.SendAsync<List<UserTag>>(HttpMethod.Get, "api/tags", null, ct);

    /// <summary>Adds a tag when <paramref name="id"/> is null, otherwise changes it.</summary>
    public Task<UserTagChange> SaveUserTagAsync(long? id, string name, string words, CancellationToken ct = default) =>
        http.SendAsync<UserTagChange>(id is null ? HttpMethod.Post : HttpMethod.Put, id is null ? "api/tags" : $"api/tags/{id}", new UserTagText(name, words), ct);

    public Task DeleteUserTagAsync(long id, CancellationToken ct = default) => http.SendAsync(HttpMethod.Delete, $"api/tags/{id}", null, ct);
}
