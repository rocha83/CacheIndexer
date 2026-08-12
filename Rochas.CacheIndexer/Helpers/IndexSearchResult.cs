namespace Rochas.CacheIndexer.Helpers
{
    /// <summary>Resultado de uma busca no índice.</summary>
    public class IndexSearchResult
    {
        public int? BestId { get; set; }

        public double Score { get; set; }

        /// <summary>Camada (tier) que produziu o hit: base, synonyms, stemming ou soundex.</summary>
        public string Tier { get; set; }

        public bool Found => BestId.HasValue;

        public static IndexSearchResult Empty => new IndexSearchResult();
    }
}
