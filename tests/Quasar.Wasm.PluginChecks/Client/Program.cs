using Client;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Quasar.Client.Ui;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddQuasarUi();
builder.Services.AddLogging();
builder.Services.AddScoped<Quasar.Services.ViewerTimeZone>();
builder.RootComponents.Add<App>("#app");
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
await builder.Build().RunAsync();
