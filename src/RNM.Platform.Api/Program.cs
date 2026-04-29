using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.ApplicationInsights;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Api.Voice;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Inbound;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Booking;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Application.Qualification;
using RNM.Platform.Application.Tenancy;
using RNM.Platform.Infrastructure.Booking;
using RNM.Platform.Infrastructure.Configuration;
using RNM.Platform.Infrastructure.Crm;
using RNM.Platform.Infrastructure.Messaging;
using RNM.Platform.Infrastructure.Observability;
using RNM.Platform.Infrastructure.Secrets;

var runtimeConfiguration = RnmRuntimeConfiguration.FromEnvironment();
runtimeConfiguration.Validate();

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        services.AddSingleton<IConfigurationValidator, ConfigurationValidator>();
        services.AddSingleton<ITenantConfigurationProvider>(serviceProvider =>
        {
            return new JsonTenantConfigurationProvider(
                runtimeConfiguration.ConfigRoot,
                serviceProvider.GetRequiredService<IConfigurationValidator>());
        });
        services.AddSingleton<IVerticalConfigurationProvider>(serviceProvider =>
        {
            return new JsonVerticalConfigurationProvider(
                runtimeConfiguration.ConfigRoot,
                serviceProvider.GetRequiredService<IConfigurationValidator>());
        });
        services.AddSingleton<TenantResolver>();
        services.AddSingleton<TenantIsolationGuard>();
        services.AddSingleton<ISecretProvider>(_ => RuntimeSecretProviderFactory.Create(runtimeConfiguration));
        services.AddSingleton<ApiKeyRequestValidator>();
        services.AddSingleton<TwilioSignatureValidator>();
        services.AddSingleton<VapiWebhookValidator>();
        services.AddSingleton<SafeErrorResponseFactory>();
        services.AddSingleton<SafeHttpResponseWriter>();
        services.AddSingleton<FormUrlEncodedBodyParser>();
        services.AddSingleton<LimitedRequestBodyReader>();
        services.AddSingleton<CorrelationContextFactory>();
        services.AddSingleton(VapiWebhookOptions.FromEnvironment());
        services.AddSingleton<VapiWebhookPayloadParser>();
        services.AddSingleton<VapiWebhookMapper>();
        services.AddSingleton<IInboundBookingWorkflow, InboundBookingWorkflow>();
        services.AddSingleton<ServiceAreaValidator>();
        services.AddSingleton<QualificationService>();
        services.AddSingleton<BookingApplicationService>();
        services.AddSingleton<CrmApplicationService>();
        services.AddSingleton<ConfirmationApplicationService>();
        services.AddHttpClient<IBookingAdapter, GoHighLevelBookingAdapter>(client =>
        {
            client.BaseAddress = RnmRuntimeConfiguration.CreateUriFromEnvironment(
                "RNM_GHL_BASE_URL",
                "https://services.leadconnectorhq.com/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient<ICrmAdapter, GoHighLevelCrmAdapter>(client =>
        {
            client.BaseAddress = RnmRuntimeConfiguration.CreateUriFromEnvironment(
                "RNM_GHL_BASE_URL",
                "https://services.leadconnectorhq.com/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient<ISmsSender, TwilioSmsSender>(client =>
        {
            client.BaseAddress = new Uri("https://api.twilio.com/2010-04-01/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<IEmailSender, AzureCommunicationEmailSender>();
        services.AddSingleton<IEventLogger, ApplicationInsightsEventLogger>();
    })
    .Build();

host.Run();
