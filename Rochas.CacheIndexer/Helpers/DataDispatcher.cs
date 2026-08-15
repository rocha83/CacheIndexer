using System;
using System.Threading;
using System.Threading.Tasks;
using Rochas.CacheIndexer.Providers;
using Rochas.Data.Specification.Interfaces;

namespace Rochas.CacheIndexer.Helpers
{
    /// <summary>
    /// Despachador de mensagens do canal de persistÃªncia para um banco de dados,
    /// usando a interface IGenericRepository&lt;T&gt; (Rochas.Data.Specification).
    ///
    /// Mapeamento de aÃ§Ãµes:
    ///   - Put   â†’ IGenericRepository.Add(entity)      (replicar escrita no slave)
    ///   - Del   â†’ IGenericRepository.Remove(filter)   (remover por filtro de chave)
    ///   - Clear â†’ nÃ£o suportado pela interface (lance NotSupportedException)
    ///
    /// O mÃ©todo DispatchAsync Ã© virtual: para replicaÃ§Ã£o idempotente (upsert) ou
    /// DeleteAll customizado, sobrescreva-o no consumidor.
    /// </summary>
    public class DataDispatcher<T> where T : class
    {
        private readonly IGenericRepository<T> _repository;

        public DataDispatcher(IGenericRepository<T> repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        /// <summary>Despacha uma mensagem do canal de persistÃªncia para o repositÃ³rio.</summary>
        public virtual async Task DispatchAsync(
            PersistenceChannelCacheProvider.ChannelMessage msg,
            CancellationToken cancellationToken = default)
        {
            if (msg == null)
                return;

            switch (msg.Action)
            {
                case PersistenceChannelCacheProvider.ChannelAction.Put:
                    await _repository.Add((T)msg.CacheItem).ConfigureAwait(false);
                    break;

                case PersistenceChannelCacheProvider.ChannelAction.Del:
                    if (msg.DeleteAll)
                        throw new NotSupportedException(
                            "DeleteAll nÃ£o Ã© suportado por IGenericRepository (requer TRUNCATE). " +
                            "Sobrescreva DispatchAsync para um comportamento customizado.");
                    await _repository.Remove((T)msg.CacheKey).ConfigureAwait(false);
                    break;

                case PersistenceChannelCacheProvider.ChannelAction.Clear:
                    throw new NotSupportedException(
                        "Clear nÃ£o Ã© suportado por IGenericRepository (requer limpeza global). " +
                        "Sobrescreva DispatchAsync para um comportamento customizado.");
            }
        }
    }
}
