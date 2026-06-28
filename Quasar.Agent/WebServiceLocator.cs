using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Magnetar.Protocol.Discovery;
using Magnetar.Protocol.Runtime;
using Newtonsoft.Json;

namespace Quasar.Agent
{
    public class WebServiceLocator
    {
        public static async Task<Uri> EnsureWebServiceAsync()
        {
            var uri = await TryGetHealthyServiceUriAsync().ConfigureAwait(false);
            if (uri != null) return uri;

            Log("Quasar supervisor is not reachable; retrying.");
            return null;
        }

        private static async Task<Uri> TryGetHealthyServiceUriAsync()
        {
            var manifest = ReadManifest();
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.BaseUrl))
                return null;

            if (!Uri.TryCreate(manifest.BaseUrl, UriKind.Absolute, out var baseUri))
                return null;

            var healthUri = new Uri(baseUri, "/api/health");
            var request = WebRequest.CreateHttp(healthUri);
            request.Method = "GET";
            request.Timeout = 2000;

            try
            {
                using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
                return response.StatusCode == HttpStatusCode.OK ? baseUri : null;
            }
            catch
            {
                return null;
            }
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
