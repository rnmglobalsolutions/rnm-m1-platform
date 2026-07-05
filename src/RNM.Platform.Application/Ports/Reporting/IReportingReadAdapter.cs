using RNM.Platform.Application.Reporting;

namespace RNM.Platform.Application.Ports.Reporting;

public interface IReportingReadAdapter
{
    Task<ReportingDataSet> GetPilotReportingDataAsync(
        PilotReportRequest request,
        CancellationToken cancellationToken);
}
