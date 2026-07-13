namespace Nemerle.ProjectInfo;

public sealed class ProjectInfoProvider : IAsyncDisposable
{
    private readonly IProjectSnapshotLoader _loader;
    private readonly SemaphoreSlim _queryGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<ProjectQueryKey, NemerleProjectSnapshot> _cache;
    private readonly Dictionary<ProjectQueryKey, Task<NemerleProjectSnapshot>> _inflight;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private bool _disposed;

    public ProjectInfoProvider(IProjectSnapshotLoader? loader = null)
    {
        _loader = loader ?? new MsBuildProjectQuery();
        var comparer = ProjectQueryKeyComparer.Instance;
        _cache = new Dictionary<ProjectQueryKey, NemerleProjectSnapshot>(comparer);
        _inflight = new Dictionary<ProjectQueryKey, Task<NemerleProjectSnapshot>>(comparer);
    }

    public Task<NemerleProjectSnapshot> GetSnapshotAsync(
        ProjectQueryKey key,
        bool forceReload,
        CancellationToken cancellationToken)
    {
        Task<NemerleProjectSnapshot> query;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!forceReload && _cache.TryGetValue(key, out var cached))
                return Task.FromResult(cached);

            if (_inflight.TryGetValue(key, out query!))
                return query.WaitAsync(cancellationToken);

            if (forceReload)
                _cache.Remove(key);
            query = QueryAndCacheAsync(key, cancellationToken);
            _inflight.Add(key, query);
        }
        return query;
    }

    public void Clear()
    {
        lock (_sync)
            _cache.Clear();
    }

    private async Task<NemerleProjectSnapshot> QueryAndCacheAsync(
        ProjectQueryKey key,
        CancellationToken cancellationToken)
    {
        // Ensure the task is entered into _inflight before even a synchronous fake loader can complete.
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        try
        {
            await _queryGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var snapshot = await _loader.LoadAsync(key, linked.Token).ConfigureAwait(false);
                lock (_sync)
                    _cache[key] = snapshot;
                return snapshot;
            }
            finally
            {
                _queryGate.Release();
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new ProjectQueryException(
                ProjectQueryErrorKind.Cancelled,
                "The MSBuild project query was cancelled.",
                innerException: ex);
        }
        finally
        {
            lock (_sync)
                _inflight.Remove(key);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<NemerleProjectSnapshot>[] inflight;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            inflight = _inflight.Values.Distinct().ToArray();
        }

        await _disposeCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(inflight).ConfigureAwait(false);
        }
        catch
        {
            // Disposal owns cancellation and only waits until every process has released the shared gate.
        }
        _disposeCancellation.Dispose();
        _queryGate.Dispose();
    }

    private sealed class ProjectQueryKeyComparer : IEqualityComparer<ProjectQueryKey>
    {
        public static readonly ProjectQueryKeyComparer Instance = new();
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        public bool Equals(ProjectQueryKey? x, ProjectQueryKey? y) =>
            ReferenceEquals(x, y) || x is not null && y is not null &&
            PathComparer.Equals(x.DotNetExecutable, y.DotNetExecutable) &&
            PathComparer.Equals(x.ProjectPath, y.ProjectPath) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Configuration, y.Configuration) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Platform, y.Platform) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.TargetFramework, y.TargetFramework);

        public int GetHashCode(ProjectQueryKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.DotNetExecutable, PathComparer);
            hash.Add(obj.ProjectPath, PathComparer);
            hash.Add(obj.Configuration, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.Platform, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.TargetFramework, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }
}
