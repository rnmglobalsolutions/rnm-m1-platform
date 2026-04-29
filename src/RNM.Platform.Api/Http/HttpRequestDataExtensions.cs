using Microsoft.Azure.Functions.Worker.Http;

namespace RNM.Platform.Api.Http;

public static class HttpRequestDataExtensions
{
    public static string? GetHeaderValue(this HttpRequestData request, string headerName)
    {
        return request.Headers.TryGetValues(headerName, out var values)
            ? values.FirstOrDefault()
            : null;
    }

    public static async Task<string> ReadBodyAsStringAsync(this HttpRequestData request)
    {
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);

        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        return body;
    }
}
