using RNM.Platform.Application.Booking;

namespace RNM.Platform.Application.Ports.Booking;

public interface IBookingAdapter
{
    Task<BookingAvailabilityResult> CheckAvailabilityAsync(
        BookingAvailabilityRequest request,
        CancellationToken cancellationToken);

    Task<CreateBookingResult> CreateBookingAsync(
        CreateBookingRequest request,
        CancellationToken cancellationToken);
}
