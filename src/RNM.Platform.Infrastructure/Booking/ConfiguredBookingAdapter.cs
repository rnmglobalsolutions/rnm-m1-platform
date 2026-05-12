using Microsoft.Extensions.DependencyInjection;
using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Ports.Booking;
using RNM.Platform.Infrastructure.Providers;

namespace RNM.Platform.Infrastructure.Booking;

public sealed class ConfiguredBookingAdapter : IBookingAdapter
{
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly IServiceProvider serviceProvider;

    public ConfiguredBookingAdapter(
        ITenantConfigurationProvider tenantConfigurationProvider,
        IServiceProvider serviceProvider)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.serviceProvider = serviceProvider;
    }

    public async Task<BookingAvailabilityResult> CheckAvailabilityAsync(
        BookingAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedAvailability()
            : await adapter.CheckAvailabilityAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreateBookingResult> CreateBookingAsync(
        CreateBookingRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = await ResolveAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        return adapter is null
            ? UnsupportedBooking()
            : await adapter.CreateBookingAsync(NormalizeRequestForProvider(request, adapter), cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<IBookingProviderAdapter?> ResolveAsync(string tenantId, CancellationToken cancellationToken)
    {
        var tenantConfiguration = await tenantConfigurationProvider
            .GetTenantConfigurationAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        var providerName = tenantConfiguration.Providers.BookingProvider.Trim();
        return serviceProvider.GetServices<IBookingProviderAdapter>()
            .FirstOrDefault(adapter => adapter.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase));
    }

    private static CreateBookingRequest NormalizeRequestForProvider(
        CreateBookingRequest request,
        IBookingProviderAdapter adapter)
    {
        return adapter.ProviderName.Equals(ProviderNames.GoHighLevelCalendar, StringComparison.OrdinalIgnoreCase)
            ? request
            : request with { ProviderContactId = null };
    }

    private static BookingAvailabilityResult UnsupportedAvailability() =>
        new(false, [], BookingFailureReason.AdapterFailure, "Booking provider is not supported.")
        {
            Succeeded = false
        };

    private static CreateBookingResult UnsupportedBooking() =>
        new(false, null, BookingFailureReason.AdapterFailure, "Booking provider is not supported.");
}

public interface IBookingProviderAdapter : IBookingAdapter, IProviderAdapter;
