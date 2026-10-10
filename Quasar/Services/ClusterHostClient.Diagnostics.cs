using System.Net.Http.Headers;
using System.Text.Json;
using CometWorks.Diagnostics;
using Quasar.Models;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Services;

public sealed partial class ClusterHostClient
{
    private static ClusterDefinition DiagnosticsTarget(EnrolledClusterHost host) => new()
    { HostCommandUrl = host.CommandUrl, HostCommandTokenEnvironmentVariable = host.CredentialReference };

    public async Task SetDiagnosticsPolicyAsync(EnrolledClusterHost host, HostContract.HostDiagnosticPolicy policy, CancellationToken token) =>
        _ = await SendAsync<HostContract.HostDiagnosticPolicy>(DiagnosticsTarget(host), HttpMethod.Put,
            "/host/v1/diagnostics/policy", policy, token);

    public async Task<ReportEnvelope[]> GetDiagnosticsReportsAsync(EnrolledClusterHost host, CancellationToken token) =>
        (await SendAsync<ReportEnvelope[]>(DiagnosticsTarget(host), HttpMethod.Get, "/host/v1/diagnostics/reports", null, token)).Data;

    public async Task AcknowledgeDiagnosticsReportAsync(EnrolledClusterHost host, string id, CancellationToken token) =>
        _ = await SendAsync<JsonElement>(DiagnosticsTarget(host), HttpMethod.Delete,
            "/host/v1/diagnostics/reports/" + Uri.EscapeDataString(id), null, token);

    public async Task DownloadDiagnosticsReportAsync(EnrolledClusterHost host, ReportEnvelope envelope, string file, CancellationToken token)
    {
        DiagnosticsProtocol.ValidateEnvelope(envelope);
        using var request = new HttpRequestMessage(HttpMethod.Get, host.CommandUrl + "/host/v1/diagnostics/reports/" + envelope.ReportId);
        TunnelAuthority(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ResolveCredential(host.CredentialReference)
            ?? throw new InvalidOperationException("Host executor credential is unavailable."));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } bytes && bytes != envelope.ArchiveBytes)
            throw new InvalidDataException("Diagnostic archive length mismatch.");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        await DiagnosticsUplinkService.CopyVerifiedAsync(input, file, envelope, deadline.Token);
    }
}
