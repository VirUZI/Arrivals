using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Arrivals.Infrastructure;

public sealed class ResponseCache
{
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public ResponseCache(IMemoryCache cache)
    {
        _cache = cache;
    }

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        TimeSpan lifetime,
        bool forceRefresh,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        if (forceRefresh)
        {
            _cache.Remove(key);
        }

        if (_cache.TryGetValue<T>(key, out var existing))
        {
            return existing!;
        }

        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue<T>(key, out existing))
            {
                return existing!;
            }

            var value = await factory(cancellationToken);
            _cache.Set(key, value, lifetime);
            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Remove(string key) => _cache.Remove(key);
}
