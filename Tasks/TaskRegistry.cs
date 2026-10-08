using System.Threading.Channels;

namespace LocalDelegationMcp.Tasks;

/// <summary>In-memory store of all task sessions, keyed by id.</summary>
public sealed class TaskRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskSession> _sessions = [];
    private readonly Dictionary<int, Action<TaskSession>> _sessionHandlers = [];
    private readonly Dictionary<Guid, Channel<bool>> _subscribers = [];
    private int _nextId = 1;
    private readonly int _maxActiveTasks;

    public TaskChangeSubscription SubscribeChanges()
    {
        lock (_gate)
        {
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });
            _subscribers[id] = channel;
            return new TaskChangeSubscription(channel.Reader, () => RemoveSubscriber(id));
        }
    }

    private void RemoveSubscriber(Guid id)
    {
        lock (_gate)
        {
            if (_subscribers.Remove(id, out var channel))
                channel.Writer.TryComplete();
        }
    }

    private void NotifySubscribers()
    {
        lock (_gate)
        {
            foreach (var channel in _subscribers.Values)
                channel.Writer.TryWrite(true);
        }
    }

    private void Register(TaskSession session)
    {
        Action<TaskSession> handler = _ => NotifySubscribers();
        session.Changed += handler;
        _sessionHandlers[session.Id] = handler;
        _sessions[session.Id] = session;
        NotifySubscribers();
    }

    public TaskRegistry(int maxActiveTasks = 4)
    {
        if (maxActiveTasks < 1)
            throw new ArgumentOutOfRangeException(nameof(maxActiveTasks), "The active task limit must be at least 1.");
        _maxActiveTasks = maxActiveTasks;
    }

    /// <summary>Creates a brand-new session and registers it. Returns the session (status Running).</summary>
    public TaskSession Create(string description, string completionCriteria)
    {
        lock (_gate)
        {
            EnsureCapacity();
            var id = _nextId++;
            var session = TaskSession.Create(id, description, completionCriteria);
            Register(session);
            return session;
        }
    }

    public bool TryGet(int id, out TaskSession? session)
    {
        lock (_gate)
            return _sessions.TryGetValue(id, out session);
    }

    /// <summary>
    /// Starts a new delegation. If taskId is provided and refers to an existing COMPLETED session, that session's
    /// context (history) is reused; otherwise a fresh session is created.
    /// Returns the session plus whether it was resumed. Throws if taskId refers to a non-completed/unknown session.
    /// </summary>
    public (TaskSession Session, bool Resumed) StartDelegation(string description, string completionCriteria, int? taskId)
    {
        lock (_gate)
        {
            if (taskId is int id && _sessions.TryGetValue(id, out var existing))
            {
                if (existing.Status != DelegationStatus.Completed)
                    throw new InvalidOperationException(
                        $"Task {id} exists but its status is '{existing.Status}'. Only completed tasks can be resumed by delegating with their task_id.");

                EnsureCapacity();
                existing.ResumeWith(description, completionCriteria);
                return (existing, true);
            }

            if (taskId is int missingId)
                throw new InvalidOperationException($"Unknown task_id {missingId}. No such task exists.");

            EnsureCapacity();
            var session = TaskSession.Create(_nextId++, description, completionCriteria);
            Register(session);
            return (session, false);
        }
    }

    private void EnsureCapacity()
    {
        var activeCount = _sessions.Values.Count(s => s.Status is DelegationStatus.Running or DelegationStatus.WaitingClarification);
        if (activeCount >= _maxActiveTasks)
            throw new InvalidOperationException($"Maximum active task limit reached ({_maxActiveTasks}). Wait for a task to finish or stop one before starting another.");
    }

    /// <summary>Non-terminal sessions (running or waiting for clarification), in creation order.</summary>
    public IReadOnlyList<TaskSession> Pending()
    {
        lock (_gate)
            return _sessions.Values.Where(s => s.Status is DelegationStatus.Running or DelegationStatus.WaitingClarification).ToList();
    }

    /// <summary>All sessions (for diagnostics).</summary>
    public IReadOnlyList<TaskSession> All()
    {
        lock (_gate)
            return _sessions.Values.ToList();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var s in _sessions.Values)
            {
                if (_sessionHandlers.TryGetValue(s.Id, out var handler))
                    s.Changed -= handler;
                s.TryStop();
                s.Dispose();
            }
            _sessions.Clear();
            _sessionHandlers.Clear();
            foreach (var channel in _subscribers.Values)
                channel.Writer.TryComplete();
            _subscribers.Clear();
        }
    }
}

public sealed class TaskChangeSubscription : IDisposable
{
    private Action? _dispose;

    internal TaskChangeSubscription(ChannelReader<bool> reader, Action dispose)
    {
        Reader = reader;
        _dispose = dispose;
    }

    public ChannelReader<bool> Reader { get; }
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
