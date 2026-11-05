using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Backup;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class ServerWorldImportApiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-world-api-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private object? _previous;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private DedicatedServerCatalog Servers => _app.Services.GetRequiredService<DedicatedServerCatalog>();

    public async Task InitializeAsync()
    {
        _previous = _cache.GetValue(null); _cache.SetValue(null, _root);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, RoleHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            Program.ConfigureAuthorizationPolicies(options);
            options.AddPolicy(QuasarPolicyNames.CanEditServers, policy => policy.RequireRole(QuasarRoles.Editor, QuasarRoles.Admin, "server-only"));
        });
        builder.Services.AddSingleton<ServerRestoreCoordinator>();
        builder.Services.AddSingleton<DedicatedServerCatalog>();
        builder.Services.AddSingleton<QuasarConfigProfileCatalog>();
        builder.Services.AddSingleton(service => new DedicatedServerSupervisor(service.GetRequiredService<DedicatedServerCatalog>(),
            new AgentRegistry(null!, null!, null!, null!), null!, null!, null!, new WebServiceOptions(),
            service.GetRequiredService<ServerRestoreCoordinator>(), NullLogger<DedicatedServerSupervisor>.Instance));
        builder.Services.AddSingleton<ServerWorldImportService>();
        _app = builder.Build();
        _app.UseAuthentication(); _app.UseAuthorization();
        _app.MapServerWorldImportApi(new QuasarAuthOptions { Enabled = true });
        await _app.Services.GetRequiredService<QuasarConfigProfileCatalog>().UpsertAsync(new() { ConfigProfileId = "profile", Name = "Profile" });
        await Servers.UpsertAsync(new() { UniqueName = "target", WorldSaveName = "Old", ConfigProfileId = "profile" });
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Add("Role", QuasarRoles.Editor);
    }

    private string ImportUrl(string name = "New") => "/api/servers/target/world/import?saveName=" + name
        + "&importSettings=false&expectedUpdatedAtUtc=" + Uri.EscapeDataString(Servers.GetServer("target")!.UpdatedAtUtc.ToString("O"));

    [Fact]
    public async Task FolderEndpointImportsWithoutTemplateAndViewerCannotMutate()
    {
        string source = Path.Combine(_root, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        var request = new { sourcePath = source, sourceStoppedConfirmed = true };
        _client.DefaultRequestHeaders.Remove("Role"); _client.DefaultRequestHeaders.Add("Role", QuasarRoles.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/servers/target/world")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync(ImportUrl(), request)).StatusCode);
        Assert.Equal("Old", Servers.GetServer("target")!.WorldSaveName);
        _client.DefaultRequestHeaders.Remove("Role"); _client.DefaultRequestHeaders.Add("Role", QuasarRoles.Editor);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync(ImportUrl(), request)).StatusCode);
        Assert.Equal("New", Servers.GetServer("target")!.WorldSaveName);
        Assert.False(Directory.Exists(MagnetarPaths.GetQuasarWorldTemplatesDirectory()));
    }

    [Fact]
    public async Task RawZipEndpointRejectsStaleRevisionAndUnsafePathsThenAcceptsWorld()
    {
        using var staleBody = Zip("Sandbox.sbc");
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync("/api/servers/target/world/import?saveName=New&importSettings=false", staleBody)).StatusCode);
        using var corrupt = new StringContent("not a ZIP");
        corrupt.Headers.ContentType = new("application/zip");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync(ImportUrl(), corrupt)).StatusCode);
        using var hostile = Zip("../Sandbox.sbc");
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync(ImportUrl(), hostile)).StatusCode);
        Assert.Equal("Old", Servers.GetServer("target")!.WorldSaveName);
        using var valid = Zip("Save/Sandbox.sbc");
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync(ImportUrl(), valid)).StatusCode);
        Assert.True(File.Exists(Path.Combine(Servers.GetServer("target")!.GetWorldSavePath(), "Sandbox.sbc")));
    }

    [Fact]
    public async Task ImportedSettingsRequireConfigPermissionAndSourceTypeMustBeExplicit()
    {
        _client.DefaultRequestHeaders.Remove("Role"); _client.DefaultRequestHeaders.Add("Role", "server-only");
        using var zip = Zip("Sandbox.sbc");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync(ImportUrl().Replace("importSettings=false", "importSettings=true"), zip)).StatusCode);
        using var formLike = new StringContent("not a ZIP");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync(ImportUrl(), formLike)).StatusCode);
        using var missingChoice = Zip("Sandbox.sbc");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync(ImportUrl().Replace("importSettings=false", "importSettings=invalid"), missingChoice)).StatusCode);
        Assert.Equal("Old", Servers.GetServer("target")!.WorldSaveName);
        Assert.Single(_app.Services.GetRequiredService<QuasarConfigProfileCatalog>().GetProfiles());
    }

    private static HttpContent Zip(string path)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry(path).Open())) writer.Write("<MyObjectBuilder_Checkpoint />");
        stream.Position = 0;
        var content = new StreamContent(stream);
        content.Headers.ContentType = new("application/zip");
        return content;
    }

    private sealed class RoleHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, Request.Headers["Role"].ToString())], "test")), "test")));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose(); await _app.DisposeAsync();
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
