using Rochas.CacheIndexer.Enumerators;

namespace Rochas.CacheIndexer.Helpers
{
    /// <summary>
    /// Centraliza todas as configurações do CacheIndexer, no mesmo espírito
    /// do PdfConfig do Rochas.PDFGenerator.
    /// </summary>
    public class CacheIndexerConfig
    {
        /// <summary>Radicaliza termos (Stemmer de Porter PT-BR) antes de hashear.</summary>
        public bool EnableStemming { get; set; } = false;

        /// <summary>Adiciona hash fonético (Soundex PT-BR) por termo.</summary>
        public bool EnablePhoneticFilter { get; set; } = false;

        /// <summary>Expande termos via dicionário de sinônimos.</summary>
        public bool EnableSynonyms { get; set; } = true;

        /// <summary>
        /// Caminho opcional para um dicionário de sinônimos customizado.
        /// Quando ausente, tenta o recurso embarcado do pacote.
        /// </summary>
        public string SynonymsFilePath { get; set; }

        /// <summary>
        /// Quando true (default), carrega o dicionário pt_br_synonyms.json
        /// embarcado no pacote caso não haja arquivo customizado.
        /// </summary>
        public bool LoadEmbeddedSynonyms { get; set; } = true;

        /// <summary>Score mínimo de match para um candidato ser aceito.</summary>
        public double MinMatchScore { get; set; } = 0.3;

        /// <summary>
        /// Peso de um termo casado no campo de título/pergunta.
        /// </summary>
        public double TitleWeight { get; set; } = 3.0;

        /// <summary>
        /// Peso de um termo casado no campo de corpo/resposta.
        /// </summary>
        public double BodyWeight { get; set; } = 1.0;

        /// <summary>
        /// Conveniência para ligar/desligar várias features de uma vez.
        /// </summary>
        public CacheIndexerFeature Features
        {
            get
            {
                var flags = CacheIndexerFeature.None;
                if (EnableSynonyms) flags |= CacheIndexerFeature.Synonyms;
                if (EnableStemming) flags |= CacheIndexerFeature.Stemming;
                if (EnablePhoneticFilter) flags |= CacheIndexerFeature.Phonetic;
                return flags;
            }
            set
            {
                EnableSynonyms = value.HasFlag(CacheIndexerFeature.Synonyms);
                EnableStemming = value.HasFlag(CacheIndexerFeature.Stemming);
                EnablePhoneticFilter = value.HasFlag(CacheIndexerFeature.Phonetic);
            }
        }
    }
}
