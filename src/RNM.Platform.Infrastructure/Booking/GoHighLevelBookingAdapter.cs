using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Ports.Booking;
using RNM.Platform.Infrastructure.GoHighLevel;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Infrastructure.Booking;

public sealed class GoHighLevelBookingAdapter : IBookingAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ISecretProvider secretProvider;
    private readonly HttpClient httpClient;

    public GoHighLevelBookingAdapter(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ISecretProvider secretProvider,
        HttpClient httpClient)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.secretProvider = secretProvider;
        this.httpClient = httpClient;
    }

    public async Task<BookingAvailabilityResult> CheckAvailabilityAsync(
        BookingAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await GetCredentialsAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
            if (credentials is null || string.IsNullOrWhiteSpace(credentials.CalendarId))
            {
                return FailedAvailability("GoHighLevel booking credentials are incomplete.");
            }

            var startsAfter = DateTimeOffset.UtcNow;
            var endsBefore = startsAfter.AddDays(14);
            var path = new StringBuilder();
            path.Append("calendars/");
            path.Append(Uri.EscapeDataString(credentials.CalendarId));
            path.Append("/free-slots?startDate=");
            path.Append(Uri.EscapeDataString(startsAfter.ToUnixTimeMilliseconds().ToString()));
            path.Append("&endDate=");
            path.Append(Uri.EscapeDataString(endsBefore.ToUnixTimeMilliseconds().ToString()));
            path.Append("&timezone=");
            path.Append(Uri.EscapeDataString(request.TimeZone));

            using var message = CreateRequest(HttpMethod.Get, path.ToString(), credentials);
            using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return FailedAvailability("GoHighLevel availability lookup failed.");
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var slots = ParseSlots(responseJson);
            return new BookingAvailabilityResult(slots.Count > 0, slots);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedAvailability("GoHighLevel availability lookup failed.");
        }
    }

    public async Task<CreateBookingResult> CreateBookingAsync(
        CreateBookingRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await GetCredentialsAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
            var providerContactId = request.ProviderContactId
                ?? GetFieldValue(request, "providerContactId")
                ?? GetFieldValue(request, "contactId");
            if (credentials is null
                || string.IsNullOrWhiteSpace(credentials.CalendarId)
                || string.IsNullOrWhiteSpace(providerContactId))
            {
                return FailedBooking("GoHighLevel booking credentials or contact id are incomplete.");
            }

            var payload = new GoHighLevelCreateAppointmentRequestDto(
                credentials.CalendarId,
                providerContactId,
                request.Slot.StartsAt,
                request.Slot.EndsAt,
                $"Inbound {request.ServiceType ?? "service"} booking");

            using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            using var message = CreateRequest(HttpMethod.Post, "calendars/events/appointments", credentials);
            message.Content = content;

            using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return FailedBooking("GoHighLevel booking creation failed.");
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var appointmentId = TryReadString(responseJson, "id")
                ?? TryReadString(responseJson, "appointmentId")
                ?? TryReadNestedString(responseJson, "appointment", "id");

            return string.IsNullOrWhiteSpace(appointmentId)
                ? FailedBooking("GoHighLevel booking response did not include an appointment id.")
                : new CreateBookingResult(true, appointmentId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedBooking("GoHighLevel booking creation failed.");
        }
    }

    private async Task<GoHighLevelCredentials?> GetCredentialsAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        var tenantConfiguration = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);
        var secretValue = await secretProvider
            .GetSecretAsync(tenantConfiguration.SecretNames.BookingApiKey, cancellationToken)
            .ConfigureAwait(false);

        return GoHighLevelCredentials.TryParse(secretValue, out var credentials)
            ? credentials
            : null;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        GoHighLevelCredentials credentials)
    {
        var message = new HttpRequestMessage(method, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        message.Headers.TryAddWithoutValidation("Version", credentials.ApiVersion);
        return message;
    }

    private static IReadOnlyCollection<AvailableSlot> ParseSlots(string responseJson)
    {
        var slots = new List<AvailableSlot>();
        using var document = JsonDocument.Parse(responseJson);
        ReadSlots(document.RootElement, slots);
        return slots;
    }

    private static void ReadSlots(JsonElement element, List<AvailableSlot> slots)
    {
        if (element.ValueKind is JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AddSlot(item, slots);
            }

            return;
        }

        if (element.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        if (element.TryGetProperty("slots", out var directSlots))
        {
            ReadSlots(directSlots, slots);
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Array)
            {
                ReadSlots(property.Value, slots);
            }
        }
    }

    private static void AddSlot(JsonElement element, List<AvailableSlot> slots)
    {
        if (element.ValueKind is JsonValueKind.String
            && DateTimeOffset.TryParse(element.GetString(), out var startsAt))
        {
            slots.Add(new AvailableSlot(null, startsAt, startsAt.AddHours(1)));
            return;
        }

        if (element.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        var startValue = ReadString(element, "startTime") ?? ReadString(element, "startsAt") ?? ReadString(element, "start");
        if (!DateTimeOffset.TryParse(startValue, out startsAt))
        {
            return;
        }

        var endValue = ReadString(element, "endTime") ?? ReadString(element, "endsAt") ?? ReadString(element, "end");
        var endsAt = DateTimeOffset.TryParse(endValue, out var parsedEndsAt)
            ? parsedEndsAt
            : startsAt.AddHours(1);

        slots.Add(new AvailableSlot(
            ReadString(element, "id") ?? ReadString(element, "slotId"),
            startsAt,
            endsAt,
            ReadString(element, "label")));
    }

    private static string? GetFieldValue(CreateBookingRequest request, string fieldName)
    {
        return request.LeadData.Fields.TryGetValue(fieldName, out var value)
            ? value
            : null;
    }

    private static string? TryReadString(string responseJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            return ReadString(document.RootElement, propertyName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadNestedString(
        string responseJson,
        string parentPropertyName,
        string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            return document.RootElement.TryGetProperty(parentPropertyName, out var parent)
                ? ReadString(parent, propertyName)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind is JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static BookingAvailabilityResult FailedAvailability(string message) =>
        new(false, [], BookingFailureReason.AdapterFailure, message)
        {
            Succeeded = false
        };

    private static CreateBookingResult FailedBooking(string message) =>
        new(false, null, BookingFailureReason.AdapterFailure, message);
}

internal sealed record GoHighLevelAvailabilityRequestDto(
    string CalendarId,
    DateTimeOffset StartsAfter,
    DateTimeOffset EndsBefore);

internal sealed record GoHighLevelCreateAppointmentRequestDto(
    string CalendarId,
    string ContactId,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    string Title);
