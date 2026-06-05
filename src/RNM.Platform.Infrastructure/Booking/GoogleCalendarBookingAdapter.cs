using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Infrastructure.Configuration;
using RNM.Platform.Infrastructure.Providers;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.Infrastructure.Booking;

public sealed class GoogleCalendarBookingAdapter : IBookingProviderAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultBusinessStart = new(9, 0, 0);
    private static readonly TimeSpan DefaultBusinessEnd = new(17, 0, 0);
    private static readonly TimeSpan DefaultAppointmentDuration = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan DefaultSlotStep = TimeSpan.FromMinutes(30);
    private static readonly string[] NonUrgentSignals =
    [
        "not urgent",
        "not_urgent",
        "non urgent",
        "non-urgent",
        "non_urgent",
        "nonurgent",
        "routine",
        "maintenance",
        "quote",
        "estimate"
    ];

    private static readonly Regex PreferredTimePattern = new(
        @"(?<!\d)(?<hour>2[0-3]|1\d|0?\d)(?::(?<minute>[0-5]\d))?\s*(?<period>a\.?m\.?|p\.?m\.?)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly ISecretProvider secretProvider;
    private readonly HttpClient httpClient;

    public GoogleCalendarBookingAdapter(
        ITenantConfigurationProvider tenantConfigurationProvider,
        ISecretProvider secretProvider,
        HttpClient httpClient)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.secretProvider = secretProvider;
        this.httpClient = httpClient;
    }

    public string ProviderName => ProviderNames.GoogleCalendar;

    public async Task<BookingAvailabilityResult> CheckAvailabilityAsync(
        BookingAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await GetCredentialsAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
            if (credentials is null || string.IsNullOrWhiteSpace(credentials.CalendarId))
            {
                return FailedAvailability("Google Calendar credentials are incomplete.");
            }

            var accessToken = await GetAccessTokenAsync(credentials, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return FailedAvailability("Google Calendar access token is unavailable.");
            }

            var startsAt = request.SelectedSlot?.StartsAt ?? DateTimeOffset.UtcNow.AddHours(2);
            var endsAt = request.SelectedSlot?.EndsAt ?? startsAt.AddDays(credentials.LookAheadDays);
            if (endsAt <= startsAt)
            {
                return new BookingAvailabilityResult(false, []);
            }

            var busyTimes = await GetBusyTimesAsync(credentials.CalendarId, accessToken, startsAt, endsAt, cancellationToken)
                .ConfigureAwait(false);
            if (busyTimes is null)
            {
                return FailedAvailability("Google Calendar availability lookup failed.");
            }

            var slots = GenerateSlots(
                busyTimes,
                startsAt,
                endsAt,
                request.TimeZone,
                request.SelectedSlot is null ? request.PreferredWindow : null,
                credentials.BusinessStart,
                credentials.BusinessEnd,
                credentials.UrgentBusinessStart,
                credentials.UrgentBusinessEnd,
                credentials.AppointmentDuration,
                credentials.SlotStep,
                credentials.IncludeWeekends,
                credentials.IncludeWeekendsForUrgent,
                request.Urgency);

            return new BookingAvailabilityResult(slots.Count > 0, slots);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedAvailability("Google Calendar availability lookup failed.");
        }
    }

    public async Task<CreateBookingResult> CreateBookingAsync(
        CreateBookingRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await GetCredentialsAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
            if (credentials is null || string.IsNullOrWhiteSpace(credentials.CalendarId))
            {
                return FailedBooking("Google Calendar credentials are incomplete.");
            }

            var accessToken = await GetAccessTokenAsync(credentials, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return FailedBooking("Google Calendar access token is unavailable.");
            }

            var busyTimes = await GetBusyTimesAsync(
                    credentials.CalendarId,
                    accessToken,
                    request.Slot.StartsAt,
                    request.Slot.EndsAt,
                    cancellationToken)
                .ConfigureAwait(false);
            if (busyTimes is null)
            {
                return FailedBooking("Google Calendar availability lookup failed.");
            }

            if (OverlapsAny(request.Slot.StartsAt, request.Slot.EndsAt, busyTimes))
            {
                return new CreateBookingResult(
                    false,
                    null,
                    BookingFailureReason.SlotUnavailable,
                    "Google Calendar slot is no longer available.");
            }

            var payload = CreateEventPayload(request, credentials.TimeZone);
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                $"calendars/{Uri.EscapeDataString(credentials.CalendarId)}/events?sendUpdates=none");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            message.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return FailedBooking("Google Calendar event creation failed.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var eventId = TryReadString(json, "id");
            return string.IsNullOrWhiteSpace(eventId)
                ? FailedBooking("Google Calendar event response did not include an event id.")
                : new CreateBookingResult(true, eventId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FailedBooking("Google Calendar event creation failed.");
        }
    }

    private async Task<GoogleCalendarCredentials?> GetCredentialsAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        var tenantConfiguration = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);
        var secretName = tenantConfiguration.GetBookingCredentialsSecretName();
        if (string.IsNullOrWhiteSpace(secretName))
        {
            return null;
        }

        var secretValue = await secretProvider.GetSecretAsync(secretName, cancellationToken).ConfigureAwait(false);
        return GoogleCalendarCredentials.TryParse(secretValue, tenantConfiguration.TimeZone, out var credentials)
            ? credentials
            : null;
    }

    private async Task<string?> GetAccessTokenAsync(
        GoogleCalendarCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(credentials.AccessToken))
        {
            return credentials.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(credentials.RefreshToken)
            || string.IsNullOrWhiteSpace(credentials.ClientId)
            || string.IsNullOrWhiteSpace(credentials.ClientSecret))
        {
            return null;
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = credentials.ClientId,
            ["client_secret"] = credentials.ClientSecret,
            ["refresh_token"] = credentials.RefreshToken,
            ["grant_type"] = "refresh_token"
        };
        using var content = new FormUrlEncodedContent(form);
        using var response = await httpClient.PostAsync(credentials.TokenUri, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return TryReadString(json, "access_token");
    }

    private async Task<IReadOnlyCollection<BusyTime>?> GetBusyTimesAsync(
        string calendarId,
        string accessToken,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            timeMin = startsAt.UtcDateTime,
            timeMax = endsAt.UtcDateTime,
            items = new[] { new { id = calendarId } }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "freeBusy");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        message.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseBusyTimes(json, calendarId);
    }

    private static IReadOnlyCollection<AvailableSlot> GenerateSlots(
        IReadOnlyCollection<BusyTime> busyTimes,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        string timeZone,
        string? preferredWindow,
        TimeSpan businessStart,
        TimeSpan businessEnd,
        TimeSpan urgentBusinessStart,
        TimeSpan urgentBusinessEnd,
        TimeSpan appointmentDuration,
        TimeSpan slotStep,
        bool includeWeekends,
        bool includeWeekendsForUrgent,
        string? urgency)
    {
        var isUrgent = IsUrgent(urgency);
        var effectiveBusinessStart = isUrgent ? urgentBusinessStart : businessStart;
        var effectiveBusinessEnd = isUrgent ? urgentBusinessEnd : businessEnd;
        var effectiveIncludeWeekends = isUrgent
            ? includeWeekends || includeWeekendsForUrgent
            : includeWeekends;

        if (effectiveBusinessEnd <= effectiveBusinessStart || appointmentDuration <= TimeSpan.Zero || slotStep <= TimeSpan.Zero)
        {
            return [];
        }

        var slots = new List<AvailableSlot>();
        var zone = ResolveTimeZone(timeZone);
        var localNow = TimeZoneInfo.ConvertTime(startsAt, zone);
        var localEnd = TimeZoneInfo.ConvertTime(endsAt, zone);
        var day = localNow.Date;

        while (day <= localEnd.Date)
        {
            if (effectiveIncludeWeekends || (day.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday))
            {
                var localSlotStart = day.Add(effectiveBusinessStart);
                var localBusinessEnd = day.Add(effectiveBusinessEnd);
                while (localSlotStart.Add(appointmentDuration) <= localBusinessEnd)
                {
                    var localSlotEnd = localSlotStart.Add(appointmentDuration);
                    var slotStart = new DateTimeOffset(localSlotStart, zone.GetUtcOffset(localSlotStart)).ToUniversalTime();
                    var slotEnd = new DateTimeOffset(localSlotEnd, zone.GetUtcOffset(localSlotEnd)).ToUniversalTime();
                    if (slotStart >= startsAt
                        && slotEnd <= endsAt
                        && MatchesPreferredWindow(localSlotStart, localNow.Date, preferredWindow)
                        && !OverlapsAny(slotStart, slotEnd, busyTimes))
                    {
                        slots.Add(new AvailableSlot(
                            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{slotStart:O}|{slotEnd:O}")),
                            slotStart,
                            slotEnd,
                            TimeZoneInfo.ConvertTime(slotStart, zone).ToString("ddd MMM d h:mm tt")));
                    }

                    localSlotStart = localSlotStart.Add(slotStep);
                }
            }

            day = day.AddDays(1);
        }

        return slots.Take(24).ToArray();
    }

    private static object CreateEventPayload(CreateBookingRequest request, string timeZone)
    {
        var zone = ResolveTimeZone(timeZone);
        var localStartsAt = TimeZoneInfo.ConvertTime(request.Slot.StartsAt, zone);
        var localEndsAt = TimeZoneInfo.ConvertTime(request.Slot.EndsAt, zone);
        var submittedName = GetFieldValue(request, "name");
        var email = GetFieldValue(request, "email");
        var serviceAddress = BuildServiceAddress(GetFieldValue(request, "serviceAddress"), request.LeadData.ZipCode);
        var propertyType = GetFieldValue(request, "propertyType");
        var urgency = GetFieldValue(request, "urgency");
        var phone = request.LeadData.CallerPhoneNumber;
        var serviceType = request.ServiceType ?? "Service";
        var description = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(submittedName))
        {
            description.AppendLine($"Customer: {submittedName}");
        }

        description.AppendLine($"Service: {serviceType}");
        if (!string.IsNullOrWhiteSpace(propertyType))
        {
            description.AppendLine($"Property type: {propertyType}");
        }

        if (!string.IsNullOrWhiteSpace(serviceAddress))
        {
            description.AppendLine($"Service address: {serviceAddress}");
        }

        if (!string.IsNullOrWhiteSpace(request.LeadData.ZipCode))
        {
            description.AppendLine($"ZIP code: {request.LeadData.ZipCode}");
        }

        if (!string.IsNullOrWhiteSpace(urgency))
        {
            description.AppendLine($"Urgency: {urgency}");
        }

        if (!string.IsNullOrWhiteSpace(request.PreferredWindow))
        {
            description.AppendLine($"Preferred time: {request.PreferredWindow}");
        }

        description.AppendLine($"Correlation ID: {request.CorrelationId}");
        if (!string.IsNullOrWhiteSpace(phone))
        {
            description.AppendLine($"Phone: {phone}");
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            description.AppendLine($"Email: {email}");
        }

        var attendees = string.IsNullOrWhiteSpace(email)
            ? []
            : new[] { new { email } };

        return new
        {
            summary = BuildSummary(serviceType, submittedName),
            description = description.ToString(),
            location = serviceAddress,
            start = new { dateTime = localStartsAt, timeZone },
            end = new { dateTime = localEndsAt, timeZone },
            attendees,
            extendedProperties = new
            {
                privateData = new
                {
                    tenantId = request.TenantId,
                    verticalId = request.VerticalId,
                    correlationId = request.CorrelationId,
                    customerNamePresent = !string.IsNullOrWhiteSpace(submittedName)
                }
            }
        };
    }

    private static IReadOnlyCollection<BusyTime> ParseBusyTimes(string json, string calendarId)
    {
        var busyTimes = new List<BusyTime>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("calendars", out var calendars)
            || !calendars.TryGetProperty(calendarId, out var calendar)
            || !calendar.TryGetProperty("busy", out var busy)
            || busy.ValueKind is not JsonValueKind.Array)
        {
            return busyTimes;
        }

        foreach (var item in busy.EnumerateArray())
        {
            var start = ReadString(item, "start");
            var end = ReadString(item, "end");
            if (DateTimeOffset.TryParse(start, out var parsedStart)
                && DateTimeOffset.TryParse(end, out var parsedEnd))
            {
                busyTimes.Add(new BusyTime(parsedStart, parsedEnd));
            }
        }

        return busyTimes;
    }

    private static bool OverlapsAny(
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        IReadOnlyCollection<BusyTime> busyTimes)
    {
        return busyTimes.Any(busy => startsAt < busy.EndsAt && endsAt > busy.StartsAt);
    }

    private static bool MatchesPreferredWindow(
        DateTime localSlotStart,
        DateTime localToday,
        string? preferredWindow)
    {
        if (string.IsNullOrWhiteSpace(preferredWindow))
        {
            return true;
        }

        var normalized = preferredWindow.Trim();
        if (!MatchesPreferredDay(localSlotStart.Date, localToday, normalized))
        {
            return false;
        }

        var explicitWindow = TryParsePreferredTimeWindow(normalized);
        if (explicitWindow is not null)
        {
            return explicitWindow.Matches(localSlotStart.TimeOfDay);
        }

        if (normalized.Contains("morning", StringComparison.OrdinalIgnoreCase))
        {
            return localSlotStart.TimeOfDay < TimeSpan.FromHours(12);
        }

        if (normalized.Contains("afternoon", StringComparison.OrdinalIgnoreCase))
        {
            return localSlotStart.TimeOfDay >= TimeSpan.FromHours(12)
                && localSlotStart.TimeOfDay < TimeSpan.FromHours(17);
        }

        if (normalized.Contains("evening", StringComparison.OrdinalIgnoreCase))
        {
            return localSlotStart.TimeOfDay >= TimeSpan.FromHours(17);
        }

        return true;
    }

    private static bool IsUrgent(string? urgency)
    {
        if (IsExplicitlyNotUrgent(urgency))
        {
            return false;
        }

        return HasUrgentSignal(urgency);
    }

    private static bool HasUrgentSignal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("urgent", StringComparison.OrdinalIgnoreCase)
            || value.Contains("emergency", StringComparison.OrdinalIgnoreCase)
            || value.Contains("asap", StringComparison.OrdinalIgnoreCase)
            || value.Contains("same day", StringComparison.OrdinalIgnoreCase)
            || value.Contains("same-day", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplicitlyNotUrgent(string? urgency)
    {
        if (string.IsNullOrWhiteSpace(urgency))
        {
            return false;
        }

        return NonUrgentSignals.Any(value => urgency.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesPreferredDay(
        DateTime localSlotDate,
        DateTime localToday,
        string preferredWindow)
    {
        if (preferredWindow.Contains("today", StringComparison.OrdinalIgnoreCase))
        {
            return localSlotDate == localToday;
        }

        if (preferredWindow.Contains("tomorrow", StringComparison.OrdinalIgnoreCase))
        {
            return localSlotDate == localToday.AddDays(1);
        }

        if (preferredWindow.Contains("next week", StringComparison.OrdinalIgnoreCase))
        {
            var nextWeekStart = localToday.AddDays(7);
            var nextWeekEnd = nextWeekStart.AddDays(7);
            return localSlotDate >= nextWeekStart && localSlotDate < nextWeekEnd;
        }

        var weekday = TryParseWeekday(preferredWindow);
        if (weekday is not null)
        {
            return localSlotDate == NextOccurrence(localToday, weekday.Value);
        }

        return true;
    }

    private static DayOfWeek? TryParseWeekday(string value)
    {
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            if (value.Contains(day.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return day;
            }
        }

        return null;
    }

    private static DateTime NextOccurrence(DateTime localToday, DayOfWeek day)
    {
        var daysUntilTarget = ((int)day - (int)localToday.DayOfWeek + 7) % 7;
        return localToday.AddDays(daysUntilTarget == 0 ? 7 : daysUntilTarget);
    }

    private static PreferredTimeWindow? TryParsePreferredTimeWindow(string preferredWindow)
    {
        var matches = PreferredTimePattern
            .Matches(preferredWindow)
            .Where(match => match.Success)
            .Select(match => PreferredTimeToken.FromMatch(match))
            .Where(token => token is not null)
            .Cast<PreferredTimeToken>()
            .ToArray();

        if (matches.Length >= 2)
        {
            var start = InferPeriod(matches[0], matches[1], isStart: true);
            var end = InferPeriod(matches[1], start, isStart: false);
            var startTime = ToTimeSpan(start);
            var endTime = ToTimeSpan(end);

            return endTime > startTime
                ? new PreferredTimeWindow(startTime, endTime)
                : null;
        }

        if (matches.Length == 1 && matches[0].Period is not null)
        {
            var time = ToTimeSpan(matches[0]);
            if (preferredWindow.Contains("after", StringComparison.OrdinalIgnoreCase))
            {
                return new PreferredTimeWindow(time, null);
            }

            if (preferredWindow.Contains("before", StringComparison.OrdinalIgnoreCase))
            {
                return new PreferredTimeWindow(null, time);
            }

            return new PreferredTimeWindow(time, time.Add(TimeSpan.FromHours(1)));
        }

        return null;
    }

    private static PreferredTimeToken InferPeriod(
        PreferredTimeToken token,
        PreferredTimeToken other,
        bool isStart)
    {
        if (token.Period is not null || token.Hour > 12)
        {
            return token;
        }

        if (other.Period is null)
        {
            return token;
        }

        var inferredPeriod = other.Period;
        if (isStart && other.Period is TimePeriod.Pm && token.Hour > other.Hour)
        {
            inferredPeriod = TimePeriod.Am;
        }

        return token with { Period = inferredPeriod };
    }

    private static TimeSpan ToTimeSpan(PreferredTimeToken token)
    {
        var hour = token.Hour;
        if (token.Period is TimePeriod.Pm && hour < 12)
        {
            hour += 12;
        }
        else if (token.Period is TimePeriod.Am && hour is 12)
        {
            hour = 0;
        }

        return new TimeSpan(hour, token.Minute, 0);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static string? GetFieldValue(CreateBookingRequest request, string fieldName)
    {
        return request.LeadData.Fields.TryGetValue(fieldName, out var value)
            ? value
            : null;
    }

    private static string? BuildServiceAddress(string? serviceAddress, string? zipCode)
    {
        if (string.IsNullOrWhiteSpace(serviceAddress))
        {
            return null;
        }

        var normalizedAddress = serviceAddress.Trim();
        var normalizedZipCode = zipCode?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedZipCode)
            || normalizedAddress.Contains(normalizedZipCode, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedAddress;
        }

        return $"{normalizedAddress} {normalizedZipCode}";
    }

    private static string BuildSummary(string serviceType, string? submittedName)
    {
        return string.IsNullOrWhiteSpace(submittedName)
            ? $"RNM booking - {serviceType}"
            : $"RNM booking - {submittedName.Trim()} - {serviceType}";
    }

    private static string? TryReadString(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadString(document.RootElement, propertyName);
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

    private sealed record PreferredTimeWindow(TimeSpan? StartsAt, TimeSpan? EndsAt)
    {
        public bool Matches(TimeSpan slotStart)
        {
            return (StartsAt is null || slotStart >= StartsAt)
                && (EndsAt is null || slotStart < EndsAt);
        }
    }

    private sealed record PreferredTimeToken(int Hour, int Minute, TimePeriod? Period)
    {
        public static PreferredTimeToken? FromMatch(Match match)
        {
            if (!int.TryParse(match.Groups["hour"].Value, out var hour)
                || hour > 23
                || !TryParseMinute(match, out var minute))
            {
                return null;
            }

            var periodValue = match.Groups["period"].Value;
            var period = periodValue.StartsWith("a", StringComparison.OrdinalIgnoreCase)
                ? TimePeriod.Am
                : periodValue.StartsWith("p", StringComparison.OrdinalIgnoreCase)
                    ? TimePeriod.Pm
                    : (TimePeriod?)null;

            return new PreferredTimeToken(hour, minute, period);
        }

        private static bool TryParseMinute(Match match, out int minute)
        {
            minute = 0;
            var minuteValue = match.Groups["minute"].Value;
            return string.IsNullOrWhiteSpace(minuteValue)
                || int.TryParse(minuteValue, out minute);
        }
    }

    private enum TimePeriod
    {
        Am,
        Pm
    }

    private sealed record BusyTime(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

    private sealed record GoogleCalendarCredentials(
        string CalendarId,
        string? AccessToken,
        string? RefreshToken,
        string? ClientId,
        string? ClientSecret,
        string TokenUri,
        string TimeZone,
        TimeSpan BusinessStart,
        TimeSpan BusinessEnd,
        TimeSpan UrgentBusinessStart,
        TimeSpan UrgentBusinessEnd,
        TimeSpan AppointmentDuration,
        TimeSpan SlotStep,
        int LookAheadDays,
        bool IncludeWeekends,
        bool IncludeWeekendsForUrgent)
    {
        public static bool TryParse(
            string secretValue,
            string tenantTimeZone,
            out GoogleCalendarCredentials? credentials)
        {
            credentials = null;
            if (string.IsNullOrWhiteSpace(secretValue))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(secretValue);
                var root = document.RootElement;
                var calendarId = ReadString(root, "calendarId");
                if (string.IsNullOrWhiteSpace(calendarId))
                {
                    return false;
                }

                var businessStart = ReadTimeSpan(root, "businessStart", DefaultBusinessStart);
                var businessEnd = ReadTimeSpan(root, "businessEnd", DefaultBusinessEnd);

                credentials = new GoogleCalendarCredentials(
                    calendarId,
                    ReadString(root, "accessToken"),
                    ReadString(root, "refreshToken"),
                    ReadString(root, "clientId"),
                    ReadString(root, "clientSecret"),
                    ReadString(root, "tokenUri") ?? "https://oauth2.googleapis.com/token",
                    ReadString(root, "timeZone") ?? tenantTimeZone,
                    businessStart,
                    businessEnd,
                    ReadTimeSpan(root, "urgentBusinessStart", businessStart),
                    ReadTimeSpan(root, "urgentBusinessEnd", businessEnd),
                    ReadMinutes(root, "appointmentMinutes", DefaultAppointmentDuration, maxMinutes: 480),
                    ReadMinutes(root, "slotStepMinutes", DefaultSlotStep, maxMinutes: 240),
                    ReadInt(root, "lookAheadDays", 14, maxValue: 60),
                    ReadBool(root, "includeWeekends", false),
                    ReadBool(root, "includeWeekendsForUrgent", false));
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static TimeSpan ReadTimeSpan(JsonElement root, string propertyName, TimeSpan defaultValue)
        {
            var value = ReadString(root, propertyName);
            return TimeSpan.TryParse(value, out var parsed) ? parsed : defaultValue;
        }

        private static TimeSpan ReadMinutes(
            JsonElement root,
            string propertyName,
            TimeSpan defaultValue,
            int maxMinutes)
        {
            return root.TryGetProperty(propertyName, out var property)
                && property.TryGetInt32(out var minutes)
                && minutes > 0
                && minutes <= maxMinutes
                ? TimeSpan.FromMinutes(minutes)
                : defaultValue;
        }

        private static int ReadInt(JsonElement root, string propertyName, int defaultValue, int maxValue)
        {
            return root.TryGetProperty(propertyName, out var property)
                && property.TryGetInt32(out var value)
                && value > 0
                && value <= maxValue
                ? value
                : defaultValue;
        }

        private static bool ReadBool(JsonElement root, string propertyName, bool defaultValue)
        {
            return root.TryGetProperty(propertyName, out var property)
                && property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : defaultValue;
        }
    }
}
