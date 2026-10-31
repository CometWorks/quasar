using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Quasar.Hosting;

namespace Quasar.Services;

public sealed class HostedPanelService(IHttpClientFactory clients, ILogger<HostedPanelService> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public HostedPanelState State => HostedPanelState.Read() ?? new();
    public string Status { get; private set; } = "Not enrolled";
    public bool AccessActive { get; private set; }
    public HostedConnectorHealth? ConnectorHealth
    {
        get
        {
            var state = State;
            var health = HostedConnectorHealth.Read();
            return AccessActive && state is { Choice: "hosted", Credential: not null } && health is not null && health.PanelId == state.PanelId &&
                health.UpdatedAt >= DateTimeOffset.UtcNow.AddSeconds(-35).ToUnixTimeSeconds() ? health : null;
        }
    }
    public bool IsReady => ConnectorHealth?.Ready == true;
    public event Action? Changed;

    public async Task ChooseAsync(string choice)
    {
        if (choice is not ("self" or "dismissed")) throw new ArgumentException("Invalid access choice.");
        await gate.WaitAsync();
        try
        {
            CheckWritableState();
            await HostedPanelState.SaveAsync(State with { Choice = choice });
            AccessActive = false;
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    public async Task<string> EnrollAsync(string panelId, CancellationToken ct = default)
    {
        if (!HostedPanelState.IsPanelId(panelId)) throw new ArgumentException("Use 3–63 lowercase letters and digits separated by single dashes.");
        await gate.WaitAsync(ct);
        try
        {
            CheckWritableState();
            var state = State;
            if (state.Credential is not null && state.PanelId != panelId)
                throw new InvalidOperationException("This installation is already enrolled. Use its existing public name to reconnect.");
            if (state.PanelId == panelId && state.BrowserUrl is not null && state.EnrollmentId is not null)
            {
                await HostedPanelState.SaveAsync(state with { Choice = "hosted" }, ct);
                Status = "Checking hosted access and the secure tunnel";
                Changed?.Invoke();
                return state.BrowserUrl;
            }
            state = new HostedPanelState { Choice = "hosted", PanelId = panelId };
            AccessActive = false;
            // Save proof before contacting Nebula: a crash must not lose the installation key.
            await HostedPanelState.SaveAsync(state, ct);
            using var http = clients.CreateClient("Nebula");
            using var response = await http.PostAsJsonAsync(HostedPanelState.Origin + "/api/v1/enrollments",
                new { panelId, secretHash = HostedPanelState.Hash(state.InstallationSecret) }, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Nebula could not start enrollment. Retry setup in a moment.");
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            string browser = result.GetProperty("browserUrl").GetString()!;
            if (!Uri.TryCreate(browser, UriKind.Absolute, out var url) || url.GetLeftPart(UriPartial.Authority) != HostedPanelState.Origin
                || url.AbsolutePath != "/account" || url.UserInfo.Length != 0)
                throw new InvalidDataException("Nebula returned an invalid enrollment URL.");
            state = state with { EnrollmentId = result.GetProperty("enrollmentId").GetString(), BrowserUrl = browser };
            await HostedPanelState.SaveAsync(state, ct);
            Status = "Waiting for Steam enrollment";
            Changed?.Invoke();
            return browser;
        }
        finally { gate.Release(); }
    }

    private static void CheckWritableState()
    {
        if (File.Exists(HostedPanelState.StatePath) && HostedPanelState.Read() is null)
            throw new InvalidOperationException("Hosted state is unreadable. Restore its private backup before changing enrollment. Local Quasar remains available.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try { await RefreshAsync(stoppingToken); }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                Status = "Hosting service unavailable; local Quasar continues working";
                AccessActive = false;
                logger.LogDebug("Hosted enrollment refresh failed ({Type}).", e.GetType().Name);
                Changed?.Invoke();
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RefreshAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var state = HostedPanelState.Read();
            if (state is not { Choice: "hosted", EnrollmentId: not null })
            {
                AccessActive = false;
                if (File.Exists(HostedPanelState.StatePath) && state is null)
                {
                    Status = "Hosted enrollment state is unreadable; local Quasar continues working";
                    Changed?.Invoke();
                }
                return;
            }
            using var http = clients.CreateClient("Nebula");
            using var request = new HttpRequestMessage(HttpMethod.Get, HostedPanelState.Origin + "/api/v1/enrollments/" + Uri.EscapeDataString(state.EnrollmentId));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.InstallationSecret);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.NotFound)
            {
                await HostedPanelState.SaveAsync(state with { Credential = null, EnrollmentId = null, BrowserUrl = null }, ct);
                Status = "Installation disconnected. Restart hosted setup to relink your panel.";
                AccessActive = false;
                Changed?.Invoke();
                return;
            }
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (result.GetProperty("panelId").GetString() != state.PanelId) throw new InvalidDataException("Enrollment panel mismatch.");
            if (result.GetProperty("expired").GetBoolean())
            {
                await HostedPanelState.SaveAsync(state with { EnrollmentId = null, BrowserUrl = null }, ct);
                Status = "Enrollment link expired. Choose hosted setup again.";
                AccessActive = false;
            }
            else if (result.GetProperty("active").GetBoolean() && result.GetProperty("credential").GetString() is { } credential)
            {
                if (!credential.StartsWith(state.PanelId + ".", StringComparison.Ordinal) || credential.Length != state.PanelId.Length + 65)
                    throw new InvalidDataException("Invalid connector credential.");
                if (state.Credential != credential)
                    await HostedPanelState.SaveAsync(state with { Credential = credential, OwnerSteamId = result.GetProperty("ownerSteamId").GetString(), BrowserUrl = HostedPanelState.Origin + "/account" }, ct);
                AccessActive = true;
                var health = ConnectorHealth;
                Status = health is { Ready: true } ? "Hosted dashboard ready"
                    : health is { TunnelRegistered: true, WorkerReady: false } ? "Tunnel connected; local Quasar is restarting"
                    : "Enrolled; Bootstrap is connecting the hosted tunnel";
            }
            else
            {
                AccessActive = false;
                Status = "Waiting for hosted access activation. Continue on the Nebula account page.";
            }
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
}
