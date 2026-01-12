namespace Argus.Configuration;

/// <summary>
/// Configuration for the Central Timer Service - the system tick engine.
/// Provides periodic callbacks for various Argus subsystems.
/// Note: TickIntervalSeconds is hardcoded to 1 second in CentralTimerService.
/// </summary>
public class CentralTimerConfiguration
{
    // No configurable properties - tick interval is hardcoded to 1 second
}

