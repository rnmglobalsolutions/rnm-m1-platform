using RNM.Platform.Application.Inbound;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Qualification;
using Xunit;

namespace RNM.Platform.UnitTests.Qualification;

public sealed class QualificationServiceTests
{
    [Fact]
    public async Task QualifyAsync_ReturnsQualified_WhenAllRequiredFieldsAndZipArePresent()
    {
        var eventLogger = new RecordingQualificationEventLogger();
        var service = CreateService(eventLogger);
        var request = CreateRequest(
            requiredFields: ["serviceNeed", "propertyType", "serviceAddress", "urgency", "preferredTime"],
            fields: new Dictionary<string, string>
            {
                ["serviceNeed"] = "Repair",
                ["propertyType"] = "Residential",
                ["serviceAddress"] = "123 Main St, Addison, TX 75001",
                ["urgency"] = "Today",
                ["preferredTime"] = "Afternoon"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.True(result.IsQualified);
        Assert.Equal(QualificationResultState.Qualified, result.State);
        Assert.Equal(ServiceAreaDecisionState.InServiceArea, result.ServiceAreaDecision.State);
        Assert.Empty(result.MissingRequiredFields);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.QualificationCompleted));
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.ServiceAreaValidated));
    }

    [Fact]
    public async Task QualifyAsync_ReturnsMissingRequiredFields_WhenRequiredFieldIsMissing()
    {
        var eventLogger = new RecordingQualificationEventLogger();
        var service = CreateService(eventLogger);
        var request = CreateRequest(
            requiredFields: ["serviceNeed", "propertyType"],
            fields: new Dictionary<string, string>
            {
                ["serviceNeed"] = "Repair",
                ["zipCode"] = "75001"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.MissingRequiredFields, result.State);
        Assert.Contains("propertyType", result.MissingRequiredFields);
        Assert.Equal(ServiceAreaDecisionState.InServiceArea, result.ServiceAreaDecision.State);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.QualificationMissingFields));
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.ServiceAreaValidated));
    }

    [Fact]
    public async Task QualifyAsync_TreatsEmailAsOptional_WhenVerticalDoesNotRequireEmail()
    {
        var service = CreateService();
        var request = CreateRequest(
            requiredFields: ["serviceNeed"],
            fields: new Dictionary<string, string>
            {
                ["serviceNeed"] = "Repair",
                ["zipCode"] = "75001"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.Qualified, result.State);
        Assert.DoesNotContain(result.MissingRequiredFields, field =>
            field.Equals("email", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QualifyAsync_ReturnsMissingRequiredFields_WhenEmailIsRequiredByVertical()
    {
        var service = CreateService();
        var request = CreateRequest(
            requiredFields: ["serviceNeed", "email"],
            fields: new Dictionary<string, string>
            {
                ["serviceNeed"] = "Repair",
                ["zipCode"] = "75001"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.MissingRequiredFields, result.State);
        Assert.Contains("email", result.MissingRequiredFields);
    }

    [Fact]
    public async Task QualifyAsync_ReturnsOutOfServiceArea_WhenZipIsOutsideAllowedList()
    {
        var eventLogger = new RecordingQualificationEventLogger();
        var service = CreateService(eventLogger);
        var request = CreateRequest(
            requiredFields: ["serviceNeed"],
            fields: new Dictionary<string, string>
            {
                ["serviceNeed"] = "Repair",
                ["zipCode"] = "99999"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.OutOfServiceArea, result.State);
        Assert.Equal(ServiceAreaDecisionState.OutOfServiceArea, result.ServiceAreaDecision.State);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.QualificationOutOfServiceArea));
    }

    [Fact]
    public async Task QualifyAsync_ReturnsMissingRequiredFieldsWithOutOfAreaDecision_WhenNonZipFieldMissingAndZipIsOutOfArea()
    {
        var service = CreateService();
        var request = CreateRequest(
            requiredFields: ["serviceNeed", "propertyType"],
            fields: new Dictionary<string, string>
            {
                ["serviceNeed"] = "Repair",
                ["zipCode"] = "99999"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.MissingRequiredFields, result.State);
        Assert.Equal(ServiceAreaDecisionState.OutOfServiceArea, result.ServiceAreaDecision.State);
        Assert.Contains("propertyType", result.MissingRequiredFields);
        Assert.DoesNotContain("zipCode", result.MissingRequiredFields);
    }

    [Fact]
    public async Task QualifyAsync_ReturnsMissingRequiredFields_WhenZipOrAddressIsMissing()
    {
        var eventLogger = new RecordingQualificationEventLogger();
        var service = CreateService(eventLogger);
        var request = CreateRequest(
            requiredFields: ["serviceNeed"],
            fields: new Dictionary<string, string>
            {
                ["serviceNeed"] = "Repair"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.MissingRequiredFields, result.State);
        Assert.Contains("zipCode", result.MissingRequiredFields);
        Assert.Equal(ServiceAreaDecisionState.MissingZipCode, result.ServiceAreaDecision.State);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.ServiceAreaValidated));
    }

    [Fact]
    public async Task QualifyAsync_ReturnsInvalidInput_WhenZipIsMalformed()
    {
        var eventLogger = new RecordingQualificationEventLogger();
        var service = CreateService(eventLogger);
        var request = CreateRequest(
            requiredFields: ["serviceNeed"],
            fields: new Dictionary<string, string>
            {
                ["serviceNeed"] = "Repair",
                ["zipCode"] = "75A01"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.InvalidInput, result.State);
        Assert.Equal(ServiceAreaDecisionState.InvalidZipCode, result.ServiceAreaDecision.State);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.QualificationInvalidInput));
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.ServiceAreaValidated));
    }

    [Fact]
    public async Task QualifyAsync_ReturnsInvalidInput_WhenRequiredFieldIsMissingAndZipIsMalformed()
    {
        var service = CreateService();
        var request = CreateRequest(
            requiredFields: ["serviceNeed", "propertyType"],
            fields: new Dictionary<string, string>
            {
                ["serviceNeed"] = "Repair",
                ["zipCode"] = "75A01"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.InvalidInput, result.State);
        Assert.Equal(ServiceAreaDecisionState.InvalidZipCode, result.ServiceAreaDecision.State);
        Assert.Contains("propertyType", result.MissingRequiredFields);
    }

    [Fact]
    public async Task QualifyAsync_ReturnsNeedsEscalation_WhenTenantZipListIsEmpty()
    {
        var service = CreateService();
        var request = CreateRequest(
            requiredFields: ["serviceNeed"],
            allowedZipCodes: [],
            fields: new Dictionary<string, string>
            {
                ["serviceNeed"] = "Repair",
                ["zipCode"] = "75001"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.NeedsEscalation, result.State);
        Assert.Equal(ServiceAreaDecisionState.NeedsEscalation, result.ServiceAreaDecision.State);
    }

    [Fact]
    public async Task QualifyAsync_UsesVerticalRequiredFields()
    {
        var service = CreateService();
        var request = CreateRequest(
            requiredFields: ["customRequiredField"],
            fields: new Dictionary<string, string>
            {
                ["serviceNeed"] = "Repair",
                ["zipCode"] = "75001"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.MissingRequiredFields, result.State);
        Assert.Contains("customRequiredField", result.MissingRequiredFields);
        Assert.DoesNotContain("propertyType", result.MissingRequiredFields);
        Assert.DoesNotContain("urgency", result.MissingRequiredFields);
    }

    [Fact]
    public async Task QualifyAsync_UsesConfiguredServiceAreaFieldAliases()
    {
        var service = CreateService();
        var request = CreateRequest(
            requiredFields: ["serviceNeed"],
            serviceAreaFieldAliases: new ServiceAreaFieldAliases(["postal"], ["location"]),
            fields: new Dictionary<string, string>
            {
                ["serviceNeed"] = "Repair",
                ["postal"] = "75001"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.Qualified, result.State);
        Assert.Equal("75001", result.LeadData.ZipCode);
    }

    [Fact]
    public async Task QualifyAsync_HasNoHvacHardcoding()
    {
        var service = CreateService();
        var request = CreateRequest(
            verticalId: "example-vertical",
            requiredFields: ["intakeThing"],
            fields: new Dictionary<string, string>
            {
                ["intakeThing"] = "Present",
                ["zipCode"] = "75001"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.Qualified, result.State);
        Assert.Contains(result.RequiredFields, field => field.FieldName == "intakeThing" && field.IsPresent);
    }

    [Fact]
    public async Task QualifyAsync_EmitsSafeTelemetryWithoutAddressData()
    {
        var eventLogger = new RecordingQualificationEventLogger();
        var service = CreateService(eventLogger);
        var request = CreateRequest(
            requiredFields: ["serviceAddress"],
            fields: new Dictionary<string, string>
            {
                ["serviceAddress"] = "456 Secret Lane, Addison, TX 75001"
            });

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.Qualified, result.State);
        Assert.All(eventLogger.Events, recordedEvent =>
        {
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("456 Secret Lane", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Keys, key =>
                key.Contains("address", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task QualifyAsync_ExtractsStructuredFieldsFromInboundActionArguments()
    {
        var service = CreateService();
        var inboundEvent = CreateInboundCallEvent(
            actionRequest: new StructuredActionRequest(
                "action-1",
                "capture_fields",
                """
                {
                  "serviceNeed": "Repair",
                  "zipCode": "75001"
                }
                """));
        var request = new QualificationRequest(
            "tenant-a",
            "vertical-a",
            "corr-123",
            inboundEvent,
            ["serviceNeed"],
            ["75001"]);

        var result = await service.QualifyAsync(request, CancellationToken.None);

        Assert.Equal(QualificationResultState.Qualified, result.State);
        Assert.Equal("Repair", result.LeadData.Fields["serviceNeed"]);
    }

    private static QualificationService CreateService(RecordingQualificationEventLogger? eventLogger = null)
    {
        return new QualificationService(
            new ServiceAreaValidator(),
            eventLogger ?? new RecordingQualificationEventLogger());
    }

    private static QualificationRequest CreateRequest(
        IReadOnlyCollection<string> requiredFields,
        IReadOnlyDictionary<string, string> fields,
        string verticalId = "vertical-a",
        IReadOnlyCollection<string>? allowedZipCodes = null,
        ServiceAreaFieldAliases? serviceAreaFieldAliases = null)
    {
        return new QualificationRequest(
            "tenant-a",
            verticalId,
            "corr-123",
            CreateInboundCallEvent(),
            requiredFields,
            allowedZipCodes ?? ["75001", "75002"],
            fields,
            serviceAreaFieldAliases);
    }

    private static InboundCallEvent CreateInboundCallEvent(StructuredActionRequest? actionRequest = null)
    {
        return new InboundCallEvent(
            "tenant-a",
            "vertical-a",
            "corr-123",
            InboundCallEventType.ActionRequested,
            new CallSession("call-123", "+15551234567"),
            "provider",
            "provider-event",
            null,
            null,
            actionRequest,
            DateTimeOffset.UtcNow);
    }

    private static Predicate<RecordedQualificationEvent> EventNamed(string eventName)
    {
        return recordedEvent => recordedEvent.EventName == eventName;
    }

    private sealed class RecordingQualificationEventLogger : IEventLogger
    {
        public List<RecordedQualificationEvent> Events { get; } = [];

        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken)
        {
            Events.Add(new RecordedQualificationEvent(eventName, properties));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedQualificationEvent(
        string EventName,
        IReadOnlyDictionary<string, string> Properties);
}
