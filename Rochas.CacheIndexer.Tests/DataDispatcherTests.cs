using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Rochas.CacheIndexer.Helpers;
using Rochas.CacheIndexer.Providers;
using Rochas.Data.Specification.Enums;
using Rochas.Data.Specification.Interfaces;
using Rochas.Data.Specification.Models;
using Xunit;

namespace Rochas.CacheIndexer.Tests
{
    public class DataDispatcherTests
    {
        private class Product
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        [Fact]
        public async Task Dispatch_Put_CallsAddOnRepository()
        {
            var repo = new FakeGenericRepository<Product>();
            var dispatcher = new DataDispatcher<Product>(repo);
            var item = new Product { Id = 1, Name = "Caneta" };

            await dispatcher.DispatchAsync(new PersistenceChannelCacheProvider.ChannelMessage
            {
                Action = PersistenceChannelCacheProvider.ChannelAction.Put,
                CacheItem = item
            });

            repo.Added.Should().ContainSingle().Which.Should().BeSameAs(item);
        }

        [Fact]
        public async Task Dispatch_Del_CallsRemoveWithKeyFilter()
        {
            var repo = new FakeGenericRepository<Product>();
            var dispatcher = new DataDispatcher<Product>(repo);
            var key = new Product { Id = 7 };

            await dispatcher.DispatchAsync(new PersistenceChannelCacheProvider.ChannelMessage
            {
                Action = PersistenceChannelCacheProvider.ChannelAction.Del,
                CacheKey = key
            });

            repo.Removed.Should().ContainSingle().Which.Id.Should().Be(7);
        }

        [Fact]
        public async Task Dispatch_Clear_ThrowsNotSupported()
        {
            var dispatcher = new DataDispatcher<Product>(new FakeGenericRepository<Product>());

            Func<Task> act = () => dispatcher.DispatchAsync(
                new PersistenceChannelCacheProvider.ChannelMessage
                {
                    Action = PersistenceChannelCacheProvider.ChannelAction.Clear
                });

            await act.Should().ThrowAsync<NotSupportedException>();
        }

        [Fact]
        public async Task Dispatch_DelDeleteAll_ThrowsNotSupported()
        {
            var dispatcher = new DataDispatcher<Product>(new FakeGenericRepository<Product>());

            Func<Task> act = () => dispatcher.DispatchAsync(
                new PersistenceChannelCacheProvider.ChannelMessage
                {
                    Action = PersistenceChannelCacheProvider.ChannelAction.Del,
                    CacheKey = new Product { Id = 1 },
                    DeleteAll = true
                });

            await act.Should().ThrowAsync<NotSupportedException>();
        }

