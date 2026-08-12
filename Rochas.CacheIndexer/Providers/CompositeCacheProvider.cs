using System;

namespace Rochas.CacheIndexer.Providers
{
    /// <summary>
    /// Provedor composto L1 (in-memory) + L2 (distribuído) para alta disponibilidade
    /// em cenários com múltiplas instâncias.
    ///
    /// Leitura: L1 local → em miss, busca na L2 e promove o item para a L1.
    /// Escrita: L1 e L2 juntas (write-through).
    /// </summary>
    public class CompositeCacheProvider : ICacheProvider
    {
        private readonly ICacheProvider _l1;
        private readonly ICacheProvider _l2;

        public CompositeCacheProvider(ICacheProvider l1, ICacheProvider l2)
        {
            _l1 = l1 ?? throw new ArgumentNullException(nameof(l1));
            _l2 = l2 ?? throw new ArgumentNullException(nameof(l2));
        }

        public object Get(object cacheKey)
        {
            var hit = _l1.Get(cacheKey);
            if (hit != null)
                return hit;

            var remote = _l2.Get(cacheKey);
            if (remote != null)
            {
                _l1.Put(cacheKey, remote);
                return remote;
            }

            return null;
        }

        public void Put(object cacheKey, object cacheItem)
        {
            _l1.Put(cacheKey, cacheItem);
            _l2.Put(cacheKey, cacheItem);
        }

        public void Del(object cacheKey, bool deleteAll = false)
        {
            _l1.Del(cacheKey, deleteAll);
            _l2.Del(cacheKey, deleteAll);
        }

        public void Clear()
        {
            _l1.Clear();
            _l2.Clear();
        }
    }
}
