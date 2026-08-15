using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Rochas.CacheIndexer.Annotations;
using Rochas.CacheIndexer.Providers;
using Xunit;

namespace Rochas.CacheIndexer.Tests
{
    public class CacheProvidersTests
    {
        // ── Modelos de teste ────────────────────────────────────────────

        private class Product
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        private class Order
        {
            public int Id { get; set; }
            public decimal Total { get; set; }
        }

        // ── InMemoryCacheProvider ───────────────────────────────────────

        [Fact]
        public void InMemory_PutAndGet_ReturnsSameItem()
        {
            var provider = new InMemoryCacheProvider();
            var product = new Product { Id = 7, Name = "Caneta" };
            var key = new { Id = 7 };

            provider.Put(key, product);

            var result = provider.Get(new { Id = 7 });
            result.Should().BeSameAs(product);
        }

        [Fact]
        public void InMemory_GetMiss_ReturnsNull()
        {
            var provider = new InMemoryCacheProvider();

            provider.Get(new { Id = 999 }).Should().BeNull();
        }

        [Fact]
        public void InMemory_SameBusinessKeyDifferentTypes_Coexist()
        {
            var provider = new InMemoryCacheProvider();
            var product = new Product { Id = 1, Name = "Caneta" };
            var order = new Order { Id = 1, Total = 99.9m };
            var productKey = new { Kind = "Product", Id = 1 };
            var orderKey = new { Kind = "Order", Id = 1 };

            provider.Put(productKey, product);
            provider.Put(orderKey, order);

            provider.Get(new { Kind = "Product", Id = 1 }).Should().BeSameAs(product);
            provider.Get(new { Kind = "Order", Id = 1 }).Should().BeSameAs(order);
        }

        [Fact]
        public void InMemory_DelSpecific_RemovesOnlyThatKey()
        {
            var provider = new InMemoryCacheProvider();
            var p1 = new Product { Id = 1 };
            var p2 = new Product { Id = 2 };
            provider.Put(new { Id = 1 }, p1);
            provider.Put(new { Id = 2 }, p2);

            provider.Del(new { Id = 1 });

            provider.Get(new { Id = 1 }).Should().BeNull();
            provider.Get(new { Id = 2 }).Should().BeSameAs(p2);
        }

        [Fact]
        public void InMemory_DelDeleteAll_RemovesAllOfTypeButKeepsOthers()
        {
            var provider = new InMemoryCacheProvider();
            var p1 = new Product { Id = 1 };
            var p2 = new Product { Id = 2 };
            var order = new Order { Id = 1 };
            provider.Put(new Product { Id = 1 }, p1);
            provider.Put(new Product { Id = 2 }, p2);
            provider.Put(new Order { Id = 1 }, order);

            provider.Del(new Product { Id = 1 }, deleteAll: true);

            provider.Get(new Product { Id = 1 }).Should().BeNull();
            provider.Get(new Product { Id = 2 }).Should().BeNull();
            provider.Get(new Order { Id = 1 }).Should().BeSameAs(order);
        }

        [Fact]
        public void InMemory_Clear_EmptiesEverything()
        {
            var provider = new InMemoryCacheProvider();
            provider.Put(new { Id = 1 }, new Product { Id = 1 });

            provider.Clear();

            provider.Get(new { Id = 1 }).Should().BeNull();
        }

        [Fact]
        public void InMemory_SingleElementList_IsUnwrappedOnGet()
        {
            var provider = new InMemoryCacheProvider();
            provider.Put(new { Id = 1 }, new List<string> { "unico" });

            provider.Get(new { Id = 1 }).Should().Be("unico");
        }

        [Fact]
        public void InMemory_NullKeyOrItem_AreIgnored()
        {
            var provider = new InMemoryCacheProvider();

            provider.Put(null, new Product());
            provider.Put(new Product { Id = 1 }, null);

            provider.Get(new Product { Id = 1 }).Should().BeNull();
        }

        [Fact]
        public void InMemory_MemoryLimitNotReached_KeepsItems()
        {
            var provider = new InMemoryCacheProvider(memorySizeLimit: 10000);
            var item = new Product { Id = 1, Name = "Caneta" };

            provider.Put(item, item);

            provider.Get(item).Should().BeSameAs(item);
        }

        // ── DistributedCacheProvider ────────────────────────────────────

