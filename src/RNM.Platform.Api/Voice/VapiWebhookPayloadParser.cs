using System.Text.Json;
using RNM.Platform.Contracts.Voice;

namespace RNM.Platform.Api.Voice;

public sealed class VapiWebhookPayloadParser
{
    private readonly VapiWebhookOptions options;

    public VapiWebhookPayloadParser(VapiWebhookOptions? options = null)
    {
        this.options = options ?? new VapiWebhookOptions();
    }

    public VapiWebhookParseResult Parse(string rawBody, DateTimeOffset receivedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return VapiWebhookParseResult.Invalid("empty_payload");
        }

        try
        {
            var documentOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = options.JsonMaxDepth
            };
            using var document = JsonDocument.Parse(rawBody, documentOptions);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return VapiWebhookParseResult.Invalid("invalid_json_shape");
            }

            var message = TryGetObject(root, "message");
            var call = TryGetObject(root, "call") ?? TryGetObject(message, "call");
            var customer = TryGetObject(call, "customer") ?? TryGetObject(root, "customer");
            var toolCall = TryGetFirstToolCall(root, message);

            var rawEventType = FirstNonEmpty(
                TryGetSafeScalarString(root, "event"),
                TryGetSafeScalarString(root, "type"),
                TryGetSafeScalarString(message, "type"),
                TryGetSafeScalarString(root, "eventType")) ?? "unknown";
            var eventKind = MapEventKind(rawEventType, toolCall);

            var envelope = new VapiWebhookEnvelope(
                rawEventType,
                eventKind,
                FirstNonEmpty(
                    TryGetSafeScalarString(root, "callId"),
                    TryGetSafeScalarString(call, "id"),
                    TryGetSafeScalarString(message, "callId")),
                FirstNonEmpty(
                    TryGetSafeScalarString(root, "callerPhoneNumber"),
                    TryGetSafeScalarString(root, "phoneNumber"),
                    TryGetSafeScalarString(customer, "number"),
                    TryGetSafeScalarString(call, "phoneNumber")),
                FirstNonEmpty(
                    TryGetSafeScalarString(root, "transcript"),
                    TryGetSafeScalarString(message, "transcript"),
                    TryGetSafeScalarString(message, "text"),
                    TryGetSafeScalarString(message, "content")),
                FirstNonEmpty(
                    TryGetSafeScalarString(root, "role"),
                    TryGetSafeScalarString(message, "role")),
                toolCall,
                receivedAtUtc);

            return VapiWebhookParseResult.Success(envelope);
        }
        catch (JsonException)
        {
            return VapiWebhookParseResult.Invalid("malformed_json");
        }
    }

    private static VapiWebhookEventKind MapEventKind(
        string rawEventType,
        VapiToolCallRequest? toolCall)
    {
        if (toolCall is not null)
        {
            return VapiWebhookEventKind.ToolCallRequested;
        }

        var normalized = NormalizeEventType(rawEventType);
        return normalized switch
        {
            "callstarted" or "callstart" or "callcreated" => VapiWebhookEventKind.CallStarted,
            "transcript" or "transcriptupdate" or "transcriptupdated" or "message" or "messageupdate" => VapiWebhookEventKind.TranscriptUpdated,
            "callended" or "callend" or "callfinished" => VapiWebhookEventKind.CallEnded,
            "toolcall" or "toolcalls" or "functioncall" or "functioncalls" or "structuredaction" => VapiWebhookEventKind.ToolCallRequested,
            _ => VapiWebhookEventKind.Unsupported
        };
    }

    private static VapiToolCallRequest? TryGetFirstToolCall(
        JsonElement root,
        JsonElement? message)
    {
        var toolCalls = TryGetArray(root, "toolCalls")
            ?? TryGetArray(message, "toolCalls")
            ?? TryGetArray(root, "toolCallList")
            ?? TryGetArray(message, "toolCallList")
            ?? TryGetArray(root, "toolWithToolCallList")
            ?? TryGetArray(message, "toolWithToolCallList")
            ?? TryGetArray(root, "tool_calls")
            ?? TryGetArray(message, "tool_calls");

        if (toolCalls is not null)
        {
            foreach (var item in toolCalls.Value.EnumerateArray())
            {
                return ToToolCall(item);
            }
        }

        var functionCall = TryGetObject(root, "functionCall")
            ?? TryGetObject(message, "functionCall")
            ?? TryGetObject(root, "function_call")
            ?? TryGetObject(message, "function_call");

        return functionCall is null ? null : ToToolCall(functionCall.Value);
    }

    private static VapiToolCallRequest ToToolCall(JsonElement toolCall)
    {
        var function = TryGetObject(toolCall, "function");
        var nestedToolCall = TryGetObject(toolCall, "toolCall");
        return new VapiToolCallRequest(
            FirstNonEmpty(
                TryGetSafeScalarString(toolCall, "id"),
                TryGetSafeScalarString(toolCall, "toolCallId"),
                TryGetSafeScalarString(nestedToolCall, "id"),
                TryGetSafeScalarString(nestedToolCall, "toolCallId"),
                TryGetSafeScalarString(toolCall, "callId")),
            FirstNonEmpty(
                TryGetSafeScalarString(toolCall, "name"),
                TryGetSafeScalarString(nestedToolCall, "name"),
                TryGetSafeScalarString(function, "name")),
            FirstNonEmpty(
                TryGetRawJson(toolCall, "arguments"),
                TryGetRawJson(toolCall, "parameters"),
                TryGetRawJson(nestedToolCall, "arguments"),
                TryGetRawJson(nestedToolCall, "parameters"),
                TryGetRawJson(function, "arguments"),
                TryGetRawJson(function, "parameters")));
    }

    private static JsonElement? TryGetObject(JsonElement? element, string name)
    {
        if (element is null
            || element.Value.ValueKind is not JsonValueKind.Object
            || !element.Value.TryGetProperty(name, out var property)
            || property.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        return property;
    }

    private static JsonElement? TryGetArray(JsonElement? element, string name)
    {
        if (element is null
            || element.Value.ValueKind is not JsonValueKind.Object
            || !element.Value.TryGetProperty(name, out var property)
            || property.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        return property;
    }

    private static string? TryGetSafeScalarString(JsonElement? element, string name)
    {
        if (element is null
            || element.Value.ValueKind is not JsonValueKind.Object
            || !element.Value.TryGetProperty(name, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.GetRawText(),
            _ => null
        };
    }

    private static string? TryGetRawJson(JsonElement? element, string name)
    {
        if (element is null
            || element.Value.ValueKind is not JsonValueKind.Object
            || !element.Value.TryGetProperty(name, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private static string NormalizeEventType(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}
