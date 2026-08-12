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
    /// <summary>
    /// Cobre a acurácia do artefato: frequência em memória atualizada durante
    /// ProcessText, IDF (desempate) calculado no instante da busca e critério
    /// de cobertura de palavras como obtenção.
    /// </summary>
    public class RelevanceScoringTests
    {
        private static uint Hash(string word, CacheIndexer indexer) =>
            indexer.ExtractHashes(word, useSynonyms: false, useStemming: false, useSoundex: false).First();

        // ── Frequência em memória durante ProcessText ───────────────────

        [Fact]
        public async Task ProcessText_WithDocumentId_UpdatesInMemoryFrequency_ChangesSearchTieBreak()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var fatura = Hash("fatura", indexer);
            var cancelamento = Hash("cancelamento", indexer);

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { cancelamento } },
                new IndexedDocument { Id = 2, TitleHashCodes = new[] { fatura } },
                new IndexedDocument { Id = 3, TitleHashCodes = new[] { fatura } }
            };

            await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            var query = new[] { fatura, cancelamento };
            indexer.SearchIndex(query, 0.3).BestId.Should().Be(1); // "cancelamento" raro (df=1) vence

            // Novo documento com "cancelamento" é processado (learning): a
            // frequência em memória sobe e o IDF do termo cai no instante da busca.
            indexer.ProcessText("cancelamento", null, documentId: 99);
            indexer.ProcessText("cancelamento", null, documentId: 100);

            var after = indexer.SearchIndex(query, 0.3);
            after.BestId.Should().Be(2); // "cancelamento" agora é o mais comum (df=3, N=5)
            after.Score.Should().Be(0.5); // cobertura inalterada (1/2), só o desempate muda
        }

        [Fact]
        public async Task ProcessText_WithoutDocumentId_EachCallCountsAsDistinctDocument()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var fatura = Hash("fatura", indexer);
            var cancelamento = Hash("cancelamento", indexer);

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { cancelamento } },
                new IndexedDocument { Id = 2, TitleHashCodes = new[] { fatura } },
                new IndexedDocument { Id = 3, TitleHashCodes = new[] { fatura } }
            };

            await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            var query = new[] { fatura, cancelamento };
            indexer.SearchIndex(query, 0.3).BestId.Should().Be(1);

            indexer.ProcessText("cancelamento");
            indexer.ProcessText("cancelamento");

            indexer.SearchIndex(query, 0.3).BestId.Should().Be(2); // 2 docs implícitos a mais p/ "cancelamento"
        }

        // ── Cobertura de palavras: obtenção ─────────────────────────────

        [Fact]
        public void Search_Coverage_AllWordsMatchBeatsSingleWord()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var a = Hash("fatura", indexer);
            var b = Hash("boleto", indexer);
            var c = Hash("nota", indexer);

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { a } },
                new IndexedDocument { Id = 2, TitleHashCodes = new[] { a, b, c } }
            };

            var result = indexer.Search(docs, new[] { a, b, c }, 0.3);
            result.Found.Should().BeTrue();
            result.BestId.Should().Be(2);
            result.Score.Should().Be(1.0);
        }

        [Fact]
        public async Task SearchIndex_Coverage_TwoWordsBeatOneWord_EvenWithRareTerm()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var comum1 = Hash("fatura", indexer);
            var comum2 = Hash("pagamento", indexer);
            var raro = Hash("cancelamento", indexer);

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { raro } },
                new IndexedDocument { Id = 2, TitleHashCodes = new[] { comum1, comum2 } },
                new IndexedDocument { Id = 3, TitleHashCodes = new[] { comum1, comum2 } }
            };

            await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

            var query = new[] { comum1, comum2, raro };
            var result = indexer.SearchIndex(query, 0.3);
            result.Found.Should().BeTrue();
            result.BestId.Should().Be(2); // cobertura 2/3 > 1/3, mesmo com termo raro no doc 1
        }

        [Fact]
        public void Search_Coverage_OnlyAppliesToDistinctMatchedWords()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var a = Hash("fatura", indexer);
            var b = Hash("boleto", indexer);

            // Doc 2 repete "a" várias vezes, mas só conta 1 palavra distinta
            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { a, b } },
                new IndexedDocument { Id = 2, TitleHashCodes = new[] { a, a, a, a, a } }
            };

            var result = indexer.Search(docs, new[] { a, b }, 0.3);
            result.BestId.Should().Be(1); // 2/2 palavras vs 1/2
        }

        // ── IDF: desempate no instante da busca ─────────────────────────

        [Fact]
        public void Search_Idf_RareWordWinsTie()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var comum = Hash("fatura", indexer);
            var raro = Hash("cancelamento", indexer);

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { raro } },
                new IndexedDocument { Id = 2, TitleHashCodes = new[] { comum } },
                new IndexedDocument { Id = 3, TitleHashCodes = new[] { comum } }
            };

            var result = indexer.Search(docs, new[] { comum, raro }, 0.3);
            result.Found.Should().BeTrue();
            result.BestId.Should().Be(1); // mesma cobertura (1/2), termo raro pontua mais
        }

        [Fact]
        public void Search_Idf_CommonWordLosesTie_WhenRarePresentElsewhere()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var comum = Hash("fatura", indexer);
            var raro1 = Hash("cancelamento", indexer);
            var raro2 = Hash("faturamento", indexer);

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { raro1 } },
                new IndexedDocument { Id = 2, TitleHashCodes = new[] { comum } },
                new IndexedDocument { Id = 3, TitleHashCodes = new[] { comum } },
                new IndexedDocument { Id = 4, TitleHashCodes = new[] { raro2 } }
            };

            var query = new[] { comum, raro1 };
            var result = indexer.Search(docs, query, 0.3);
            result.BestId.Should().Be(1); // idf(raro1) > idf(comum)
        }

        // ── minMatchScore (limiar de obtenção) ──────────────────────────

        [Fact]
        public void Search_MinMatchScore_ThresholdControlsAcceptance()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var words = new[] { "fatura", "boleto", "nota", "pagar", "recibo" }
                .Select(w => Hash(w, indexer)).ToArray();

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { words[0] } }
            };

            // 1/5 palavras = cobertura 0.2
            indexer.Search(docs, words, 0.3).Found.Should().BeFalse();
            indexer.Search(docs, words, 0.2).Found.Should().BeTrue();
            indexer.Search(docs, words, 0.2).BestId.Should().Be(1);
        }

        // ── Casos de borda ─────────────────────────────────────────────

        [Fact]
        public void Search_EmptyQueryOrEmptyDocs_ReturnsEmpty()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };
            var h = Hash("fatura", indexer);

            indexer.Search(Array.Empty<IndexedDocument>(), new[] { h }, 0.3).Found.Should().BeFalse();
            indexer.Search(new[] { new IndexedDocument { Id = 1, TitleHashCodes = new[] { h } } }, Array.Empty<uint>(), 0.3).Found.Should().BeFalse();
        }

        [Fact]
        public void SearchIndex_BeforeIndexLoad_ReturnsEmpty()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };
            var h = Hash("fatura", indexer);

            indexer.SearchIndex(new[] { h }, 0.3).Found.Should().BeFalse();
        }

        [Fact]
        public async Task SearchIndex_InactiveDocumentsAreNotRanked()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };
            var h = Hash("fatura", indexer);

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, TitleHashCodes = new[] { h }, IsActive = false },
                new IndexedDocument { Id = 2, TitleHashCodes = new[] { h }, IsActive = true }
            };

            await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));
            indexer.SearchIndex(new[] { h }, 0.3).BestId.Should().Be(2);
        }

        // ── Tiers da busca progressiva ─────────────────────────────────

        [Fact]
        public async Task FindBestMatch_StemmingTier_FindsInflectedWord()
        {
            var indexer = new CacheIndexer();
            indexer.SetFeatures(CacheIndexerFeature.None);
            indexer.EnableStemming = true;

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, Title = "pagamento" }
            };

            var result = await indexer.FindBestMatch("pagamentos", () => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));
            result.Found.Should().BeTrue();
            result.BestId.Should().Be(1);
            result.Tier.Should().Be("stemming"); // base/synonyms não casam o radical
        }

        [Fact]
        public async Task FindBestMatch_SoundexTier_FindsPhoneticMatch()
        {
            var indexer = new CacheIndexer();
            indexer.SetFeatures(CacheIndexerFeature.None);
            indexer.EnablePhoneticFilter = true;

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, Title = "casa" }
            };

            var result = await indexer.FindBestMatch("caza", () => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));
            result.Found.Should().BeTrue();
            result.BestId.Should().Be(1);
            result.Tier.Should().Be("soundex"); // "caza" ~ "casa" (mesmo código fonético)
        }

        [Fact]
        public async Task FindBestMatch_NoTierMatches_ReturnsNotFound()
        {
            var indexer = new CacheIndexer();
            indexer.SetFeatures(CacheIndexerFeature.None);

            var docs = new List<IndexedDocument>
            {
                new IndexedDocument { Id = 1, Title = "casa" }
            };

            var result = await indexer.FindBestMatch("astronauta", () => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));
            result.Found.Should().BeFalse();
        }

        // ── Determinismo ───────────────────────────────────────────────

        [Fact]
        public void ExtractHashes_IsDeterministic_AcrossCalls()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var first = indexer.ExtractHashes("Quero emitir uma fatura para pagamento");
            var second = indexer.ExtractHashes("Quero emitir uma fatura para pagamento");

            first.Should().BeEquivalentTo(second);
        }

        [Fact]
        public void ProcessText_ReturnsConsistentHashesAndKeywords()
        {
            var indexer = new CacheIndexer { EnableSynonyms = false };

            var result = indexer.ProcessText("Como emitir boleto", "Pague sua fatura no portal");

            result.TitleKeywords.Should().Be("emitir,boleto");
            result.TitleHashCodes.Should().Contain(Hash("emitir", indexer));
            result.BodyHashCodes.Should().Contain(Hash("fatura", indexer));
        }
    }
}
