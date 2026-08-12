using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Rochas.CacheIndexer.Helpers;
using Rochas.Extensions;
using Rochas.PTStemmer;

namespace Rochas.CacheIndexer.Core
{
    /// <summary>
    /// Motor de índice léxico invertido em memória.
    /// Mapeia hashes uint de termos normalizados -> lista de Ids,
    /// segregado por SegmentId e separado entre campo de título e corpo.
    /// O peso de cada termo é derivado da frequência de documentos (IDF):
    /// quanto menos registros um hash aponta, maior seu peso de matching.
    /// </summary>
    internal class LexicalIndexEngine
    {
        private readonly Dictionary<uint, uint[]> _synonymMap = new Dictionary<uint, uint[]>();
        private readonly Dictionary<int, Dictionary<uint, List<int>>> _titleSegmentIndexes = new Dictionary<int, Dictionary<uint, List<int>>>();
        private readonly Dictionary<int, Dictionary<uint, List<int>>> _bodySegmentIndexes = new Dictionary<int, Dictionary<uint, List<int>>>();
        private readonly Dictionary<uint, int> _documentFrequency = new Dictionary<uint, int>();
        private readonly Dictionary<int, HashSet<uint>> _idfDocTracker = new Dictionary<int, HashSet<uint>>();
        private DateTime? _lastLoadedAt;
        private readonly object _indexLock = new object();

        public bool EnableStemming { get; set; }
        public bool EnablePhoneticFilter { get; set; }
        public bool EnableSynonyms { get; set; } = true;

        public string SynonymsFilePath { get; set; }
        public bool LoadEmbeddedSynonyms { get; set; } = true;

        public bool IsAvailable => true;

        public bool IsCacheExpired
        {
            get
            {
                lock (_indexLock)
                {
                    return !_lastLoadedAt.HasValue;
                }
            }
        }

        public LexicalIndexEngine(CacheIndexerConfig config = null)
        {
            if (config != null)
            {
                EnableStemming = config.EnableStemming;
                EnablePhoneticFilter = config.EnablePhoneticFilter;
                EnableSynonyms = config.EnableSynonyms;
                SynonymsFilePath = config.SynonymsFilePath;
                LoadEmbeddedSynonyms = config.LoadEmbeddedSynonyms;
            }

            if (EnableSynonyms)
            {
                LoadSynonyms(SynonymsFilePath);
            }
        }

        public void InvalidateIndex()
        {
            lock (_indexLock)
            {
                _titleSegmentIndexes.Clear();
                _bodySegmentIndexes.Clear();
                _documentFrequency.Clear();
                _idfDocTracker.Clear();
                _lastLoadedAt = null;
            }
        }

        /// <summary>
        /// (Re)carrega o dicionário de sinônimos. Útil ao religar a feature
        /// de sinônimos em runtime.
        /// </summary>
        public void RefreshSynonyms()
        {
            LoadSynonyms(SynonymsFilePath);
        }

