using System;
using Rochas.CacheIndexer.Providers;

namespace Rochas.CacheIndexer.Annotations
{
    /// <summary>
    /// Marca uma entidade/POCO como cacheável, associando um tipo de provedor
    /// (ICacheProvider) à entidade.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public class CacheableAttribute : Attribute
    {
        /// <summary>
        /// Tipo do provedor de cache (ICacheProvider) associado à entidade.
        /// Ex.: typeof(InMemoryCacheProvider), typeof(DistributedCacheProvider).
        /// </summary>
        public Type CacheProviderType { get; }

        public CacheableAttribute(Type cacheProviderType)
        {
            CacheProviderType = cacheProviderType
                ?? throw new ArgumentNullException(nameof(cacheProviderType));

            if (!typeof(ICacheProvider).IsAssignableFrom(cacheProviderType))
                throw new ArgumentException(
                    $"O tipo '{cacheProviderType.FullName}' deve implementar ICacheProvider.",
                    nameof(cacheProviderType));
        }
    }
}
