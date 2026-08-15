using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Rochas.CacheIndexer.Enumerators;
using Rochas.CacheIndexer.Helpers;
using Xunit;

namespace Rochas.CacheIndexer.Tests
{
    /// <summary>
    /// Cobre caminhos de borda e branchs ainda não exercitados: argumentos
    /// inválidos, busca por corpo, segmento inexistente, hashes zerados,
    /// dicionário de sinônimos customizado e fallback de leitura.
    /// </summary>
    public class EdgeCaseCoverageTests
    {
        private static uint Hash(string word, CacheIndexer indexer) =>
            indexer.ExtractHashes(word, useSynonyms: false, useStemming: false, useSoundex: false).First();

        // ── API pública: argumentos inválidos ─────────────────────────

        [Fact]
        public async Task EnsureIndexLoaded_NullLoader_ThrowsArgumentNull()
        {
            var indexer = new CacheIndexer();

            Func<Task> act = () => indexer.EnsureIndexLoaded(null);

            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task FindBestMatch_WhiteSpaceMessage_ReturnsEmpty()
        {
            var indexer = new CacheIndexer();
            var docs = new List<IndexedDocument>();

            var result = await indexer.FindBestMatch("   ", () => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            result.Found.Should().BeFalse();
        }

        [Fact]
        public void IsAvailable_IsTrueAfterConstruction()
        {
            new CacheIndexer().IsAvailable.Should().BeTrue();
        }

        // ── FindBestMatch: tiers base e synonyms ──────────────────────

        [Fact]
        public async Task FindBestMatch_BaseTier_FindsExactWord()
        {
            var indexer = new CacheIndexer();
            indexer.SetFeatures(CacheIndexerFeature.None);
            var docs = new List<IndexedDocument> { new IndexedDocument { Id = 1, Title = "fatura" } };

            var result = await indexer.FindBestMatch("fatura", () => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            result.Found.Should().BeTrue();
            result.Tier.Should().Be("base");
        }

        [Fact]
        public async Task FindBestMatch_SynonymsTier_FindsSynonymMatch()
        {
            var indexer = new CacheIndexer(new CacheIndexerConfig { MinMatchScore = 0.1 });
            indexer.EnableSynonyms = false; // índice construído sem expansão

            var docs = new List<IndexedDocument> { new IndexedDocument { Id = 1, Title = "fatura" } };
            await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            indexer.EnableSynonyms = true; // dicionário religado no runtime -> tier synonyms

            var result = await indexer.FindBestMatch("boleto", () => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            result.Found.Should().BeTrue();
            result.Tier.Should().Be("synonyms");
        }

        // ── SearchIndex: corpo, segmento ausente e hashes zerados ─────

        [Fact]
        public async Task SearchIndex_MatchesHashInBody()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };
            var tokenHash = Hash("fatura", indexer);
            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, BodyHashCodes = new[] { tokenHash } }
            };

            await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            var result = indexer.SearchIndex(new[] { tokenHash }, 0.3);

            result.Found.Should().BeTrue();
            result.BestId.Should().Be(1);
            result.Score.Should().Be(1.0);
        }

        [Fact]
        public async Task SearchIndex_UnknownSegment_ReturnsEmpty()
        {
            var indexer = new CacheIndexer { EnableSynonyms = true };
            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, SegmentId = 10, Title = "fatura" }
            };

            await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            var query = indexer.ExtractHashes("fatura");

