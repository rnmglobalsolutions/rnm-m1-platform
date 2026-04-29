using System.Text;
using Microsoft.Azure.Functions.Worker.Http;

namespace RNM.Platform.Api.Http;

public sealed class LimitedRequestBodyReader
{
    public async Task<RequestBodyReadResult> ReadAsStringAsync(
        HttpRequestData request,
        int maxBodyBytes,
        CancellationToken cancellationToken)
    {
        if (TryGetContentLength(request, out var contentLength) && contentLength > maxBodyBytes)
        {
            return RequestBodyReadResult.TooLarge();
        }

        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        var buffer = new byte[8192];
        var totalBytesRead = 0;
        using var memoryStream = new MemoryStream();

        while (true)
        {
            var bytesRead = await request.Body
                .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
            if (totalBytesRead > maxBodyBytes)
            {
                return RequestBodyReadResult.TooLarge();
            }

            await memoryStream
                .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        return RequestBodyReadResult.Success(Encoding.UTF8.GetString(memoryStream.ToArray()));
    }

    private static bool TryGetContentLength(HttpRequestData request, out long contentLength)
    {
        contentLength = 0;
        var value = request.GetHeaderValue("Content-Length");
        return !string.IsNullOrWhiteSpace(value)
            && long.TryParse(value, out contentLength)
            && contentLength >= 0;
    }
}

public sealed record RequestBodyReadResult(
    bool IsTooLarge,
    string Body)
{
    public static RequestBodyReadResult Success(string body) => new(IsTooLarge: false, body);

    public static RequestBodyReadResult TooLarge() => new(IsTooLarge: true, string.Empty);
}
