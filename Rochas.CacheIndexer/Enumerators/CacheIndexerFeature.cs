using System;

namespace Rochas.CacheIndexer.Enumerators
{
    /// <summary>
    /// Features de normalização lexical que podem ser ligadas/desligadas
    /// em conjunto via flag set.
    /// </summary>
    [Flags]
    public enum CacheIndexerFeature
    {
        /// <summary>Nenhuma feature extra de normalização.</summary>
        None = 0,

        /// <summary>Expansão via dicionário de sinônimos.</summary>
        Synonyms = 1,

        /// <summary>Radicalização por Stemming de Porter para PT-BR.</summary>
        Stemming = 2,

        /// <summary>Filtro fonético Soundex PT-BR.</summary>
        Phonetic = 4,

        /// <summary>Todas as features habilitadas.</summary>
        All = Synonyms | Stemming | Phonetic
    }
}
