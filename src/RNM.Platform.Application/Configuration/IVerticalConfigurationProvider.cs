using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Configuration;

public interface IVerticalConfigurationProvider
{
    Task<VerticalConfiguration> GetVerticalConfigurationAsync(
        string verticalId,
        CancellationToken cancellationToken);
}
