namespace Rochas.CacheIndexer.Helpers
{
    /// <summary>
    /// Documento indexável. Pode carregar texto bruto (Title/Body) e/ou
    /// hashes pré-computados (para reuso de índices já persistidos).
    /// </summary>
    public class IndexedDocument
    {
        public int Id { get; set; }

        /// <summary>Identificador de segmento opcional para índices segregados.</summary>
        public int? SegmentId { get; set; }

        /// <summary>Texto do título/pergunta.</summary>
        public string Title { get; set; }

        /// <summary>Texto do corpo/resposta.</summary>
        public string Body { get; set; }

        public uint[] TitleHashCodes { get; set; }

        public uint[] BodyHashCodes { get; set; }

        /// <summary>Indica se a entrada está ativa para busca.</summary>
        public bool IsActive { get; set; } = true;
    }
}
