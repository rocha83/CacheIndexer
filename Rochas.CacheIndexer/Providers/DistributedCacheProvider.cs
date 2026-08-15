using System;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Rochas.Data.Specification.Interfaces;

namespace Rochas.CacheIndexer.Providers
{
    /// <summary>
    /// Provedor de cache distribuído para Redis ou Microsoft Garnet, via IDistributedCache.
    /// Pode receber uma instância configurada (DI) ou uma connection string
    /// (Garnet é Redis-compatible e usa o mesmo caminho).
    /// </summary>
    public class DistributedCacheProvider : ICacheProvider
    {
        private readonly IDistributedCache _cache;
        private readonly string _instanceName;
        private readonly TimeSpan? _defaultExpiration;

        /// <summary>Usa uma instância de IDistributedCache fornecida pelo host (DI).</summary>
        public DistributedCacheProvider(IDistributedCache cache, string instanceName = "",
            TimeSpan? defaultExpiration = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _instanceName = instanceName;
            _defaultExpiration = defaultExpiration ?? TimeSpan.FromMinutes(5);
        }

        /// <summary>Cria um RedisCache internamente a partir da connection string.</summary>
        public DistributedCacheProvider(string connectionString, string instanceName = "cache:",
            TimeSpan? defaultExpiration = null)
            : this(CreateRedisCache(connectionString, instanceName), instanceName, defaultExpiration)
        {
        }

        public object Get(object cacheKey)
        {
            if (cacheKey == null)
                return null;

            var bytes = _cache.Get(BuildKey(cacheKey));
            if (bytes == null)
                return null;

            return JsonSerializer.Deserialize<object>(bytes);
        }

        public void Put(object cacheKey, object cacheItem)
        {
            if (cacheKey == null || cacheItem == null)
                return;

            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _defaultExpiration
            };
            _cache.Set(BuildKey(cacheKey), JsonSerializer.SerializeToUtf8Bytes(cacheItem), options);
        }

        public void Del(object cacheKey, bool deleteAll = false)
        {
            if (cacheKey == null)
                return;

            _cache.Remove(BuildKey(cacheKey));

            if (deleteAll)
                throw new NotSupportedException(
                    "deleteAll requer varredura de chaves; use o flush do host.");
        }

        public void Clear()
        {
            throw new NotSupportedException(
                "IDistributedCache não expõe limpeza global; use o flush do host.");
        }

        private static IDistributedCache CreateRedisCache(string connectionString, string instanceName)
        {
            return new RedisCache(new RedisCacheOptions
            {
                Configuration = connectionString,
                InstanceName = instanceName
            });
        }

        private string BuildKey(object cacheKey)
        {
            return _instanceName + JsonSerializer.Serialize(cacheKey);
        }
    }
}
