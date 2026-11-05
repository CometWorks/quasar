using System.Globalization;
using System.Xml;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Quasar.Models;
using Quasar.Services.Auth;

namespace Quasar.Services;

internal static class ServerWorldImportApi
{
    internal sealed record FolderRequest(string SourcePath, bool SourceStoppedConfirmed);

    public static void MapServerWorldImportApi(this WebApplication app, QuasarAuthOptions auth)
    {
        var read = app.MapGet("/api/servers/{uniqueName}/world", (string uniqueName, DedicatedServerCatalog servers) =>
            servers.GetServer(uniqueName) is { } server ? Results.Json(new { server.UniqueName, server.WorldSaveName,
                server.ConfigProfileId, server.GoalState, server.UpdatedAtUtc }) : Results.NotFound());
        var import = app.MapPost("/api/servers/{uniqueName}/world/import", async (string uniqueName, HttpContext context,
            DedicatedServerCatalog servers, ServerWorldImportService imports, IAuthorizationService authorization,
            CancellationToken token) =>
        {
            string settingsChoice = context.Request.Query["importSettings"].ToString();
            if (!bool.TryParse(settingsChoice, out bool importSettings))
                return Results.BadRequest(new { error = "Choose importSettings=true or importSettings=false explicitly." });
            if (auth.Enabled && importSettings && !(await authorization.AuthorizeAsync(context.User, null, QuasarPolicyNames.CanEditConfigs)).Succeeded)
                return Results.Forbid();
            var server = servers.GetServer(uniqueName);
            if (server is null) return Results.NotFound();
            if (!DateTimeOffset.TryParse(context.Request.Query["expectedUpdatedAtUtc"], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var expected) || expected != server.UpdatedAtUtc)
                return Results.Conflict(new { error = "Read the current server world and supply its expectedUpdatedAtUtc before importing." });
            if (!imports.CanImport(server)) return Results.Conflict(new { error = "Stop the server and set its goal Off before importing a world." });
            try
            {
                string saveName = context.Request.Query["saveName"].ToString().Trim();
                WorldSaveFiles.ValidateSaveName(saveName);
                var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (size is { IsReadOnly: false }) size.MaxRequestBodySize = WorldSaveFiles.MaxUploadBytes;
                string parent = DedicatedServerPathResolver.Resolve(server).SavesPath;
                StagedWorld staged;
                string type = context.Request.ContentType?.Split(';')[0].Trim() ?? "";
                if (type.Equals("application/zip", StringComparison.OrdinalIgnoreCase))
                    staged = await WorldSaveFiles.FromZipAsync(context.Request.Body, parent, cancellationToken: token);
                else if (type.Equals("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    // A folder request is metadata, not a large archive upload.
                    if (context.Request.ContentLength > 16 * 1024) return Results.BadRequest(new { error = "Folder request is too large." });
                    if (size is { IsReadOnly: false }) size.MaxRequestBodySize = 16 * 1024;
                    var request = await context.Request.ReadFromJsonAsync<FolderRequest>(token);
                    if (request is not { SourceStoppedConfirmed: true } || string.IsNullOrWhiteSpace(request.SourcePath))
                        return Results.BadRequest(new { error = "Provide sourcePath and confirm sourceStoppedConfirmed." });
                    staged = await imports.StageFolderAsync(request.SourcePath, parent, token: token);
                }
                else return Results.BadRequest(new { error = "Use application/zip for a raw world ZIP or application/json for a folder request." });
                using (staged)
                {
                    var updated = await imports.ImportAsync(server, staged, saveName, importSettings, token);
                    return Results.Json(new { updated.UniqueName, updated.WorldSaveName, updated.ConfigProfileId,
                        updated.GoalState, updated.UpdatedAtUtc, PreviousWorldSaveName = server.WorldSaveName });
                }
            }
            catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
            catch (Exception error) when (error is IOException or InvalidDataException or XmlException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { return Results.BadRequest(new { error = error.Message }); }
        });
        if (auth.Enabled)
        {
            read.RequireAuthorization(QuasarPolicyNames.CanView);
            import.RequireAuthorization(QuasarPolicyNames.CanEditServers);
        }
    }
}
