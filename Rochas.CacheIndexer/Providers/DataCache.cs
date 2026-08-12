using System;

namespace Rochas.CacheIndexer.Providers
{
    /// <summary>
    /// Fachada estática de acesso ao cache de objetos, desacoplada de qualquer
    /// provedor concreto. Inicialize uma vez no startup e use os métodos estáticos.
    /// </summary>
    public static class DataCache
    {
        private static ICacheProvider _defaultProvider;

        public static ICacheProvider DefaultProvider => _defaultProvider;

        /// <summary>Inicializa o provedor de cache padrão (uma vez no startup).</summary>
        public static void Initialize(ICacheProvider defaultProvider)
        {
            _defaultProvider = defaultProvider;
        }

        /// <summary>
        /// Inicializa o provedor in-memory padrão com limite de memória em MB.
        /// Use 0 para sem limite.
        /// </summary>
        public static void Initialize(int memorySizeLimit)
        {
            _defaultProvider = new InMemoryCacheProvider(memorySizeLimit);
        }

        public static void Put(object cacheKey, object cacheItem)
        {
            var provider = _defaultProvider ?? throw new InvalidOperationException(
                "Nenhum provedor de cache inicializado. Chame DataCache.Initialize(...) antes de usar.");

            provider.Put(cacheKey, cacheItem);
        }

        public static object Get(object cacheKey)
        {
            var provider = _defaultProvider ?? throw new InvalidOperationException(
                "Nenhum provedor de cache inicializado. Chame DataCache.Initialize(...) antes de usar.");

            return provider.Get(cacheKey);
        }

        public static void Del(object cacheKey, bool deleteAll = false)
        {
            var provider = _defaultProvider ?? throw new InvalidOperationException(
                "Nenhum provedor de cache inicializado. Chame DataCache.Initialize(...) antes de usar.");

            provider.Del(cacheKey, deleteAll);
        }

        public static void Clear()
        {
            var provider = _defaultProvider ?? throw new InvalidOperationException(
                "Nenhum provedor de cache inicializado. Chame DataCache.Initialize(...) antes de usar.");

            provider.Clear();
        }
    }
}
