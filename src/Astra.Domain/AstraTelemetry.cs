using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Astra.Domain;

public static class AstraTelemetry
{
    public const string ActivitySourceName = "Astra.LiveOps";

    public const string MeterName = "Astra.LiveOps";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static readonly Meter Meter = new(MeterName);
}
