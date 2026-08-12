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
    /// segregado por SegmentId e separado entre campo de título (peso maior)
    /// e campo de corpo (peso menor).
    /// </summary>
    internal class LexicalIndexEngine
    {
        private readonly Dictionary<uint, uint[]> _synonymMap = new Dictionary<uint, uint[]>();
        private readonly Dictionary<int, Dictionary<uint, List<int>>> _titleSegmentIndexes = new Dictionary<int, Dictionary<uint, List<int>>>();
        private readonly Dictionary<int, Dictionary<uint, List<int>>> _bodySegmentIndexes = new Dictionary<int, Dictionary<uint, List<int>>>();
        private DateTime? _lastLoadedAt;
        private readonly object _indexLock = new object();

        public bool EnableStemming { get; set; }
        public bool EnablePhoneticFilter { get; set; }
        public bool EnableSynonyms { get; set; } = true;
        public double TitleWeight { get; set; } = 3.0;
        public double BodyWeight { get; set; } = 1.0;

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
                TitleWeight = config.TitleWeight;
                BodyWeight = config.BodyWeight;
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

                foreach (var doc in documents)
                {
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
                    }
                }

                _lastLoadedAt = DateTime.UtcNow;
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

        public TextHashResult ProcessText(string title, string body = null)
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

            return new TextHashResult
            {
                TitleHashCodes = titleHashesSet.ToArray(),
                BodyHashCodes = bodyHashesSet.ToArray(),
                TitleKeywords = string.Join(",", titleTokensDistinct),
                BodyKeywords = string.Join(",", bodyTokensDistinct)
            };
        }

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

            var titleIndexesToSearch = new List<Dictionary<uint, List<int>>>();
            var bodyIndexesToSearch = new List<Dictionary<uint, List<int>>>();

            lock (_indexLock)
            {
                if (_titleSegmentIndexes.Count == 0 && _bodySegmentIndexes.Count == 0)
                    return IndexSearchResult.Empty;

                if (segmentId.HasValue)
                {
                    if (_titleSegmentIndexes.TryGetValue(segmentId.Value, out var titleTarget))
                    {
                        titleIndexesToSearch.Add(titleTarget);
                    }
                    if (_bodySegmentIndexes.TryGetValue(segmentId.Value, out var bodyTarget))
                    {
                        bodyIndexesToSearch.Add(bodyTarget);
                    }
                }
                else
                {
                    titleIndexesToSearch.AddRange(_titleSegmentIndexes.Values);
                    bodyIndexesToSearch.AddRange(_bodySegmentIndexes.Values);
                }
            }

            if (titleIndexesToSearch.Count == 0 && bodyIndexesToSearch.Count == 0)
                return IndexSearchResult.Empty;

            var querySet = new HashSet<uint>(queryHashes);
            var candidateMatchCounts = new Dictionary<int, double>();

            foreach (var titleIndexSnapshot in titleIndexesToSearch)
            {
                foreach (var hash in querySet)
                {
                    if (titleIndexSnapshot.TryGetValue(hash, out var ids))
                    {
                        foreach (var id in ids)
                        {
                            candidateMatchCounts.TryGetValue(id, out var currentScore);
                            candidateMatchCounts[id] = currentScore + TitleWeight;
                        }
                    }
                }
            }

            foreach (var bodyIndexSnapshot in bodyIndexesToSearch)
            {
                foreach (var hash in querySet)
                {
                    if (bodyIndexSnapshot.TryGetValue(hash, out var ids))
                    {
                        foreach (var id in ids)
                        {
                            candidateMatchCounts.TryGetValue(id, out var currentScore);
                            candidateMatchCounts[id] = currentScore + BodyWeight;
                        }
                    }
                }
            }

            if (candidateMatchCounts.Count == 0)
                return IndexSearchResult.Empty;

            var best = candidateMatchCounts
                .Select(kvp =>
                {
                    double score = kvp.Value / (querySet.Count * TitleWeight);
                    return (Id: kvp.Key, Score: score);
                })
                .Where(x => x.Score >= minMatchScore)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            return best.Id != 0 ? new IndexSearchResult { BestId = best.Id, Score = best.Score } : IndexSearchResult.Empty;
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

            var scored = activeDocuments
                .AsParallel()
                .Select(doc =>
                {
                    double matchScoreSum = 0.0;
                    var titleHashes = doc.TitleHashCodes ?? Array.Empty<uint>();
                    var bodyHashes = doc.BodyHashCodes ?? Array.Empty<uint>();

                    foreach (var h in querySet)
                    {
                        if (Array.IndexOf(titleHashes, h) >= 0)
                        {
                            matchScoreSum += TitleWeight;
                        }
                        else if (Array.IndexOf(bodyHashes, h) >= 0)
                        {
                            matchScoreSum += BodyWeight;
                        }
                    }

                    double score = matchScoreSum / (querySet.Count * TitleWeight);
                    return (Document: doc, Score: score);
                })
                .Where(x => x.Score >= minMatchScore)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Document.Id)
                .FirstOrDefault();

            return scored.Document != null
                ? new IndexSearchResult { BestId = scored.Document.Id, Score = scored.Score }
                : IndexSearchResult.Empty;
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
