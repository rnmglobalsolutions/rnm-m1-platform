using Xunit;

namespace RNM.Platform.UnitTests;

/// <summary>
/// Tests that set the process-wide SENDGRID_API_KEY environment variable must not run in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SendGridEnvironmentCollection
{
    public const string Name = "SendGrid environment";
}
