using System.Runtime.CompilerServices;

namespace ShardECS.SECS.Roads;

public sealed class Road
{
    private readonly bool       _condition;
    private readonly string     _label;
    private readonly List<Road> _thenBranch = [];
    private readonly List<Road> _elseBranch = [];
    private Action<int>?        _thenAction;
    private Action<int>?        _elseAction;

    private Road(bool condition, string label)
    {
        _condition = condition;
        _label     = label;
    }

    public static Road If(
        bool condition,
        [CallerArgumentExpression(nameof(condition))] string label = "")
        => new(condition, label);

    public static RoadRoot For(int entityId) => new(entityId);

    public Road Then(Action<int> action) { _thenAction = action; return this; }
    public Road Then(params Road[] children) { _thenBranch.AddRange(children); return this; }
    public Road Else(Action<int> action) { _elseAction = action; return this; }
    public Road Else(params Road[] children) { _elseBranch.AddRange(children); return this; }

    internal void Execute(int entity, RoadDebugLog? log, int depth)
    {
        if (_condition)
        {
            log?.RecordTaken(_label, depth);
            _thenAction?.Invoke(entity);
            foreach (var child in _thenBranch)
                child.Execute(entity, log, depth + 1);
            if (log != null)
                foreach (var child in _elseBranch)
                    child.RecordSkipped(log, depth + 1);
        }
        else
        {
            log?.RecordElse(_label, depth);
            _elseAction?.Invoke(entity);
            foreach (var child in _elseBranch)
                child.Execute(entity, log, depth + 1);
            if (log != null)
                foreach (var child in _thenBranch)
                    child.RecordSkipped(log, depth + 1);
        }
    }

    internal void RecordSkipped(RoadDebugLog log, int depth)
    {
        log.RecordSkipped(_label, depth);
        foreach (var child in _thenBranch) child.RecordSkipped(log, depth + 1);
        foreach (var child in _elseBranch) child.RecordSkipped(log, depth + 1);
    }
}

public sealed class RoadRoot
{
    private readonly int           _entity;
    private          Road?         _root;
    private          RoadDebugLog? _log;

    internal RoadRoot(int entity) => _entity = entity;

    public RoadRoot If(
        bool condition,
        [CallerArgumentExpression(nameof(condition))] string label = "")
    {
        _root = Road.If(condition, label);
        return this;
    }

    public RoadRoot Then(Action<int> action) { _root!.Then(action); return this; }
    public RoadRoot Then(params Road[] children) { _root!.Then(children); return this; }
    public RoadRoot Else(Action<int> action) { _root!.Else(action); return this; }
    public RoadRoot Else(params Road[] children) { _root!.Else(children); return this; }
    public RoadRoot WithDebug() { _log = new RoadDebugLog(); return this; }

    public void Execute()
    {
        if (_root is null) return;
        _root.Execute(_entity, _log, 0);
        _log?.Print(_entity);
    }
}
