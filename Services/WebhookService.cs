using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AntigravityUsageTracker.Services;

public static class WebhookService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task SendAsync(string url, string message)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { content = message }),
                Encoding.UTF8, "application/json");
            using var resp = await Client.PostAsync(uri, content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                ErrorLog.Write("WebhookService.SendAsync", new HttpRequestException("HTTP " + (int)resp.StatusCode));
        }
        catch (Exception ex)
        {
            // Log only the exception type/message; the URL is a secret.
            ErrorLog.Write("WebhookService.SendAsync", ex);
        }
    }
}
