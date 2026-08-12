using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rochas.CacheIndexer.Core;
using Rochas.CacheIndexer.Enumerators;
using Rochas.CacheIndexer.Helpers;

namespace Rochas.CacheIndexer
{
    /// <summary>
    /// Índice léxico em memória com cache de hashes, segregação por segmento
    /// e features opcionais de normalização:
    /// <list type="bullet">
    /// <item><description>Sinônimos (dicionário pt-BR embarcado ou customizado);</description></item>
    /// <item><description>Stemming (Stemmer de Porter PT-BR);</description></item>
    /// <item><description>Filtro fonético (Soundex PT-BR).</description></item>
    /// </list>
    /// </summary>
    public class CacheIndexer
    {
        private readonly LexicalIndexEngine _engine;
        private readonly double _minMatchScore;

        public CacheIndexer() : this(null)
        {
        }

        public CacheIndexer(CacheIndexerConfig config = null)
        {
            _engine = new LexicalIndexEngine(config);
            _minMatchScore = config?.MinMatchScore ?? 0.3;
        }

        /// <summary>Radicaliza termos (Stemmer de Porter PT-BR) antes de hashear.</summary>
        public bool EnableStemming
        {
            get => _engine.EnableStemming;
            set => _engine.EnableStemming = value;
        }

        /// <summary>Adiciona hash fonético (Soundex PT-BR) por termo.</summary>
        public bool EnablePhoneticFilter
        {
            get => _engine.EnablePhoneticFilter;
            set => _engine.EnablePhoneticFilter = value;
        }

        /// <summary>Expande termos via dicionário de sinônimos.</summary>
        public bool EnableSynonyms
        {
            get => _engine.EnableSynonyms;
            set
            {
                _engine.EnableSynonyms = value;
                if (value) _engine.RefreshSynonyms();
            }
        }

        public bool IsAvailable => _engine.IsAvailable;

        public bool IsCacheExpired => _engine.IsCacheExpired;

        /// <summary>
        /// Liga/desliga as features de normalização de uma vez via flag enum.
        /// </summary>
        public void SetFeatures(CacheIndexerFeature features)
        {
            EnableSynonyms = features.HasFlag(CacheIndexerFeature.Synonyms);
            EnableStemming = features.HasFlag(CacheIndexerFeature.Stemming);
            EnablePhoneticFilter = features.HasFlag(CacheIndexerFeature.Phonetic);
        }

        public CacheIndexerFeature GetFeatures()
        {
            var flags = CacheIndexerFeature.None;
            if (EnableSynonyms) flags |= CacheIndexerFeature.Synonyms;
            if (EnableStemming) flags |= CacheIndexerFeature.Stemming;
            if (EnablePhoneticFilter) flags |= CacheIndexerFeature.Phonetic;
            return flags;
        }

        /// <summary>Limpa o índice em memória, forçando reindexação no próximo uso.</summary>
        public void InvalidateIndex()
        {
            _engine.InvalidateIndex();
        }

        /// <summary>
        /// Carrega (ou recarrega) o índice a partir dos documentos fornecidos.
        /// No-op se o índice já estiver carregado e não expirado.
        /// </summary>
        public Task EnsureIndexLoadedAsync(Func<Task<IReadOnlyList<IndexedDocument>>> loadDocumentsFunc)
        {
            if (loadDocumentsFunc == null)
                throw new ArgumentNullException(nameof(loadDocumentsFunc));

            return _engine.EnsureIndexLoadedAsync(loadDocumentsFunc);
        }

        /// <summary>
        /// Tokeniza e hasheia um par título/corpo usando as features atuais.
        /// </summary>
        public TextHashResult ProcessText(string title, string body = null)
        {
            return _engine.ProcessText(title, body);
        }

        /// <summary>Extrai hashes de um texto usando as features atuais.</summary>
        public uint[] ExtractHashes(string text)
        {
            return _engine.ExtractHashes(text);
        }

        /// <summary>
        /// Extrai hashes de um texto, permitindo ligar/desligar cada feature
        /// pontualmente para esta chamada.
        /// </summary>
        public uint[] ExtractHashes(string text, bool useSynonyms, bool useStemming, bool useSoundex)
        {
            return _engine.ExtractHashes(text, useSynonyms, useStemming, useSoundex);
        }

        /// <summary>
        /// Busca no índice (segundo maior score, escopado por segmento quando informado).
        /// Título casado conta como <c>TitleWeight</c>, corpo como <c>BodyWeight</c>.
        /// </summary>
        public IndexSearchResult SearchIndex(uint[] queryHashes, double minMatchScore = 0.3, int? segmentId = null)
        {
            return _engine.SearchIndex(queryHashes, minMatchScore, segmentId);
        }

        /// <summary>
        /// Busca direta sobre uma coleção de documentos (sem indexação prévia).
        /// </summary>
        public IndexSearchResult Search(IEnumerable<IndexedDocument> documents, uint[] queryHashes, double minMatchScore = 0.3)
        {
            return _engine.Search(documents, queryHashes, minMatchScore);
        }

        /// <summary>
        /// Busca progressiva em 4 camadas (tiers), da mais precisa para a mais
        /// permissiva: base -&gt; sinônimos -&gt; stemming -&gt; soundex.
        /// Retorna o primeiro hit encontrado.
        /// </summary>
        public async Task<IndexSearchResult> FindBestMatchAsync(
            string message,
            Func<Task<IReadOnlyList<IndexedDocument>>> loadDocumentsFunc,
            int? segmentId = null)
        {
            if (string.IsNullOrWhiteSpace(message))
                return IndexSearchResult.Empty;

            await EnsureIndexLoadedAsync(loadDocumentsFunc);

            var tiers = new (bool Synonyms, bool Stemming, bool Soundex, string Label)[]
            {
                (false, false, false, "base"),
                (true,  false, false, "synonyms"),
                (true,  true,  false, "stemming"),
                (true,  true,  true,  "soundex")
            };

            foreach (var (syn, stem, sx, label) in tiers)
            {
                var queryHashes = _engine.ExtractHashes(message, syn, stem, sx);
                if (queryHashes.Length == 0) continue;

                var result = _engine.SearchIndex(queryHashes, _minMatchScore, segmentId);
                if (result.Found)
                {
                    result.Tier = label;
                    return result;
                }
            }

            return IndexSearchResult.Empty;
        }
    }
}
