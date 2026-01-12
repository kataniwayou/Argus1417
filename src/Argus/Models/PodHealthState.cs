namespace Argus.Models;

/// <summary>
/// Health state for a single monitored pod (Prometheus or KSM)
/// </summary>
public class PodHealthState
{
    /// <summary>Name of the pod being monitored</summary>
    public string PodName { get; set; } = string.Empty;
    
    /// <summary>Whether the pod exists and phase is Running</summary>
    public bool? PodRunning { get; set; }
    
    /// <summary>Pod phase from K8s (Running, Pending, etc.)</summary>
    public string? PodPhase { get; set; }
    
    /// <summary>Whether the container is ready</summary>
    public bool? ContainerReady { get; set; }
    
    /// <summary>Container state (running, waiting, terminated)</summary>
    public ContainerState ContainerState { get; set; } = ContainerState.Unknown;
    
    /// <summary>Current cumulative restart count from K8s</summary>
    public int RestartCount { get; set; }
    
    /// <summary>Sliding window of restart counts for rate calculation</summary>
    public List<int> RestartWindow { get; set; } = [];
    
    /// <summary>Calculated restarts within the sliding window</summary>
    public int RestartsInWindow { get; set; }
    
    /// <summary>Pod deletion timestamp (null if not terminating)</summary>
    public DateTime? DeletionTimestamp { get; set; }
    
    /// <summary>Overall status based on 6-step health check</summary>
    public PodStatus Status { get; set; } = PodStatus.Unknown;
    
    /// <summary>Reason for current status</summary>
    public string StatusReason { get; set; } = string.Empty;
    
    /// <summary>Timestamp when this state was captured</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Results of individual health checks
    /// </summary>
    public HealthCheckResults HealthChecks { get; set; } = new();
}

/// <summary>
/// Results of the 6-step health check
/// </summary>
public class HealthCheckResults
{
    /// <summary>Step 1: Pod exists</summary>
    public bool PodExists { get; set; }
    
    /// <summary>Step 2: Pod phase is Running</summary>
    public bool PodPhaseRunning { get; set; }
    
    /// <summary>Step 3: Pod is not terminating</summary>
    public bool NotTerminating { get; set; }
    
    /// <summary>Step 4: Container is ready</summary>
    public bool ContainerReady { get; set; }
    
    /// <summary>Step 5: Container state is running</summary>
    public bool ContainerRunning { get; set; }
    
    /// <summary>Step 6: Restart rate is stable</summary>
    public bool RestartStable { get; set; }
    
    /// <summary>All 6 checks passed</summary>
    public bool AllPassed => PodExists && PodPhaseRunning && NotTerminating 
                             && ContainerReady && ContainerRunning && RestartStable;
}

