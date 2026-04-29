namespace RNM.Platform.Domain.Calls;

public enum CallOutcome
{
    Unknown = 0,
    Booked = 1,
    NotBooked = 2,
    Escalated = 3,
    OutOfServiceArea = 4,
    NoAvailability = 5,
    Abandoned = 6
}