        [Fact]
        public void Distributed_PutAndGet_UsingAnyIDistributedCache()
        {
            var provider = new DistributedCacheProvider(new FakeDistributedCache(), instanceName: "test:");
            var item = new Product { Id = 3, Name = "Lapis" };

            provider.Put(item, item);

            var result = provider.Get(item);

            result.Should().BeOfType<System.Text.Json.JsonElement>();
            var element = (System.Text.Json.JsonElement)result;
            element.TryGetProperty("Name", out var name).Should().BeTrue();
            name.GetString().Should().Be("Lapis");
        }

        [Fact]
        public void Distributed_GetMiss_ReturnsNull()
        {
            var provider = new DistributedCacheProvider(new FakeDistributedCache());

            provider.Get(new Product { Id = 42 }).Should().BeNull();
        }

        [Fact]
        public void Distributed_Del_RemovesKey()
        {
            var fake = new FakeDistributedCache();
            var provider = new DistributedCacheProvider(fake);

            provider.Put(new Product { Id = 9 }, new Product { Id = 9 });
            provider.Del(new Product { Id = 9 });

            provider.Get(new Product { Id = 9 }).Should().BeNull();
        }

        [Fact]
        public void Distributed_Get_NullKey_ReturnsNull()
        {
            var provider = new DistributedCacheProvider(new FakeDistributedCache());

            provider.Get(null).Should().BeNull();
        }

        [Fact]
        public void Distributed_Put_NullArguments_AreIgnored()
        {
            var fake = new FakeDistributedCache();
            var provider = new DistributedCacheProvider(fake);

            provider.Put(null, new Product { Id = 5 });
            provider.Put(new Product { Id = 5 }, null);

            provider.Get(new Product { Id = 5 }).Should().BeNull();
        }

        [Fact]
        public void Distributed_Del_NullKey_DoesNotThrow()
        {
            var provider = new DistributedCacheProvider(new FakeDistributedCache());

            provider.Del(null);
        }

