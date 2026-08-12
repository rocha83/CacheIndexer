using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Rochas.CacheIndexer.Enumerators;
using Rochas.CacheIndexer.Helpers;
using Xunit;

namespace Rochas.CacheIndexer.Tests
{
    public class CacheIndexerTests
    {
        // ── Toggles: estado default ────────────────────────────────────

        [Fact]
        public void DefaultFeatures_OnlySynonymsEnabled()
        {
            var indexer = new CacheIndexer();
            indexer.EnableSynonyms.Should().BeTrue();
            indexer.EnableStemming.Should().BeFalse();
            indexer.EnablePhoneticFilter.Should().BeFalse();
            indexer.GetFeatures().Should().Be(CacheIndexerFeature.Synonyms);
        }

        [Fact]
        public void SetFeatures_FlagsToggleEveryBoolean()
        {
            var indexer = new CacheIndexer();

            indexer.SetFeatures(CacheIndexerFeature.All);
            indexer.EnableSynonyms.Should().BeTrue();
            indexer.EnableStemming.Should().BeTrue();
            indexer.EnablePhoneticFilter.Should().BeTrue();

            indexer.SetFeatures(CacheIndexerFeature.None);
            indexer.EnableSynonyms.Should().BeFalse();
            indexer.EnableStemming.Should().BeFalse();
            indexer.EnablePhoneticFilter.Should().BeFalse();

            indexer.SetFeatures(CacheIndexerFeature.Phonetic | CacheIndexerFeature.Stemming);
            indexer.GetFeatures().Should().Be(CacheIndexerFeature.Phonetic | CacheIndexerFeature.Stemming);
        }

        [Fact]
        public void Config_RespectsInitialToggles()
        {
            var config = new CacheIndexerConfig
            {
                EnableSynonyms = false,
                EnableStemming = true,
                EnablePhoneticFilter = true
            };

            var indexer = new CacheIndexer(config);
            indexer.EnableSynonyms.Should().BeFalse();
            indexer.EnableStemming.Should().BeTrue();
            indexer.EnablePhoneticFilter.Should().BeTrue();
        }

        // ── Toggle: Stemming ───────────────────────────────────────────

        [Fact]
        public void Stemming_On_MatchesInflectedWords()
        {
            var indexer = new CacheIndexer { EnableStemming = true };

            var pluralHashes = indexer.ExtractHashes("pagamentos", useSynonyms: false, useStemming: true, useSoundex: false);
            var singularHashes = indexer.ExtractHashes("pagamento", useSynonyms: false, useStemming: true, useSoundex: false);

            pluralHashes.Should().NotBeEmpty();
            pluralHashes.Should().Contain(singularHashes); // ambos viram o mesmo radical
        }

        [Fact]
        public void Stemming_Off_InflectedWordsDiverge()
        {
            var indexer = new CacheIndexer { EnableStemming = false };

            var pluralHashes = indexer.ExtractHashes("pagamentos", useSynonyms: false, useStemming: false, useSoundex: false);
            var singularHashes = indexer.ExtractHashes("pagamento", useSynonyms: false, useStemming: false, useSoundex: false);

            pluralHashes.Should().NotContain(singularHashes);
        }

        // ── Toggle: Sinônimos ──────────────────────────────────────────

        [Fact]
        public void Synonyms_On_ExpandsDictionaryTerms()
        {
            var indexer = new CacheIndexer { EnableSynonyms = true };

            var faturaHashes = indexer.ExtractHashes("fatura", useSynonyms: true, useStemming: false, useSoundex: false);
            var boletoHash = indexer.ExtractHashes("boleto", useSynonyms: false, useStemming: false, useSoundex: false).First();

            faturaHashes.Should().Contain(boletoHash); // "fatura" expande para "boleto"
        }

        [Fact]
        public void Synonyms_Off_DoesNotExpandDictionaryTerms()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var faturaHashes = indexer.ExtractHashes("fatura", useSynonyms: false, useStemming: false, useSoundex: false);
            var boletoHash = indexer.ExtractHashes("boleto", useSynonyms: false, useStemming: false, useSoundex: false).First();

