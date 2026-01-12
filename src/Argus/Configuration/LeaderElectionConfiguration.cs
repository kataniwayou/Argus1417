namespace Argus.Configuration;

/// <summary>
/// Configuration for Kubernetes Lease-based leader election.
/// Only the leader sends NOC HTTP calls; all pods execute the same business logic.
/// </summary>
public class LeaderElectionConfiguration
{
    /// <summary>
    /// Name of the Kubernetes Lease resource used for leader election.
    /// Default: "argus-leader"
    /// </summary>
    public string LeaseName { get; set; } = "argus-leader";

    /// <summary>
    /// How long the lease is valid without renewal (in seconds).
    /// If the leader fails to renew within this duration, the lease expires
    /// and another pod can acquire leadership.
    /// Default: 15 seconds
    /// </summary>
    public int LeaseDurationSeconds { get; set; } = 15;

    /// <summary>
    /// How often the leader renews the lease (in seconds).
    /// Must be less than LeaseDurationSeconds to provide a buffer for
    /// network latency and K8s API slowness.
    /// Default: 10 seconds (provides 5s buffer with 15s lease duration)
    /// </summary>
    public int RenewIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// How often followers check the lease and attempt to acquire it (in seconds).
    /// Default: 10 seconds
    /// </summary>
    public int RetryIntervalSeconds { get; set; } = 10;
}

