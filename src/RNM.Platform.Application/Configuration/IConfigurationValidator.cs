using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Configuration;

public interface IConfigurationValidator
{
    ConfigurationValidationResult ValidateTenant(TenantConfiguration tenantConfiguration);

    ConfigurationValidationResult ValidateVertical(VerticalConfiguration verticalConfiguration);
}
