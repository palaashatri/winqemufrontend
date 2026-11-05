using System.Text.Json.Serialization;

namespace WinQemu.Core.Lifecycle;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VmState
{
    Created,
    Stopped,
    Starting,
    Running,
    Paused,
    Suspended,
    ShuttingDown,
    Stopping,
    Crashed,
    Unavailable
}

public sealed record VmStateTransitionEvent(
    string VmId,
    VmState PreviousState,
    VmState CurrentState,
    string Reason,
    DateTimeOffset TimestampUtc);

public sealed class VmStateMachine
{
    private readonly Lock _sync = new();
    private readonly List<VmStateTransitionEvent> _history = [];

    public VmStateMachine(string vmId, VmState initialState = VmState.Stopped)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);
        VmId = vmId;
        State = initialState;
    }

    public string VmId { get; }
    public VmState State { get; private set; }
    public string? LastErrorMessage { get; private set; }

    public event EventHandler<VmStateTransitionEvent>? StateChanged;

    public IReadOnlyList<VmStateTransitionEvent> GetHistory()
    {
        lock (_sync)
        {
            return _history.ToList();
        }
    }

    public bool IsActiveProcessState =>
        State is VmState.Starting or VmState.Running or VmState.Paused or VmState.Suspended or VmState.ShuttingDown or VmState.Stopping;

    public VmStateTransitionEvent TransitionTo(VmState nextState, string reason, string? errorMessage = null)
    {
        VmStateTransitionEvent evt;
        lock (_sync)
        {
            var previous = State;
            if (!CanTransition(previous, nextState))
            {
                throw new InvalidOperationException(
                    $"Invalid VM lifecycle transition for '{VmId}' from {previous} to {nextState} ({reason}).");
            }

            State = nextState;
            if (nextState == VmState.Crashed && !string.IsNullOrWhiteSpace(errorMessage))
            {
                LastErrorMessage = errorMessage;
            }
            else if (nextState is VmState.Starting or VmState.Running or VmState.Stopped)
            {
                LastErrorMessage = errorMessage;
            }

            evt = new VmStateTransitionEvent(VmId, previous, nextState, reason, DateTimeOffset.UtcNow);
            _history.Add(evt);
            if (_history.Count > 200)
            {
                _history.RemoveAt(0);
            }
        }

        StateChanged?.Invoke(this, evt);
        return evt;
    }

    public VmState MapQmpRunState(string qmpStatus)
    {
        if (string.IsNullOrWhiteSpace(qmpStatus))
        {
            return State;
        }

        return qmpStatus.Trim().ToLowerInvariant() switch
        {
            "running" => VmState.Running,
            "paused" or "prelaunch" or "finish-migrate" or "Suspended" => VmState.Paused,
            "suspended" => VmState.Suspended,
            "shutdown" => VmState.ShuttingDown,
            "internal-error" or "io-error" or "guest-panicked" or "watchdog" => VmState.Crashed,
            _ => State
        };
    }

    public static bool CanTransition(VmState from, VmState to)
    {
        if (from == to)
        {
            return true;
        }

        if (to is VmState.Crashed or VmState.Unavailable or VmState.Stopped)
        {
            return true;
        }

        return (from, to) switch
        {
            (VmState.Created, VmState.Stopped) => true,
            (VmState.Created, VmState.Starting) => true,
            (VmState.Stopped, VmState.Starting) => true,
            (VmState.Stopped, VmState.Running) => true, // Reattached process
            (VmState.Crashed, VmState.Starting) => true,
            (VmState.Unavailable, VmState.Stopped) => true,
            (VmState.Unavailable, VmState.Starting) => true,
            (VmState.Starting, VmState.Running) => true,
            (VmState.Starting, VmState.Paused) => true,
            (VmState.Starting, VmState.Stopping) => true,
            (VmState.Running, VmState.Paused) => true,
            (VmState.Running, VmState.Suspended) => true,
            (VmState.Running, VmState.ShuttingDown) => true,
            (VmState.Running, VmState.Stopping) => true,
            (VmState.Paused, VmState.Running) => true,
            (VmState.Paused, VmState.ShuttingDown) => true,
            (VmState.Paused, VmState.Stopping) => true,
            (VmState.Suspended, VmState.Running) => true,
            (VmState.Suspended, VmState.Stopping) => true,
            (VmState.ShuttingDown, VmState.Stopping) => true,
            (VmState.ShuttingDown, VmState.Running) => true,
            _ => false
        };
    }
}
