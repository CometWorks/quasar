using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Quasar.Client;
using Quasar.Client.Ui;
using Quasar.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddMudServices();
// Native dialog and toast services; QuasarThemeProvider renders their hosts.
builder.Services.AddQuasarUi();
builder.Services.AddScoped<DashboardSdk>();
builder.Services.AddScoped<Quasar.Client.Administration.DashboardPushState>();
builder.Services.AddScoped<ViewerTimeZone>();
var host = builder.Build();
await host.Services.GetRequiredService<ViewerTimeZone>().InitializeAsync(host.Services.GetRequiredService<IJSRuntime>());
await host.RunAsync();