            faturaHashes.Should().NotContain(boletoHash);
        }

        // ── Toggle: Soundex fonético ───────────────────────────────────

        [Fact]
        public void Phonetic_On_SameSoundWordsShareHash()
        {
            var indexer = new CacheIndexer { EnablePhoneticFilter = true };

            var casaHashes = indexer.ExtractHashes("casa", useSynonyms: false, useStemming: false, useSoundex: true);
            var cazaHashes = indexer.ExtractHashes("caza", useSynonyms: false, useStemming: false, useSoundex: true);

            // "casa" e "caza" produzem o mesmo código Soundex -> hashes fonéticos compartilhados
            casaHashes.Should().IntersectWith(cazaHashes);
        }

        [Fact]
        public void Phonetic_Off_DifferentSpellingsDiverge()
        {
            var indexer = new CacheIndexer { EnablePhoneticFilter = false };

            var casaHashes = indexer.ExtractHashes("casa", useSynonyms: false, useStemming: false, useSoundex: false);
            var cazaHashes = indexer.ExtractHashes("caza", useSynonyms: false, useStemming: false, useSoundex: false);

            casaHashes.Should().NotIntersectWith(cazaHashes);
        }

        // ── Pipeline: índice + busca progressiva ───────────────────────

        [Fact]
        public async Task FindBestMatch_WithSynonymsEnabled_FindsSynonym()
        {
            var indexer = new CacheIndexer { EnableSynonyms = true };
            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, Title = "fatura", Body = null }
            };

            await indexer.EnsureIndexLoadedAsync(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            var result = await indexer.FindBestMatchAsync("boleto", () => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));
            result.Found.Should().BeTrue();
            result.BestId.Should().Be(1);
            result.Score.Should().BeGreaterThan(0.9);
        }

        [Fact]
        public async Task FindBestMatch_WithSynonymsDisabled_DoesNotFindSynonym()
        {
            var indexer = new CacheIndexer();
            indexer.SetFeatures(CacheIndexerFeature.None);

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, Title = "fatura", Body = null }
            };

            await indexer.EnsureIndexLoadedAsync(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            var result = await indexer.FindBestMatchAsync("boleto", () => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));
            result.Found.Should().BeFalse();
        }

        [Fact]
        public async Task SearchIndex_RespectsSegmentScoping()
        {
            var indexer = new CacheIndexer { EnableSynonyms = true };
            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, SegmentId = 10, Title = "fatura" },
                new IndexedDocument { Id = 2, SegmentId = 20, Title = "fatura" }
            };

            await indexer.EnsureIndexLoadedAsync(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            var query = indexer.ExtractHashes("fatura");
            var scoped = indexer.SearchIndex(query, minMatchScore: 0.9, segmentId: 20);
            scoped.Found.Should().BeTrue();
            scoped.BestId.Should().Be(2);

            var unscoped = indexer.SearchIndex(query, minMatchScore: 0.9);
            unscoped.Found.Should().BeTrue();
        }

        [Fact]
        public void Search_OverPrecomputedHashes_TieBrokenById()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };
            var tokenHash = indexer.ExtractHashes("fatura", false, false, false).First();

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { tokenHash } },
                new IndexedDocument { Id = 2, BodyHashCodes = new[] { tokenHash } }
            };

            var query = new[] { tokenHash };
            var result = indexer.Search(docs, query, minMatchScore: 0.9);

            result.Found.Should().BeTrue();
            result.BestId.Should().Be(1); // cobertura/IDF iguais -> desempate por Id
        }

        // ── Relevância: cobertura de palavras (max de termos) ────────────

        [Fact]
        public void Search_CoveragePrimary_WinsByMoreMatchedWords()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var a = indexer.ExtractHashes("fatura", false, false, false).First();
            var b = indexer.ExtractHashes("boleto", false, false, false).First();
            var c = indexer.ExtractHashes("nota", false, false, false).First();

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { a } },
                new IndexedDocument { Id = 2, TitleHashCodes = new[] { a, b, c } }
            };

            var query = new[] { a, b, c };
            var result = indexer.Search(docs, query, minMatchScore: 0.3);

            result.Found.Should().BeTrue();
            result.BestId.Should().Be(2); // 3/3 palavras > 1/3
        }

        // ── Relevância: IDF (termo raro vale mais) ───────────────────────

        [Fact]
        public void Search_IdfWeightsRareWordHigher()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            // "comum" aparece em 2 docs; "raro" aparece em 1 doc
            var comum = indexer.ExtractHashes("fatura", false, false, false).First();
            var raro = indexer.ExtractHashes("cancelamento", false, false, false).First();

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { comum, raro } },
                new IndexedDocument { Id = 2, TitleHashCodes = new[] { comum } }
            };

            var query = new[] { comum, raro };
            var result = indexer.Search(docs, query, minMatchScore: 0.3);

            result.Found.Should().BeTrue();
            result.BestId.Should().Be(1); // cobre 2/2 palavras e ainda tem o termo raro
            result.Score.Should().Be(1.0);
        }

        [Fact]
        public void SearchIndex_RareWordBeatsCommonWordOnTie()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var comum = indexer.ExtractHashes("fatura", false, false, false).First();
            var raro = indexer.ExtractHashes("cancelamento", false, false, false).First();

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { raro } },
                new IndexedDocument { Id = 2, TitleHashCodes = new[] { comum } },
                new IndexedDocument { Id = 3, TitleHashCodes = new[] { comum } }
            };

            // amboso tem cobertura 1/1; o doc 1 tem o termo raro (maior IDF)
            var query = new[] { comum, raro };
            indexer.EnsureIndexLoadedAsync(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs)).GetAwaiter().GetResult();

            var result = indexer.SearchIndex(query, minMatchScore: 0.3);
            result.Found.Should().BeTrue();
            result.BestId.Should().Be(1);
        }

        // ── Relevância: cobertura vence IDF em primeiro critério ─────────

        [Fact]
        public void Search_CoverageBeatsIdf_MoreWordsWins()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var comum1 = indexer.ExtractHashes("fatura", false, false, false).First();
            var comum2 = indexer.ExtractHashes("pagamento", false, false, false).First();
            var raro = indexer.ExtractHashes("cancelamento", false, false, false).First();

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { raro } },                          // 1 palavra rara (IDF alto)
                new IndexedDocument { Id = 2, TitleHashCodes = new[] { comum1, comum2 } },               // 2 palavras comuns (IDF baixo)
                new IndexedDocument { Id = 3, TitleHashCodes = new[] { comum1, comum2 } }                // comum1/comum2 frequentes
            };

            var query = new[] { comum1, comum2, raro };
            var result = indexer.Search(docs, query, minMatchScore: 0.3);

            result.Found.Should().BeTrue();
            result.BestId.Should().Be(2); // cobertura 2/3 prevalece sobre o IDF do termo raro (1/3)
        }

        [Fact]
        public void ProcessText_ProducesKeywordsAndHashes()
        {
            var indexer = new CacheIndexer { EnableSynonyms = true };

            var result = indexer.ProcessText("Como emitir boleto", "Pague sua fatura no portal");

            result.TitleKeywords.Should().Be("emitir,boleto");
            result.TitleHashCodes.Should().NotBeEmpty();
            result.BodyKeywords.Should().Contain("fatura");
            result.BodyHashCodes.Should().NotBeEmpty();

            // Título deve conter o hash expandido de "boleto" (sinônimo de fatura) via dicionário
            var faturaHash = indexer.ExtractHashes("fatura", false, false, false).First();
            result.TitleHashCodes.Should().Contain(faturaHash);
        }

        [Fact]
        public async Task InvalidateIndex_ForcesReload()
        {
            var indexer = new CacheIndexer { EnableSynonyms = true };
            var docs = new List<IndexedDocument>();
            await indexer.EnsureIndexLoadedAsync(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));
            indexer.IsCacheExpired.Should().BeFalse();

            indexer.InvalidateIndex();
            indexer.IsCacheExpired.Should().BeTrue();
        }
    }
}
