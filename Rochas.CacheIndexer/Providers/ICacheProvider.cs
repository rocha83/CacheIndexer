namespace Rochas.CacheIndexer.Providers
{
    /// <summary>
    /// Contrato de provedor de cache de objetos.
    /// Permite trocar o armazenamento (in-memory, distribuído, composto ou canal)
    /// sem alterar o consumidor.
    /// </summary>
    public interface ICacheProvider
    {
        /// <summary>Recupera item do cache pela chave serializada.</summary>
        object Get(object cacheKey);

        /// <summary>Armazena item no cache com chave serializada.</summary>
        void Put(object cacheKey, object cacheItem);

        /// <summary>Remove item do cache. Se deleteAll=true, remove todos do mesmo tipo.</summary>
        void Del(object cacheKey, bool deleteAll = false);

        /// <summary>Limpa todo o cache.</summary>
        void Clear();
    }
}