            indexer.SearchIndex(query, 0.3, segmentId: 99).Found.Should().BeFalse();
        }

        [Fact]
        public async Task SearchIndex_ZeroHashesAreSkippedWhenIndexing()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };
            var tokenHash = Hash("fatura", indexer);
            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new uint[] { 0, tokenHash } }
            };

            await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            var result = indexer.SearchIndex(new[] { tokenHash }, 0.3);

            result.BestId.Should().Be(1);
            result.Score.Should().Be(1.0);
        }

        [Fact]
        public async Task SearchIndex_PrecomputedRawHash_ExpandsSynonymsAtIndexing()
        {
            var indexer = new CacheIndexer { EnableSynonyms = true };
            var faturaHash = Hash("fatura", indexer);
            var boletoHash = Hash("boleto", indexer);
            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { faturaHash } }
            };

            await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            var result = indexer.SearchIndex(new[] { boletoHash }, 0.3);

            result.Found.Should().BeTrue();
            result.BestId.Should().Be(1); // hash pré-computado expandido no indexing
        }

        // ── SearchIndex / Search: entradas inválidas ──────────────────

        [Fact]
        public void SearchIndex_NullQuery_ReturnsEmpty()
        {
            var indexer = new CacheIndexer();

            indexer.SearchIndex(null, 0.3).Found.Should().BeFalse();
        }

        [Fact]
        public void Search_NullDocuments_ReturnsEmpty()
        {
            var indexer = new CacheIndexer();
            var h = Hash("fatura", indexer);

            indexer.Search(null, new[] { h }, 0.3).Found.Should().BeFalse();
        }

        [Fact]
        public void Search_NoActiveDocumentsWithHashes_ReturnsEmpty()
        {
            var indexer = new CacheIndexer();
            var h = Hash("fatura", indexer);
            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new uint[0] },
                new IndexedDocument { Id = 2 }
            };

            indexer.Search(docs, new[] { h }, 0.3).Found.Should().BeFalse();
        }

        // ── ExtractHashes / ProcessText: bordas ───────────────────────

        [Fact]
        public void ExtractHashes_WhiteSpace_ReturnsEmpty()
        {
            var indexer = new CacheIndexer();

            indexer.ExtractHashes("   ").Should().BeEmpty();
        }

        [Fact]
        public void ProcessText_NullTitleAndBody_ReturnsEmptyResult()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var result = indexer.ProcessText(null, null, documentId: 1);

            result.TitleKeywords.Should().BeEmpty();
            result.BodyKeywords.Should().BeEmpty();
            result.TitleHashCodes.Should().BeEmpty();
            result.BodyHashCodes.Should().BeEmpty();
        }

        [Fact]
        public void ProcessText_ExplicitDocumentId_DeduplicatesHashes()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var result = indexer.ProcessText("fatura fatura fatura", null, documentId: 7);

            result.TitleKeywords.Should().Be("fatura");
            result.TitleHashCodes.Should().ContainSingle();
        }

        // ── Config: Features conveniência ─────────────────────────────

        [Fact]
        public void Config_Features_GetSetRoundTrip()
        {
            var config = new CacheIndexerConfig();

            config.Features = CacheIndexerFeature.Synonyms | CacheIndexerFeature.Phonetic;
            config.EnableSynonyms.Should().BeTrue();
            config.EnableStemming.Should().BeFalse();
            config.EnablePhoneticFilter.Should().BeTrue();
            config.Features.Should().Be(CacheIndexerFeature.Synonyms | CacheIndexerFeature.Phonetic);

            config.Features = CacheIndexerFeature.None;
            config.EnableSynonyms.Should().BeFalse();
            config.Features.Should().Be(CacheIndexerFeature.None);
        }

        // ── Dicionário de sinônimos customizado ───────────────────────

        [Fact]
        public void Synonyms_CustomFile_ExpandsTerms()
        {
            var file = Path.Combine(Path.GetTempPath(), $"syn_{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(file,
                    "{\"girassol\": [\"hortensia\"],\"jasmim\":[\"margarida\"]}");

                var indexer = new CacheIndexer(new CacheIndexerConfig
                {
                    EnableSynonyms = true,
                    EnableStemming = false,
                    SynonymsFilePath = file
                });

                var girassolHashes = indexer.ExtractHashes("girassol", true, false, false);
                var hortensiaHash = indexer.ExtractHashes("hortensia", false, false, false).First();

                girassolHashes.Should().Contain(hortensiaHash);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void Synonyms_NoEmbeddedAndNoFile_UsesEmptyDictionary()
        {
            var indexer = new CacheIndexer(new CacheIndexerConfig
            {
                EnableSynonyms = true,
                LoadEmbeddedSynonyms = false,
                SynonymsFilePath = null
            });

            var faturaHashes = indexer.ExtractHashes("fatura", true, false, false);
            var boletoHash = indexer.ExtractHashes("boleto", false, false, false).First();

            faturaHashes.Should().NotContain(boletoHash);
        }

        [Fact]
        public void Synonyms_MissingCustomFile_FallsBackToEmbedded()
        {
            var indexer = new CacheIndexer(new CacheIndexerConfig
            {
                EnableSynonyms = true,
                SynonymsFilePath = @"Z:\inexistente\synonyms.json"
            });

            var faturaHashes = indexer.ExtractHashes("fatura", true, false, false);
            var boletoHash = indexer.ExtractHashes("boleto", false, false, false).First();

            faturaHashes.Should().Contain(boletoHash);
        }
    }
}