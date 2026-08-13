using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Rochas.CacheIndexer.Helpers;
using Rochas.CacheIndexer.Providers;
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
            var repo = new FakePersistenceRepository<Product>();
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
            var repo = new FakePersistenceRepository<Product>();
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
            var dispatcher = new DataDispatcher<Product>(new FakePersistenceRepository<Product>());

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
            var dispatcher = new DataDispatcher<Product>(new FakePersistenceRepository<Product>());

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
        public async Task Worker_ReplicatesPutEvent_ToRepository()
        {
            var provider = new PersistenceChannelCacheProvider(new InMemoryCacheProvider());
            var repo = new FakePersistenceRepository<Product>();
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

        // ── Fake IPersistenceRepository ─────────────────────────────

        private class FakePersistenceRepository<T> : IPersistenceRepository<T> where T : class
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
