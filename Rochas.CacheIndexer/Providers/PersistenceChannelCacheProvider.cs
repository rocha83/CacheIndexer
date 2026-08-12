using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Rochas.CacheIndexer.Providers
{
    /// <summary>
    /// Canal de persistência assíncrona para replicação de dados por evento a um ou
    /// mais SGDBs (worker/client consumer).
    ///
    /// O master grava no cache local e publica no canal; cada consumidor (slave)
    /// se inscreve via Subscribe()/ConsumeAsync() e recebe uma cópia de cada evento
    /// (fan-out real: um canal privado por assinante), persistindo no seu banco.
    ///
    /// Backpressure: canais bounded (FullMode.Wait); se um consumidor ficar lento
    /// além da capacidade, eventos são descartados só para ele. Capacity &lt;= 0 cria
    /// canal unbounded (nenhuma perda).
    /// </summary>
    public class PersistenceChannelCacheProvider : ICacheProvider, IDisposable
    {
        private readonly ICacheProvider _inner;
        private readonly ConcurrentDictionary<Guid, Channel<ChannelMessage>> _subscribers = new();
        private readonly CancellationTokenSource _cts = new();

        public PersistenceChannelCacheProvider(ICacheProvider innerProvider)
        {
            _inner = innerProvider ?? throw new ArgumentNullException(nameof(innerProvider));
        }

        /// <summary>Mensagem de replicação com a ação e os dados para persistir no SGDB consumidor.</summary>
        public class ChannelMessage
        {
            public ChannelAction Action { get; set; }
            public object CacheKey { get; set; }
            public object CacheItem { get; set; }
            public bool DeleteAll { get; set; }
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        }

        public enum ChannelAction
        {
            Put,
            Del,
            Clear
        }

        public object Get(object cacheKey)
            => _inner.Get(cacheKey);

        public void Put(object cacheKey, object cacheItem)
        {
            _inner.Put(cacheKey, cacheItem);
            Broadcast(new ChannelMessage
            {
                Action = ChannelAction.Put,
                CacheKey = cacheKey,
                CacheItem = cacheItem
            });
        }

        public void Del(object cacheKey, bool deleteAll = false)
        {
            _inner.Del(cacheKey, deleteAll);
            Broadcast(new ChannelMessage
            {
                Action = ChannelAction.Del,
                CacheKey = cacheKey,
                CacheItem = null,
                DeleteAll = deleteAll
            });
        }

        public void Clear()
        {
            _inner.Clear();
            Broadcast(new ChannelMessage
            {
                Action = ChannelAction.Clear,
                CacheKey = string.Empty,
                CacheItem = null
            });
        }

        /// <summary>
        /// Inscreve um consumidor (slave) no canal. Cada assinatura cria um canal
        /// privado e recebe uma cópia de todos os eventos publicados.
        /// </summary>
        public ChannelReader<ChannelMessage> Subscribe(int capacity = 1000)
        {
            var channel = capacity > 0
                ? Channel.CreateBounded<ChannelMessage>(new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.Wait
                })
                : Channel.CreateUnbounded<ChannelMessage>();

            _subscribers.TryAdd(Guid.NewGuid(), channel);
            return channel.Reader;
        }

        /// <summary>
        /// Consome o canal de forma assíncrona (conveniência de um único consumidor):
        /// cada chamada cria uma assinatura privada, portanto múltiplos consumidores
        /// recebem cópias independentes dos eventos.
        /// </summary>
        public async IAsyncEnumerable<ChannelMessage> ConsumeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var reader = Subscribe();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _cts.Token);

            while (!linkedCts.Token.IsCancellationRequested)
            {
                ChannelMessage msg;
                try
                {
                    msg = await reader.ReadAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                yield return msg;
            }
        }

        private void Broadcast(ChannelMessage msg)
        {
            foreach (var subscriber in _subscribers)
                subscriber.Value.Writer.TryWrite(msg);
        }

        /// <summary>Encerra o canal e interrompe todos os consumidores.</summary>
        public void Stop()
        {
            _cts.Cancel();

            foreach (var subscriber in _subscribers)
                subscriber.Value.Writer.Complete();
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }
    }
}
