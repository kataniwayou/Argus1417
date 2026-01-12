namespace Argus.Services.LeaderElection;

/// <summary>
/// Service for Kubernetes Lease-based leader election.
/// Only the leader sends NOC HTTP calls; all pods execute the same business logic.
/// </summary>
public interface ILeaderElectionService
{
    /// <summary>
    /// Gets whether this pod is currently the leader.
    /// Thread-safe property that can be checked before NOC HTTP calls.
    /// </summary>
    bool IsLeader { get; }

    /// <summary>
    /// Gets the identity of this pod (typically pod name).
    /// </summary>
    string PodIdentity { get; }

    /// <summary>
    /// Gets the identity of the current leader (if known).
    /// May be null if no leader has been elected yet.
    /// </summary>
    string? CurrentLeaderIdentity { get; }

    /// <summary>
    /// Event raised when leadership status changes.
    /// Raised with true when this pod becomes leader, false when it loses leadership.
    /// </summary>
    event EventHandler<bool>? OnLeadershipChanged;
}

