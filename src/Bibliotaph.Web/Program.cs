using Bibliotaph.Web;
using Bibliotaph.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient(new ClientHeaderHandler { InnerHandler = new HttpClientHandler() })
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<Session>();
builder.Services.AddScoped<Theme>();
builder.Services.AddScoped<LibraryActivity>();
builder.Services.AddScoped<CollectionDialogs>();
builder.Services.AddScoped<CollectionActions>();
builder.Services.AddScoped<Pins>();

await builder.Build().RunAsync();
