using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Magnetar.Protocol.Discovery;
using Magnetar.Protocol.Runtime;
using Newtonsoft.Json;

namespace Quasar.Agent
{
    public class WebServiceLocator
    {
        public async Task<Uri> EnsureWebServiceAsync(CancellationToken cancellationToken)
        {
            var uri = await TryGetHealthyServiceUriAsync(cancellationToken).ConfigureAwait(false);
            if (uri != null) return uri;

            Log("Quasar supervisor is not reachable; retrying.");
            return null;
        }

        private static async Task<Uri> TryGetHealthyServiceUriAsync(CancellationToken cancellationToken)
        {
            foreach (var baseUri in GetCandidateBaseUris())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await IsHealthyAsync(baseUri, cancellationToken).ConfigureAwait(false))
                    return baseUri;
            }

            return null;
        }

        private static async Task<bool> IsHealthyAsync(Uri baseUri, CancellationToken cancellationToken)
        {
            var healthUri = new Uri(baseUri, "/api/health");
            var request = WebRequest.CreateHttp(healthUri);
            request.Method = "GET";
            request.Timeout = 2000;

            using (cancellationToken.Register(request.Abort))
            {
                try
                {
                    using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
                    return response.StatusCode == HttpStatusCode.OK;
                }
                catch
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);

                    return false;
                }
            }
        }

        private static List<Uri> GetCandidateBaseUris()
        {
            var candidates = new List<Uri>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddCandidate(Environment.GetEnvironmentVariable("QUASAR_BASE_URL"), candidates, seen);
            AddCandidate(Environment.GetEnvironmentVariable("QUASAR_PUBLIC_BASE_URL"), candidates, seen);
            AddCandidate(Environment.GetEnvironmentVariable("MAGNETAR_WEB_BASE_URL"), candidates, seen);

            var manifest = ReadManifest();
            if (manifest != null)
                AddCandidate(manifest.BaseUrl, candidates, seen);

            return candidates;
        }

        private static void AddCandidate(string rawBaseUrl, List<Uri> candidates, HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(rawBaseUrl))
                return;

            if (!Uri.TryCreate(rawBaseUrl.Trim(), UriKind.Absolute, out var uri))
                return;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return;

            var builder = new UriBuilder(uri)
            {
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty,
            };

            var normalized = builder.Uri;
            var key = normalized.AbsoluteUri.TrimEnd('/');
            if (seen.Add(key))
                candidates.Add(normalized);
        }

        private static WebServiceDiscoveryManifest ReadManifest()
        {
            try
            {
                var path = MagnetarPaths.GetWebServiceManifestPath();
                return !File.Exists(path) ? null : JsonConvert.DeserializeObject<WebServiceDiscoveryManifest>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static void Log(string message)
        {
            Console.WriteLine($"[Quasar.Agent] {message}");
        }
    }
}
