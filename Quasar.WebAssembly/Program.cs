using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Quasar.Client;
using Quasar.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddMudServices();
builder.Services.AddScoped<DashboardSdk>();
builder.Services.AddScoped<Quasar.Client.Administration.DashboardPushState>();
builder.Services.AddScoped<ViewerTimeZone>();
var host = builder.Build();
await host.Services.GetRequiredService<ViewerTimeZone>().InitializeAsync(host.Services.GetRequiredService<IJSRuntime>());
await host.RunAsync();
