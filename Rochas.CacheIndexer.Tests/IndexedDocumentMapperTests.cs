using System.Threading.Tasks;
using FluentAssertions;
using Rochas.CacheIndexer.Helpers;
using Rochas.Data.Specification.Annotations;
using Rochas.Data.Specification.Enums;
using Xunit;

namespace Rochas.CacheIndexer.Tests
{
    public class IndexedDocumentMapperTests
    {
        private class Product
        {
            [Indexable(Section = IndexSection.Title)]
            public string Name { get; set; }

            [Indexable(Section = IndexSection.Title)]
            public string Brand { get; set; }

            [Indexable(Section = IndexSection.Body)]
            public string Description { get; set; }

            public decimal Price { get; set; }
        }

        [Fact]
        public void Build_RespectsSectionAnnotation()
        {
            var entity = new Product
            {
                Name = "Caneta Esferográfica",
                Brand = "Bic",
                Description = "Escrita azul, ponta fina",
                Price = 1.50m
            };

            var doc = IndexedDocumentMapper.Build(entity, id: 7);

            doc.Id.Should().Be(7);
            doc.Title.Should().Be("Caneta Esferográfica Bic");
            doc.Body.Should().Be("Escrita azul, ponta fina");
        }

        [Fact]
        public void Build_IgnoresNonAnnotatedColumns()
        {
            var entity = new Product
            {
                Name = "Caderno",
                Description = "96 folhas",
                Price = 12.90m
            };

            var doc = IndexedDocumentMapper.Build(entity);

            doc.Title.Should().Be("Caderno");
            doc.Body.Should().Be("96 folhas");
            doc.Title.Should().NotContain("12,90");
            doc.Body.Should().NotContain("12,90");
        }

        [Fact]
        public async Task Index_OnlyConsidersAnnotatedColumns()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };
            var entity = new Product
            {
                Name = "fatura",
                Brand = "crédito",
                Description = null,
                Price = 999.99m
            };

            var doc = IndexedDocumentMapper.Build(entity, id: 1);
            var docs = new[] { doc };

            await indexer.EnsureIndexLoaded(() => Task.FromResult<System.Collections.Generic.IReadOnlyList<IndexedDocument>>(docs));

            // "999,99" não está anotado -> não deve ser encontrável no índice
            var missingHashes = indexer.ExtractHashes("999,99", false, false, false);
            missingHashes.Should().NotBeEmpty();
            var missingMatch = indexer.SearchIndex(missingHashes, minMatchScore: 0.9);
            missingMatch.Found.Should().BeFalse();

            var match = await indexer.FindBestMatch("fatura", () => Task.FromResult<System.Collections.Generic.IReadOnlyList<IndexedDocument>>(docs));
            match.Found.Should().BeTrue();
            match.BestId.Should().Be(1);
        }

        [Fact]
        public void ExtractIndexedStrings_ReturnsOnlyTargetSection()
        {
            var entity = new Product
            {
                Name = "Borracha",
                Brand = "Faber",
                Description = "Branca",
                Price = 2.0m
            };

            var titles = IndexedDocumentMapper.ExtractIndexedStrings(entity, IndexSection.Title);
            var bodies = IndexedDocumentMapper.ExtractIndexedStrings(entity, IndexSection.Body);

            titles.Should().Equal("Borracha", "Faber");
            bodies.Should().Equal("Branca");
        }
    }
}