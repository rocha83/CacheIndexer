using System;
using System.Threading;
using System.Threading.Tasks;
using Rochas.CacheIndexer.Providers;
using Rochas.Data.Specification.Interfaces;

namespace Rochas.CacheIndexer.Helpers
{
    /// <summary>
    /// Despachador de mensagens do canal de persistência para um banco de dados,
    /// usando a interface IPersistenceRepository&lt;T&gt; (Rochas.Data.Specification).
    ///
    /// Mapeamento de ações:
    ///   - Put   → IPersistenceRepository.Add(entity)     (replicar escrita no slave)
    ///   - Del   → IPersistenceRepository.Remove(filter)  (remover por filtro de chave)
    ///   - Clear → não suportado pela interface (lance NotSupportedException)
    ///
    /// O método DispatchAsync é virtual: para replicação idempotente (upsert) ou
    /// DeleteAll customizado, sobrescreva-o no consumidor.
    /// </summary>
    public class DataDispatcher<T> where T : class
    {
        private readonly IPersistenceRepository<T> _repository;

        public DataDispatcher(IPersistenceRepository<T> repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        /// <summary>Despacha uma mensagem do canal de persistência para o repositório.</summary>
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
                            "DeleteAll não é suportado por IPersistenceRepository (requer TRUNCATE). " +
                            "Sobrescreva DispatchAsync para um comportamento customizado.");
                    await _repository.Remove((T)msg.CacheKey).ConfigureAwait(false);
                    break;

                case PersistenceChannelCacheProvider.ChannelAction.Clear:
                    throw new NotSupportedException(
                        "Clear não é suportado por IPersistenceRepository (requer limpeza global). " +
                        "Sobrescreva DispatchAsync para um comportamento customizado.");
            }
        }
    }
}