        public async Task EnsureIndexLoadedAsync(Func<Task<IReadOnlyList<IndexedDocument>>> loadDocumentsFunc)
        {
            if (!IsCacheExpired) return;

            var documents = await loadDocumentsFunc();

            lock (_indexLock)
            {
                if (EnableSynonyms)
                {
                    LoadSynonyms(SynonymsFilePath);
                }

                _titleSegmentIndexes.Clear();
                _bodySegmentIndexes.Clear();
                _documentFrequency.Clear();

                foreach (var doc in documents)
                {
                    if (!doc.IsActive) continue;

                    int segKey = doc.SegmentId ?? 0;

                    var expandedTitleHashes = ExpandTokensOrHashes(doc.Title, doc.TitleHashCodes);
                    if (expandedTitleHashes.Count > 0)
                    {
                        if (!_titleSegmentIndexes.TryGetValue(segKey, out var titleIndex))
                        {
                            titleIndex = new Dictionary<uint, List<int>>();
                            _titleSegmentIndexes[segKey] = titleIndex;
                        }
                        AddHashesToIndex(titleIndex, expandedTitleHashes, doc.Id);
                        CountDocumentFrequency(expandedTitleHashes, doc.Id);
                    }

                    var expandedBodyHashes = ExpandTokensOrHashes(doc.Body, doc.BodyHashCodes);
                    if (expandedBodyHashes.Count > 0)
                    {
                        if (!_bodySegmentIndexes.TryGetValue(segKey, out var bodyIndex))
                        {
                            bodyIndex = new Dictionary<uint, List<int>>();
                            _bodySegmentIndexes[segKey] = bodyIndex;
                        }
                        AddHashesToIndex(bodyIndex, expandedBodyHashes, doc.Id);
                        CountDocumentFrequency(expandedBodyHashes, doc.Id);
                    }
                }

                _lastLoadedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Conta em quantos documentos distintos cada hash aparece, para o
        /// cálculo do IDF (quanto menos registros um termo aponta, maior o peso).
        /// </summary>
        private void CountDocumentFrequency(IEnumerable<uint> hashes, int id)
        {
            if (!_idfDocTracker.TryGetValue(id, out var docHashes))
            {
                docHashes = new HashSet<uint>();
                _idfDocTracker[id] = docHashes;
            }

            foreach (var hash in hashes)
            {
                if (docHashes.Add(hash))
                {
                    _documentFrequency.TryGetValue(hash, out var count);
                    _documentFrequency[hash] = count + 1;
                }
            }
        }

        private static void AddHashesToIndex(Dictionary<uint, List<int>> index, IEnumerable<uint> hashes, int id)
        {
            foreach (var hash in hashes)
            {
                if (!index.TryGetValue(hash, out var ids))
                {
                    ids = new List<int>();
                    index[hash] = ids;
                }
                ids.Add(id);
            }
        }

        private HashSet<uint> ExpandTokensOrHashes(string keywordsStr, uint[] rawHashes)
        {
            var hashesSet = new HashSet<uint>();

            if (!string.IsNullOrWhiteSpace(keywordsStr))
            {
                var tokens = keywordsStr.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var token in tokens)
                {
                    ProcessToken(token.Trim(), hashesSet);
                }
            }
            else if (rawHashes != null)
            {
                foreach (var h in rawHashes)
                {
                    if (h != 0)
                    {
                        hashesSet.Add(h);
                        if (EnableSynonyms && _synonymMap.TryGetValue(h, out var synHashes))
                        {
                            foreach (var sh in synHashes)
                                hashesSet.Add(sh);
                        }
                    }
                }
            }

            return hashesSet;
        }

        private void LoadSynonyms(string customPath)
        {
            if (!EnableSynonyms) return;

            try
            {
                _synonymMap.Clear();
                var json = ReadSynonymsJson(customPath);
                if (string.IsNullOrWhiteSpace(json))
                    return;

                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var word = prop.Name.ToLowerInvariant().Trim();
                        var keyHash = (EnableStemming ? StemWord(word) : word).GetCustomHashCode();

                        var synHashes = new List<uint>();
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var synItem in prop.Value.EnumerateArray())
                            {
                                var s = synItem.GetString()?.ToLowerInvariant().Trim();
                                if (!string.IsNullOrEmpty(s))
                                {
                                    var h = (EnableStemming ? StemWord(s) : s).GetCustomHashCode();
                                    if (h != 0) synHashes.Add(h);
                                }
                            }
                        }

                        if (synHashes.Count > 0)
                        {
                            _synonymMap[keyHash] = synHashes.Distinct().ToArray();
                        }
                    }
                }
                else if (doc.RootElement.TryGetProperty("synonyms", out var synsElement) && synsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var group in synsElement.EnumerateArray())
                    {
                        var groupWords = new List<string>();
                        foreach (var word in group.EnumerateArray())
                        {
                            var w = word.GetString()?.ToLowerInvariant().Trim();
                            if (!string.IsNullOrEmpty(w))
                            {
                                groupWords.Add(w);
                            }
                        }

                        if (groupWords.Count > 1)
                        {
                            var groupHashes = groupWords.Select(w => (EnableStemming ? StemWord(w) : w).GetCustomHashCode()).Distinct().ToArray();
                            foreach (var h in groupHashes)
                            {
                                _synonymMap[h] = groupHashes;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Silently fallback if synonym json cannot be parsed
            }
        }

        private string ReadSynonymsJson(string customPath)
        {
            if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
            {
                return File.ReadAllText(customPath);
            }

            if (LoadEmbeddedSynonyms)
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly
                    .GetManifestResourceNames()
                    .FirstOrDefault(name => name.IndexOf("pt_br_synonyms", StringComparison.OrdinalIgnoreCase) >= 0);

                if (resourceName != null)
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        return reader.ReadToEnd();
                    }
                }
            }

            var candidatePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "pt_br_synonyms.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "pt_br_synonyms.json"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "pt_br_synonyms.json")
            };

            string validPath = candidatePaths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
            return validPath != null ? File.ReadAllText(validPath) : null;
        }

        public TextHashResult ProcessText(string title, string body = null, int? documentId = null)
        {
            var titleTokens = title?.Tokenize() ?? Array.Empty<string>();
            var bodyTokens = body?.Tokenize() ?? Array.Empty<string>();

            var titleTokensDistinct = titleTokens.Distinct().ToArray();
            var bodyTokensDistinct = bodyTokens.Distinct().ToArray();

            var titleHashesSet = new HashSet<uint>();
            var bodyHashesSet = new HashSet<uint>();

            foreach (var token in titleTokensDistinct)
                ProcessToken(token, titleHashesSet, EnableSynonyms, EnableStemming, EnablePhoneticFilter);

            foreach (var token in bodyTokensDistinct)
                ProcessToken(token, bodyHashesSet, EnableSynonyms, EnableStemming, EnablePhoneticFilter);

            // Frequencia de documentos mantida em memoria: atualizada durante o
            // ProcessText para que o IDF (desempate) reflita o corpus corrente.
            lock (_indexLock)
            {
                var processedId = ResolveDocumentId(documentId);
                if (titleHashesSet.Count > 0)
                    CountDocumentFrequency(titleHashesSet, processedId);
                if (bodyHashesSet.Count > 0)
                    CountDocumentFrequency(bodyHashesSet, processedId);
            }

            return new TextHashResult
            {
                TitleHashCodes = titleHashesSet.ToArray(),
                BodyHashCodes = bodyHashesSet.ToArray(),
                TitleKeywords = string.Join(",", titleTokensDistinct),
                BodyKeywords = string.Join(",", bodyTokensDistinct)
            };
        }

        /// <summary>
        /// Sem documentId explícito, cada chamada de ProcessText conta como um
        /// documento distinto (ids negativos nunca colidem com ids reais).
        /// </summary>
        private int ResolveDocumentId(int? documentId)
        {
            if (documentId.HasValue) return documentId.Value;
            return --_implicitDocCounter;
        }

        private int _implicitDocCounter;

        public uint[] ExtractHashes(string text)
        {
            return ExtractHashes(text, EnableSynonyms, EnableStemming, EnablePhoneticFilter);
        }

        public uint[] ExtractHashes(string text, bool useSynonyms, bool useStemming, bool useSoundex)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<uint>();

            var tokens = text.Tokenize() ?? Array.Empty<string>();
            var hashesSet = new HashSet<uint>();

            foreach (var token in tokens)
            {
                ProcessToken(token, hashesSet, useSynonyms, useStemming, useSoundex);
            }

            return hashesSet.ToArray();
        }

        private void ProcessToken(string token, HashSet<uint> hashesSet)
        {
            ProcessToken(token, hashesSet, EnableSynonyms, EnableStemming, EnablePhoneticFilter);
        }

        private void ProcessToken(string token, HashSet<uint> hashesSet, bool useSynonyms, bool useStemming, bool useSoundex)
        {
            if (string.IsNullOrWhiteSpace(token)) return;

            var processedWord = useStemming ? StemWord(token) : token.ToLowerInvariant().Trim();
            if (!string.IsNullOrEmpty(processedWord))
            {
                var wordHash = processedWord.GetCustomHashCode();
                hashesSet.Add(wordHash);

                if (useSynonyms && _synonymMap.TryGetValue(wordHash, out var synHashes))
                {
                    foreach (var sh in synHashes)
                        hashesSet.Add(sh);
                }
            }

            if (useSoundex)
            {
                var soundex = PhoneticFilter.Generate(token);
                if (!string.IsNullOrEmpty(soundex))
                {
                    hashesSet.Add(("sx_" + soundex).GetCustomHashCode());
                }
            }
        }

        public IndexSearchResult SearchIndex(uint[] queryHashes, double minMatchScore, int? segmentId = null)
        {
            if (queryHashes == null || queryHashes.Length == 0)
                return IndexSearchResult.Empty;

            List<Dictionary<uint, List<int>>> titleIndexesToSearch;
            List<Dictionary<uint, List<int>>> bodyIndexesToSearch;
            Dictionary<uint, int> docFrequencySnapshot;
            int totalDocs;

            lock (_indexLock)
            {
                if (_titleSegmentIndexes.Count == 0 && _bodySegmentIndexes.Count == 0)
                    return IndexSearchResult.Empty;

                if (segmentId.HasValue)
                {
                    titleIndexesToSearch = _titleSegmentIndexes.TryGetValue(segmentId.Value, out var t)
                        ? new List<Dictionary<uint, List<int>>> { t }
                        : new List<Dictionary<uint, List<int>>>();
                    bodyIndexesToSearch = _bodySegmentIndexes.TryGetValue(segmentId.Value, out var b)
                        ? new List<Dictionary<uint, List<int>>> { b }
                        : new List<Dictionary<uint, List<int>>>();
                }
                else
                {
                    titleIndexesToSearch = _titleSegmentIndexes.Values.ToList();
                    bodyIndexesToSearch = _bodySegmentIndexes.Values.ToList();
                }

                if (titleIndexesToSearch.Count == 0 && bodyIndexesToSearch.Count == 0)
                    return IndexSearchResult.Empty;

                docFrequencySnapshot = new Dictionary<uint, int>(_documentFrequency);
                totalDocs = _idfDocTracker.Count;
            }

            var querySet = new HashSet<uint>(queryHashes);
            var candidateCoverage = new Dictionary<int, int>();
            var candidateIdfSum = new Dictionary<int, double>();
            var candidateHashes = new Dictionary<int, HashSet<uint>>();

            // Criterio 1: cobertura de palavras (maximo de termos da expressao).
            // Criterio 2 (desempate): peso IDF por termo, calculado no indexing.
            foreach (var titleIndexSnapshot in titleIndexesToSearch)
            {
                foreach (var hash in querySet)
                {
                    if (!titleIndexSnapshot.TryGetValue(hash, out var ids)) continue;
                    double idf = ComputeIdf(hash, totalDocs, docFrequencySnapshot);
                    foreach (var id in ids)
                        AddCandidateMatch(id, hash, idf, candidateHashes, candidateCoverage, candidateIdfSum);
                }
            }

            foreach (var bodyIndexSnapshot in bodyIndexesToSearch)
            {
                foreach (var hash in querySet)
                {
                    if (!bodyIndexSnapshot.TryGetValue(hash, out var ids)) continue;
                    double idf = ComputeIdf(hash, totalDocs, docFrequencySnapshot);
                    foreach (var id in ids)
                        AddCandidateMatch(id, hash, idf, candidateHashes, candidateCoverage, candidateIdfSum);
                }
            }

            if (candidateCoverage.Count == 0)
                return IndexSearchResult.Empty;

            var best = candidateCoverage
                .Select(kvp =>
                {
                    double coverage = (double)kvp.Value / querySet.Count;
                    return (Id: kvp.Key, Coverage: coverage, IdFWeight: candidateIdfSum[kvp.Key]);
                })
                .Where(x => x.Coverage >= minMatchScore)
                .OrderByDescending(x => x.Coverage)
                .ThenByDescending(x => x.IdFWeight)
                .ThenBy(x => x.Id)
                .FirstOrDefault();

            return best.Id != 0 ? new IndexSearchResult { BestId = best.Id, Score = best.Coverage } : IndexSearchResult.Empty;
        }

        public IndexSearchResult Search(IEnumerable<IndexedDocument> documents, uint[] queryHashes, double minMatchScore)
        {
            if (documents == null || queryHashes == null || queryHashes.Length == 0)
                return IndexSearchResult.Empty;

            var activeDocuments = documents
                .Where(d => d.IsActive &&
                       ((d.TitleHashCodes != null && d.TitleHashCodes.Length > 0) ||
                        (d.BodyHashCodes != null && d.BodyHashCodes.Length > 0)))
                .ToList();

            if (activeDocuments.Count == 0)
                return IndexSearchResult.Empty;

            var querySet = new HashSet<uint>(queryHashes);
            var documentFrequency = ComputeDocumentFrequency(activeDocuments);
            int totalDocs = activeDocuments.Count;

            var scored = activeDocuments
                .AsParallel()
                .Select(doc =>
                {
                    var titleHashes = doc.TitleHashCodes ?? Array.Empty<uint>();
                    var bodyHashes = doc.BodyHashCodes ?? Array.Empty<uint>();

                    var matched = new HashSet<uint>();
                    double idfSum = 0.0;

                    foreach (var h in querySet)
                    {
                        if (Array.IndexOf(titleHashes, h) >= 0 || Array.IndexOf(bodyHashes, h) >= 0)
                        {
                            if (matched.Add(h))
                            {
                                idfSum += ComputeIdf(h, totalDocs, documentFrequency);
                            }
                        }
                    }

                    double coverage = (double)matched.Count / querySet.Count;
                    return (Document: doc, Coverage: coverage, IdFWeight: idfSum);
                })
                .Where(x => x.Coverage >= minMatchScore)
                .OrderByDescending(x => x.Coverage)
                .ThenByDescending(x => x.IdFWeight)
                .ThenBy(x => x.Document.Id)
                .FirstOrDefault();

            return scored.Document != null
                ? new IndexSearchResult { BestId = scored.Document.Id, Score = scored.Coverage }
                : IndexSearchResult.Empty;
        }

        private static Dictionary<uint, int> ComputeDocumentFrequency(IEnumerable<IndexedDocument> documents)
        {
            var documentFrequency = new Dictionary<uint, int>();
            foreach (var doc in documents)
            {
                var hashes = new HashSet<uint>();
                if (doc.TitleHashCodes != null) hashes.UnionWith(doc.TitleHashCodes);
                if (doc.BodyHashCodes != null) hashes.UnionWith(doc.BodyHashCodes);

                foreach (var hash in hashes)
                {
                    documentFrequency.TryGetValue(hash, out var count);
                    documentFrequency[hash] = count + 1;
                }
            }

            return documentFrequency;
        }

        private static void AddCandidateMatch(
            int id, uint hash, double idf,
            Dictionary<int, HashSet<uint>> candidateHashes,
            Dictionary<int, int> candidateCoverage,
            Dictionary<int, double> candidateIdfSum)
        {
            if (!candidateHashes.TryGetValue(id, out var matched))
            {
                matched = new HashSet<uint>();
                candidateHashes[id] = matched;
            }

            if (matched.Add(hash))
            {
                candidateCoverage.TryGetValue(id, out var coverage);
                candidateCoverage[id] = coverage + 1;

                candidateIdfSum.TryGetValue(id, out var idfSum);
                candidateIdfSum[id] = idfSum + idf;
            }
        }

        /// <summary>
        /// Peso inverso de frequência de documentos: quanto menos registros o
        /// termo aponta, maior o valor. Termos raros pontuam mais que os comuns.
        /// </summary>
        private static double ComputeIdf(uint hash, int totalDocs, Dictionary<uint, int> documentFrequency)
        {
            documentFrequency.TryGetValue(hash, out var docCount);
            return Math.Log(1.0 + (double)totalDocs / (1.0 + docCount));
        }

        private static string StemWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return string.Empty;
            try
            {
                using var stemmer = Stemmer.StemmerFactory();
                stemmer.DisableCaching();
                return stemmer.Stemming(word.ToLowerInvariant().Trim());
            }
            catch
            {
                return word.ToLowerInvariant().Trim();
            }
        }
    }
}
