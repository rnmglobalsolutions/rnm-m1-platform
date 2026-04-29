using System.Net;

namespace RNM.Platform.Api.Http;

public sealed class FormUrlEncodedBodyParser
{
    public IReadOnlyCollection<KeyValuePair<string, string>> Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        return body
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var separatorIndex = part.IndexOf('=', StringComparison.Ordinal);
                var key = separatorIndex >= 0 ? part[..separatorIndex] : part;
                var value = separatorIndex >= 0 ? part[(separatorIndex + 1)..] : string.Empty;

                return new KeyValuePair<string, string>(
                    WebUtility.UrlDecode(key),
                    WebUtility.UrlDecode(value));
            })
            .ToArray();
    }
}
