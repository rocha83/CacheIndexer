using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rochas.CacheIndexer.Providers;

namespace Rochas.CacheIndexer.Helpers
{
    /// <summary>
    /// Worker Background Service (Microsoft.Extensions.Hosting) que consome o canal
    /// de persistÃªncia e replica cada evento em um SGDB via DataDispatcher&lt;T&gt;
    /// (IGenericRepository&lt;T&gt; do Rochas.Data.Specification).
    ///
    /// Registro em ASP.NET Core (DI):
    ///   services.AddSingleton(provider);
    ///   services.AddHostedService(sp =>
    ///       new PersistenceChannelWorker&lt;Product&gt;(
    ///           sp.GetRequiredService&lt;PersistenceChannelCacheProvider&gt;(),
    ///           new DataDispatcher&lt;Product&gt;(new GenericRepository&lt;Product&gt;(engine, connStr))));
    ///
    /// A assinatura do canal Ã© criada no construtor; o loop Ã© iniciado no StartAsync
    /// e interrompido no StopAsync (graceful). Falhas por mensagem sÃ£o registradas
    /// no logger e o consumo continua.
    /// </summary>
    public class PersistenceChannelWorker<T> : BackgroundService where T : class
    {
        private readonly ChannelReader<PersistenceChannelCacheProvider.ChannelMessage> _reader;
        private readonly DataDispatcher<T> _dispatcher;
        private readonly ILogger _logger;

        public PersistenceChannelWorker(
            PersistenceChannelCacheProvider provider,
            DataDispatcher<T> dispatcher,
            ILogger logger = null)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            _reader = provider.Subscribe();
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var msg in _reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _dispatcher.DispatchAsync(msg, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Falha ao replicar mensagem {Action} no canal de persistÃªncia.", msg.Action);
                }
            }
        }
    }
}