        [Fact]
        public void Dispatch_NullRepository_Throws()
        {
            Action act = () => new DataDispatcher<Product>(null);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public async Task Dispatch_NullMessage_IsIgnored()
        {
            var dispatcher = new DataDispatcher<Product>(new FakeGenericRepository<Product>());

            await dispatcher.DispatchAsync(null);
        }

        [Fact]
        public async Task Worker_ReplicatesPutEvent_ToRepository()
        {
            var provider = new PersistenceChannelCacheProvider(new InMemoryCacheProvider());
            var repo = new FakeGenericRepository<Product>();
            var worker = new PersistenceChannelWorker<Product>(provider, new DataDispatcher<Product>(repo));
            var item = new Product { Id = 42, Name = "Caderno" };

            await worker.StartAsync(CancellationToken.None);
            try
            {
                provider.Put(new { Id = 42 }, item);

                var received = await repo.WaitForAddAsync(CancellationToken.None, TimeSpan.FromSeconds(5));
                received.Should().BeSameAs(item);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Worker_MessageFailure_LogsErrorAndKeepsConsuming()
        {
            var provider = new PersistenceChannelCacheProvider(new InMemoryCacheProvider());
            var repo = new FakeGenericRepository<Product>();
            var logger = new FakeLogger();
            var worker = new PersistenceChannelWorker<Product>(provider, new DataDispatcher<Product>(repo), logger);
            var item = new Product { Id = 43, Name = "Pasta" };

            await worker.StartAsync(CancellationToken.None);
            try
            {
                provider.Del(new Product { Id = 1 }, deleteAll: true);   // DispatchAsync lanÃ§a NotSupportedException
                provider.Put(new { Id = 43 }, item);                     // consumo deve continuar

                var received = await repo.WaitForAddAsync(CancellationToken.None, TimeSpan.FromSeconds(5));
                received.Should().BeSameAs(item);
                logger.Logs.Should().NotBeEmpty();
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public void Worker_NullProvider_Throws()
        {
            var dispatcher = new DataDispatcher<Product>(new FakeGenericRepository<Product>());

            Action act = () => new PersistenceChannelWorker<Product>(null, dispatcher);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public async Task Worker_StopWhileIdle_ExitsGracefully()
        {
            var provider = new PersistenceChannelCacheProvider(new InMemoryCacheProvider());
            var repo = new FakeGenericRepository<Product>();
            var worker = new PersistenceChannelWorker<Product>(provider, new DataDispatcher<Product>(repo));

            await worker.StartAsync(CancellationToken.None);
            await Task.Delay(100); // loop ocioso pendurado em ReadAllAsync
            await worker.StopAsync(CancellationToken.None);

            worker.Should().NotBeNull(); // StopAsync retornou: ExecuteAsync encerrou sem exceÃ§Ã£o
        }

        [Fact]
        public async Task Worker_ChannelCompleted_EndsLoopNormally()
        {
            var provider = new PersistenceChannelCacheProvider(new InMemoryCacheProvider());
            var repo = new FakeGenericRepository<Product>();
            var worker = new PersistenceChannelWorker<Product>(provider, new DataDispatcher<Product>(repo));

            await worker.StartAsync(CancellationToken.None);
            await Task.Delay(100);
            provider.Dispose(); // completa o canal -> ReadAllAsync termina -> loop sai
            await worker.StopAsync(CancellationToken.None);
        }

        // â”€â”€ Fake IGenericRepository â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private class FakeLogger : ILogger
        {
            public ConcurrentQueue<(LogLevel Level, string Message)> Logs { get; } = new();

            public IDisposable BeginScope<TState>(TState state) => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
                => Logs.Enqueue((logLevel, formatter(state, exception)));
        }

        private class FakeGenericRepository<T> : IGenericRepository<T> where T : class
        {
            public ConcurrentQueue<T> Added { get; } = new();
            public ConcurrentQueue<T> Removed { get; } = new();

            private readonly TaskCompletionSource<T> _addSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Initialize(string databaseFileName, string tableScript) { }

            public Task<int> Add(T entity, bool persistComposition = false)
            {
                Added.Enqueue(entity);
                _addSignal.TrySetResult(entity);
                return Task.FromResult(1);
            }

            public int AddSync(T entity, bool persistComposition = false) => Add(entity).GetAwaiter().GetResult();

            public Task AddRange(IEnumerable<T> entities, bool persistComposition = false)
                => Task.CompletedTask;

            public void AddRangeSync(IEnumerable<T> entities, bool persistComposition = false) { }

            public Task<int> Remove(T filterEntity)
            {
                Removed.Enqueue(filterEntity);
                return Task.FromResult(1);
            }

            public int RemoveSync(T filterEntity) => 1;

            public Task<int> Update(T entity, T filterEntity, bool persistComposition = false)
                => Task.FromResult(1);

            public int UpdateSync(T entity, T filterEntity, bool persistComposition = false) => 1;

            public Task<int> Count(T filterEntity) => Task.FromResult(0);
            public int CountSync(T filterEntity) => 0;

            public Task<T> Get(object key, bool loadComposition = false) => Task.FromResult<T>(null);
            public T GetSync(object key, bool loadComposition = false) => null;
            public Task<T> Get(T filter, bool loadComposition = false) => Task.FromResult<T>(null);
            public T GetSync(T filter, bool loadComposition = false) => null;

            public IQueryBuilder<T> Search(object criteria, bool loadComposition = false, bool filterConjunction = false)
                => throw new NotImplementedException();
            public IQuerySyncBuilder<T> SearchSync(object criteria, bool loadComposition = false, bool filterConjunction = false)
                => throw new NotImplementedException();
            public IQueryPaginatedBuilder<T> Search(object criteria, int page, int pageSize, bool loadComposition = false, bool filterConjunction = false)
                => throw new NotImplementedException();
            public IQueryPaginatedBuilder<T> SearchSync(object criteria, int page, int pageSize, bool loadComposition = false, bool filterConjunction = false)
                => throw new NotImplementedException();

            public ICollection<T> BulkSearch(object[] criterias, bool loadComposition = false, int recordsLimit = 0, string sortAttributes = null, bool orderDescending = false)
                => new List<T>();
            public ICollection<T> BulkSearchSync(object[] criterias, bool loadComposition = false, int recordsLimit = 0, string sortAttributes = null, bool orderDescending = false)
                => new List<T>();

            public IQueryBuilder<T> Query(T filter, bool loadComposition = false, bool filterConjunction = false)
                => throw new NotImplementedException();
            public IQueryPaginatedBuilder<T> Query(T filter, int page, int pageSize, bool loadComposition = false, bool filterConjunction = false)
                => throw new NotImplementedException();
            public IQueryBuilder<T> OrderBy(params string[] sortAttributes)
                => throw new NotImplementedException();
            public IQueryBuilder<T> OrderByDescending(params string[] sortAttributes)
                => throw new NotImplementedException();
            public IQueryBuilder<T> GroupBy(string[] groupAttributes, Dictionary<string, DataAggregationType> aggregates = null)
                => throw new NotImplementedException();

            public IQuerySyncBuilder<T> QuerySync(T filter, bool loadComposition = false, bool filterConjunction = false)
                => throw new NotImplementedException();
            public IQueryPaginatedBuilder<T> QuerySync(T filter, int page, int pageSize, bool loadComposition = false, bool filterConjunction = false)
                => throw new NotImplementedException();

            public Task<ICollection<T>> QueryRaw(string sql, Dictionary<string, object> parameters)
                => Task.FromResult<ICollection<T>>(new List<T>());
            public ICollection<T> QueryRawSync(string sql, Dictionary<string, object> parameters) => new List<T>();
            public Task<PaginatedResult<T>> QueryRaw(string sql, string countSql, Dictionary<string, object> parameters, int page = 1, int pageSize = 20)
                => Task.FromResult<PaginatedResult<T>>(new PaginatedResult<T>());
            public PaginatedResult<T> QueryRawSync(string sql, string countSql, Dictionary<string, object> parameters, int page = 1, int pageSize = 20)
                => new PaginatedResult<T>();

            public async Task<T> WaitForAddAsync(CancellationToken ct, TimeSpan timeout)
            {
                var completed = await Task.WhenAny(_addSignal.Task, Task.Delay(timeout, ct));
                completed.Should().Be(_addSignal.Task, "worker deveria ter replicado o evento Put");
                return await _addSignal.Task;
            }

            public void Dispose() { }
        }
    }
}
