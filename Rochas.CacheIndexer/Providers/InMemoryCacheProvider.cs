using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Rochas.Data.Specification.Interfaces;
using System.Text.Json;

namespace Rochas.CacheIndexer.Providers
{
    /// <summary>
    /// Provedor de cache in-memory (padrão), thread-safe via ConcurrentDictionary.
    ///
    /// A chave combina hash do tipo com a chave serializada, permitindo que
    /// tipos diferentes coexistam com a mesma chave de negócio.
    /// </summary>
    public class InMemoryCacheProvider : ICacheProvider
    {
        private ConcurrentDictionary<KeyValuePair<uint, string>, object> cacheItems =
            new ConcurrentDictionary<KeyValuePair<uint, string>, object>();

        private readonly int _memorySizeLimit;

        /// <summary>memorySizeLimit (MB) esvazia o cache ao ser excedido. 0 desabilita.</summary>
        public InMemoryCacheProvider(int memorySizeLimit = 0)
        {
            _memorySizeLimit = memorySizeLimit;
        }

        public object Get(object cacheKey)
        {
            object result = null;

            if (cacheKey != null)
            {
                var serialCacheKey = BuildCacheKey(cacheKey);

                if (cacheItems.ContainsKey(serialCacheKey))
                    result = cacheItems[serialCacheKey];
            }

            var listResult = result as IList;
            if ((listResult != null) && (listResult.Count == 1))
                result = ((IList)result)[0];

            return result;
        }

        public void Put(object cacheKey, object cacheItem)
        {
            if ((cacheKey != null) && (cacheItem != null))
            {
                CheckMemoryUsage();

                var serialCacheKey = BuildCacheKey(cacheKey);

                if (!cacheItems.ContainsKey(serialCacheKey))
                    cacheItems.TryAdd(serialCacheKey, cacheItem);
            }
        }

        public void Del(object cacheKey, bool deleteAll = false)
        {
            if (cacheKey != null)
            {
                var serialCacheKey = BuildCacheKey(cacheKey);

                if (cacheItems.ContainsKey(serialCacheKey))
                    cacheItems.TryRemove(serialCacheKey, out var _fake);

                if (deleteAll)
                {
                    var typeKey = ComputeTypeHash(cacheKey.GetType());
                    foreach (var key in cacheItems.Keys.Where(k => k.Key.Equals(typeKey)).ToList())
                        cacheItems.TryRemove(key, out var _fake2);
                }
            }
        }

        public void Clear()
        {
            cacheItems = new ConcurrentDictionary<KeyValuePair<uint, string>, object>();
        }

        private void CheckMemoryUsage()
        {
            if (_memorySizeLimit > 0)
            {
                var memSize = GC.GetTotalMemory(false) / 1024 / 1024;
                if (memSize > _memorySizeLimit)
                    cacheItems = new ConcurrentDictionary<KeyValuePair<uint, string>, object>();
            }
        }

        private static KeyValuePair<uint, string> BuildCacheKey(object cacheKey)
        {
            var serialKey = JsonSerializer.Serialize(cacheKey);
            return new KeyValuePair<uint, string>(
                ComputeTypeHash(cacheKey.GetType()), serialKey);
        }

        /// <summary>Hash FNV-1a 32 bits do tipo.</summary>
        private static uint ComputeTypeHash(Type type)
        {
            var value = type?.FullName ?? string.Empty;
            if (string.IsNullOrEmpty(value))
                return 0;

            const uint prime = 16777619;
            uint hash = 2166136261;

            foreach (char c in value)
            {
                hash ^= (byte)c;
                hash *= prime;
            }

            return hash;
        }
    }
}
