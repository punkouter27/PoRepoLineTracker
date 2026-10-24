using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using PoRepoLineTracker.Client;
using PoRepoLineTracker.Client.Services;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Configure HttpClient for API calls.
//
// Every request now goes through AntiforgeryHandler, which attaches the CSRF token to
// state-changing calls. Registering the client by name and resolving HttpClient from the factory
// keeps `@inject HttpClient Http` working unchanged across every page and component.
//
// "ApiRaw" is the same client WITHOUT the handler: AntiforgeryHandler fetches the token with it,
// and routing that fetch back through the handler would re-enter its own gate and deadlock.
builder.Services.AddHttpClient("ApiRaw", client =>
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress));

builder.Services.AddTransient<AntiforgeryHandler>();
builder.Services.AddHttpClient("Api", client =>
        client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<AntiforgeryHandler>();

builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("Api"));

builder.Services.AddScoped<UserPreferencesClient>();

// One hub connection shared by the repository grid and the activity feed. Scoped is the whole
// app's lifetime in a WASM host, which is what makes "shared" true.
builder.Services.AddScoped<AnalysisFeedClient>();
// Registered after DialogService/NotificationService below purely for readability — DI resolves
// by type, not by registration order.
builder.Services.AddScoped<RepositoryCommandClient>();

// Add authentication services
builder.Services.AddScoped<ApiAuthenticationStateProvider>(sp =>
    new ApiAuthenticationStateProvider(sp.GetRequiredService<HttpClient>()));
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<ApiAuthenticationStateProvider>());
builder.Services.AddAuthorizationCore();

// Add Radzen services with Material3 theme
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<NotificationService>();
// Not opened by app code, but RadzenChart injects it to draw series tooltips.
builder.Services.AddScoped<TooltipService>();

await builder.Build().RunAsync();

