namespace RNM.Platform.Application.Inbound;

public sealed record InboundCallEventProcessingResult(
    bool Accepted,
    bool Processed,
    string Outcome)
{
    public static InboundCallEventProcessingResult ProcessedResult() =>
        new(Accepted: true, Processed: true, Outcome: "processed");

    public static InboundCallEventProcessingResult IgnoredUnsupported() =>
        new(Accepted: true, Processed: false, Outcome: "ignored_unsupported_event");
}
