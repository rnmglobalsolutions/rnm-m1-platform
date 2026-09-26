namespace RNM.Platform.Application.Crm;

public enum ConsentChannel
{
    Sms = 0,
    Email = 1
}

public sealed record ChannelConsentCapture(
    bool Granted,
    string SourceField,
    string DisclosureText,
    string DisclosureVersion,
    DateTimeOffset CapturedAt,
    string Source);

/// <summary>
/// Single source of truth for per-channel consent status, evidence validation, and CRM attribute layout.
/// </summary>
public static class ChannelConsent
{
    public const int MaxDisclosureTextLength = 2000;

    /// <summary>
    /// Resolves the consent status for one channel.
    /// Records written before per-channel consent existed carry neither channel attribute; they keep the
    /// behavior they had before the split (legacy opt_in covers both channels, legacy opt-out came from SMS STOP).
    /// Once any channel attribute exists the record is per-channel and legacy opt_in is no longer inherited.
    /// </summary>
    public static string ResolveStatus(
        IReadOnlyDictionary<string, string> attributes,
        string? legacyConsentStatus,
        ConsentChannel channel)
    {
        var channelStatus = GetAttribute(attributes, StatusAttributeName(channel));
        if (channelStatus is not null)
        {
            return channelStatus;
        }

        var legacyOptedOut = string.Equals(legacyConsentStatus, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase);
        if (channel is ConsentChannel.Sms && legacyOptedOut)
        {
            return CrmConsentStatuses.OptedOut;
        }

        var isLegacyRecord = GetAttribute(attributes, StatusAttributeName(Other(channel))) is null;
        return isLegacyRecord
            && string.Equals(legacyConsentStatus, CrmConsentStatuses.OptIn, StringComparison.OrdinalIgnoreCase)
                ? CrmConsentStatuses.OptIn
                : CrmConsentStatuses.Unknown;
    }

    public static bool HasValidEvidence(ChannelConsentCapture capture, DateTimeOffset utcNow) =>
        !string.IsNullOrWhiteSpace(capture.SourceField)
        && !string.IsNullOrWhiteSpace(capture.DisclosureText)
        && !string.IsNullOrWhiteSpace(capture.DisclosureVersion)
        && !string.IsNullOrWhiteSpace(capture.Source)
        && capture.CapturedAt <= utcNow.AddMinutes(5);

    /// <summary>
    /// A grant without evidence is recorded as not granted instead of rejecting the lead.
    /// </summary>
    public static ChannelConsentCapture? RequireEvidence(
        ChannelConsentCapture? capture,
        DateTimeOffset utcNow,
        out bool downgraded)
    {
        downgraded = capture is { Granted: true } && !HasValidEvidence(capture, utcNow);
        return downgraded ? capture! with { Granted = false } : capture;
    }

    public static string CaptureStatus(ChannelConsentCapture? capture) =>
        capture?.Granted is true ? CrmConsentStatuses.OptIn : CrmConsentStatuses.Unknown;

    public static void WriteAttributes(
        IDictionary<string, string> attributes,
        ConsentChannel channel,
        string status,
        ChannelConsentCapture? capture)
    {
        var sms = channel is ConsentChannel.Sms;
        attributes[StatusAttributeName(channel)] = status;
        if (capture is null)
        {
            return;
        }

        attributes[sms ? CrmContactAttributeNames.SmsConsentGranted : CrmContactAttributeNames.EmailConsentGranted] = capture.Granted.ToString();
        attributes[sms ? CrmContactAttributeNames.SmsConsentSourceField : CrmContactAttributeNames.EmailConsentSourceField] = capture.SourceField;
        attributes[sms ? CrmContactAttributeNames.SmsConsentDisclosureText : CrmContactAttributeNames.EmailConsentDisclosureText] = Truncate(capture.DisclosureText);
        attributes[sms ? CrmContactAttributeNames.SmsConsentTextVersion : CrmContactAttributeNames.EmailConsentTextVersion] = capture.DisclosureVersion;
        attributes[sms ? CrmContactAttributeNames.SmsConsentCapturedAt : CrmContactAttributeNames.EmailConsentCapturedAt] = capture.CapturedAt.ToUniversalTime().ToString("O");
        attributes[sms ? CrmContactAttributeNames.SmsConsentSource : CrmContactAttributeNames.EmailConsentSource] = capture.Source;
    }

    public static string StatusAttributeName(ConsentChannel channel) =>
        channel is ConsentChannel.Sms
            ? CrmContactAttributeNames.SmsConsentStatus
            : CrmContactAttributeNames.EmailConsentStatus;

    private static ConsentChannel Other(ConsentChannel channel) =>
        channel is ConsentChannel.Sms ? ConsentChannel.Email : ConsentChannel.Sms;

    private static string? GetAttribute(IReadOnlyDictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxDisclosureTextLength ? trimmed : trimmed[..MaxDisclosureTextLength];
    }
}