        [Fact]
        public void Distributed_DelDeleteAll_ThrowsNotSupported()
        {
            var provider = new DistributedCacheProvider(new FakeDistributedCache());

            Action act = () => provider.Del(new Product { Id = 1 }, deleteAll: true);

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Distributed_Clear_ThrowsNotSupported()
        {
            var provider = new DistributedCacheProvider(new FakeDistributedCache());

            Action act = () => provider.Clear();

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Distributed_ConnectionStringCtor_CreatesRedisCache()
        {
            var provider = new DistributedCacheProvider("localhost:6379");

            provider.Should().NotBeNull();
        }

        // ── CompositeCacheProvider ──────────────────────────────────────

        [Fact]
        public void Composite_WriteThrough_FillsBothLayers()
        {
            var l1 = new InMemoryCacheProvider();
            var l2 = new InMemoryCacheProvider();
            var provider = new CompositeCacheProvider(l1, l2);
            var item = new Product { Id = 5, Name = "Mouse" };

            provider.Put(item, item);

            l1.Get(item).Should().BeSameAs(item);
            l2.Get(item).Should().BeSameAs(item);
        }

        [Fact]
        public void Composite_ReadThrough_PromotesL2IntoL1()
        {
            var l1 = new InMemoryCacheProvider();
            var l2 = new InMemoryCacheProvider();
            var provider = new CompositeCacheProvider(l1, l2);
            var item = new Product { Id = 6, Name = "Teclado" };
            l2.Put(item, item);

            var result = provider.Get(item);

            result.Should().BeSameAs(item);
            l1.Get(item).Should().BeSameAs(item); // promovido
        }

        [Fact]
        public void Composite_Del_PropagatesToBothLayers()
        {
            var l1 = new InMemoryCacheProvider();
            var l2 = new InMemoryCacheProvider();
            var provider = new CompositeCacheProvider(l1, l2);
            var item = new Product { Id = 8, Name = "Monitor" };
            l1.Put(item, item);
            l2.Put(item, item);

            provider.Del(item);

            l1.Get(item).Should().BeNull();
            l2.Get(item).Should().BeNull();
        }

        [Fact]
        public void Composite_Get_L1Hit_DoesNotTouchL2()
        {
            var l1 = new InMemoryCacheProvider();
            var l2 = new InMemoryCacheProvider();
            var provider = new CompositeCacheProvider(l1, l2);
            var item = new Product { Id = 12, Name = "Mousepad" };
            l1.Put(item, item);

            var result = provider.Get(item);

            result.Should().BeSameAs(item);
            l2.Get(item).Should().BeNull(); // não foi promovido nem lido da L2
        }

        [Fact]
        public void Composite_Get_MissInBoth_ReturnsNull()
        {
            var provider = new CompositeCacheProvider(new InMemoryCacheProvider(), new InMemoryCacheProvider());

            provider.Get(new Product { Id = 404 }).Should().BeNull();
        }

        [Fact]
        public void Composite_Clear_EmptiesBothLayers()
        {
            var l1 = new InMemoryCacheProvider();
            var l2 = new InMemoryCacheProvider();
            var provider = new CompositeCacheProvider(l1, l2);
            var item = new Product { Id = 13, Name = "Monitor 2" };
            l1.Put(item, item);
            l2.Put(item, item);

            provider.Clear();

            l1.Get(item).Should().BeNull();
            l2.Get(item).Should().BeNull();
        }

        [Fact]
        public void Composite_NullLayer_Throws()
        {
            Action l1 = () => new CompositeCacheProvider(null, new InMemoryCacheProvider());
            l1.Should().Throw<ArgumentNullException>();

            Action l2 = () => new CompositeCacheProvider(new InMemoryCacheProvider(), null);
            l2.Should().Throw<ArgumentNullException>();
        }

        // ── PersistenceChannelCacheProvider ─────────────────────────────

        [Fact]
        public async Task PersistenceChannel_Put_PublishesPutMessage()
        {
            using var provider = new PersistenceChannelCacheProvider(new InMemoryCacheProvider());
            var item = new Product { Id = 1, Name = "Caderno" };
            var received = new List<PersistenceChannelCacheProvider.ChannelMessage>();

            using var cts = new CancellationTokenSource();
            var consumer = ConsumeUntilAsync(provider, received, 1, cts.Token);

            provider.Put(item, item);

            await consumer;
            cts.Cancel();

            received.Should().HaveCount(1);
            received[0].Action.Should().Be(PersistenceChannelCacheProvider.ChannelAction.Put);
            received[0].CacheItem.Should().BeSameAs(item);
        }

        [Fact]
        public async Task PersistenceChannel_DelAndClear_PublishTheirActions()
        {
            using var provider = new PersistenceChannelCacheProvider(new InMemoryCacheProvider());
            var received = new List<PersistenceChannelCacheProvider.ChannelMessage>();

            using var cts = new CancellationTokenSource();
            var consumer = ConsumeUntilAsync(provider, received, 3, cts.Token);

            provider.Put(new Product { Id = 1 }, new Product { Id = 1 });
            provider.Del(new Product { Id = 1 });
            provider.Clear();

            await consumer;
            cts.Cancel();

            received.Select(m => m.Action).Should().BeEquivalentTo(
                new[]
                {
                    PersistenceChannelCacheProvider.ChannelAction.Put,
                    PersistenceChannelCacheProvider.ChannelAction.Del,
                    PersistenceChannelCacheProvider.ChannelAction.Clear
                });
        }

        [Fact]
        public async Task PersistenceChannel_FanOut_EveryConsumerGetsACopy()
        {
            using var provider = new PersistenceChannelCacheProvider(new InMemoryCacheProvider());
            var a = new List<PersistenceChannelCacheProvider.ChannelMessage>();
            var b = new List<PersistenceChannelCacheProvider.ChannelMessage>();

            using var cts = new CancellationTokenSource();
            var consumerA = ConsumeUntilAsync(provider, a, 1, cts.Token);
            var consumerB = ConsumeUntilAsync(provider, b, 1, cts.Token);

            provider.Put(new Product { Id = 2 }, new Product { Id = 2 });

            await consumerA;
            await consumerB;
            cts.Cancel();

            a.Should().HaveCount(1);
            b.Should().HaveCount(1);
        }

        [Fact]
        public void PersistenceChannel_Get_ReadsFromInnerCache()
        {
            using var provider = new PersistenceChannelCacheProvider(new InMemoryCacheProvider());
            var item = new Product { Id = 4, Name = "Borracha" };

            provider.Put(item, item);

            provider.Get(item).Should().BeSameAs(item);
        }

        [Fact]
        public void PersistenceChannel_NullInnerProvider_Throws()
        {
            Action act = () => new PersistenceChannelCacheProvider(null);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public async Task PersistenceChannel_UnboundedSubscribe_ReceivesEvents()
        {
            using var provider = new PersistenceChannelCacheProvider(new InMemoryCacheProvider());
            var reader = provider.Subscribe(capacity: 0);
            var received = new List<PersistenceChannelCacheProvider.ChannelMessage>();

            provider.Put(new Product { Id = 1 }, new Product { Id = 1 });

            await foreach (var msg in reader.ReadAllAsync())
            {
                received.Add(msg);
                break;
            }

            received.Should().ContainSingle();
            received[0].Action.Should().Be(PersistenceChannelCacheProvider.ChannelAction.Put);
        }

        [Fact]
        public async Task PersistenceChannel_Consume_CancellationExitsCleanly()
        {
            var provider = new PersistenceChannelCacheProvider(new InMemoryCacheProvider());
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var task = Task.Run(async () =>
            {
                await foreach (var _ in provider.Consume()) { }
                completed.TrySetResult(true);
            });

            await Task.Delay(100); // deixa o loop pendurado em ReadAsync
            provider.Dispose();    // cancela o token interno -> OperationCanceledException -> yield break

            (await Task.WhenAny(completed.Task, Task.Delay(5000)))
                .Should().Be(completed.Task, "Consume deveria encerrar ao cancelar o provider");
        }

        private static async Task ConsumeUntilAsync(
            PersistenceChannelCacheProvider provider,
            List<PersistenceChannelCacheProvider.ChannelMessage> sink,
            int count,
            CancellationToken token)
        {
            await foreach (var msg in provider.Consume(token))
            {
                sink.Add(msg);
                if (sink.Count >= count)
                    return;
            }
        }

        // ── DataCache (fachada) ─────────────────────────────────────────

        [Fact]
        public void DataCache_Uninitialized_Throws()
        {
            DataCache.Initialize(null);
            DataCache.DefaultProvider.Should().BeNull();

            Action act = () => DataCache.Put(new Product { Id = 1 }, new Product { Id = 1 });
            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void DataCache_Initialized_RoutesToProvider()
        {
            DataCache.Initialize(new InMemoryCacheProvider());
            var item = new Product { Id = 11, Name = "Apontador" };

            DataCache.Put(item, item);

            DataCache.Get(item).Should().BeSameAs(item);
        }

        [Fact]
        public void DataCache_InitializeWithMemoryLimit_CreatesInMemoryProvider()
        {
            DataCache.Initialize(memorySizeLimit: 100);

            DataCache.DefaultProvider.Should().BeOfType<InMemoryCacheProvider>();
        }

        [Fact]
        public void DataCache_Del_RemovesItemFromProvider()
        {
            DataCache.Initialize(new InMemoryCacheProvider());
            var item = new Product { Id = 21, Name = "Lapis" };

            DataCache.Put(item, item);
            DataCache.Del(item);

            DataCache.Get(item).Should().BeNull();
        }

        [Fact]
        public void DataCache_Clear_EmptiesProvider()
        {
            DataCache.Initialize(new InMemoryCacheProvider());
            var item = new Product { Id = 22, Name = "Regua" };

            DataCache.Put(item, item);
            DataCache.Clear();

            DataCache.Get(item).Should().BeNull();
        }

        [Fact]
        public void DataCache_GetAndDelAndClear_Uninitialized_Throw()
        {
            DataCache.Initialize(null);

            Action get = () => DataCache.Get(new Product { Id = 1 });
            Action del = () => DataCache.Del(new Product { Id = 1 });
            Action clear = () => DataCache.Clear();

            get.Should().Throw<InvalidOperationException>();
            del.Should().Throw<InvalidOperationException>();
            clear.Should().Throw<InvalidOperationException>();
        }

        // ── CacheableAttribute ──────────────────────────────────────────

        [Fact]
        public void CacheableAttribute_ValidProvider_StoresType()
        {
            var attr = new CacheableAttribute(typeof(InMemoryCacheProvider));

            attr.CacheProviderType.Should().Be(typeof(InMemoryCacheProvider));
        }

        [Fact]
        public void CacheableAttribute_NonProviderType_Throws()
        {
            Action act = () => new CacheableAttribute(typeof(string));

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void CacheableAttribute_NullType_Throws()
        {
            Action act = () => new CacheableAttribute(null);

            act.Should().Throw<ArgumentNullException>();
        }

        // ── Fake IDistributedCache para testes ──────────────────────────

        private class FakeDistributedCache : IDistributedCache
        {
            private readonly ConcurrentDictionary<string, byte[]> _store = new();

            public byte[] Get(string key) => _store.TryGetValue(key, out var v) ? v : null;

            public Task<byte[]> GetAsync(string key, CancellationToken token = default)
                => Task.FromResult(Get(key));

            public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
                => _store[key] = value;

            public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
                CancellationToken token = default)
            {
                Set(key, value, options);
                return Task.CompletedTask;
            }

            public void Refresh(string key) { }

            public Task RefreshAsync(string key, CancellationToken token = default)
                => Task.CompletedTask;

            public void Remove(string key) => _store.TryRemove(key, out _);

            public Task RemoveAsync(string key, CancellationToken token = default)
            {
                Remove(key);
                return Task.CompletedTask;
            }
        }
    }
}
