using System.Text.RegularExpressions;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// Thin wrapper that carries cookies between requests (WebApplicationFactory's default client
/// does not) and scrapes the real, rendered __RequestVerificationToken so tests exercise the
/// genuine antiforgery flow instead of bypassing it.
/// </summary>
public sealed partial class AntiforgeryHttpClient(HttpClient client)
{
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public HttpClient Client { get; } = client;

    public async Task<(HttpResponseMessage Response, string Html)> GetPageAsync(string url, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCookies(request);

        var response = await Client.SendAsync(request, cancellationToken);
        CaptureCookies(response);

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response, html);
    }

    public static string ExtractAntiforgeryToken(string html)
    {
        var match = AntiforgeryTokenPattern().Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException("No __RequestVerificationToken field found in the rendered page.");
        }

        return match.Groups[1].Value;
    }

    public async Task<HttpResponseMessage> PostFormAsync(
        string url,
        string antiforgeryToken,
        IEnumerable<KeyValuePair<string, string>> fields,
        CancellationToken cancellationToken = default)
    {
        var formFields = new List<KeyValuePair<string, string>>(fields)
        {
            new("__RequestVerificationToken", antiforgeryToken)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(formFields)
        };
        ApplyCookies(request);

        var response = await Client.SendAsync(request, cancellationToken);
        CaptureCookies(response);
        return response;
    }

    private void ApplyCookies(HttpRequestMessage request)
    {
        if (_cookies.Count == 0)
        {
            return;
        }

        request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(pair => $"{pair.Key}={pair.Value}")));
    }

    private void CaptureCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            return;
        }

        foreach (var header in setCookieHeaders)
        {
            var nameValue = header.Split(';', 2)[0];
            var separatorIndex = nameValue.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = nameValue[..separatorIndex];
            var value = nameValue[(separatorIndex + 1)..];
            _cookies[name] = value;
        }
    }

    [GeneratedRegex("__RequestVerificationToken\"[^>]*value=\"([^\"]*)\"")]
    private static partial Regex AntiforgeryTokenPattern();
}
