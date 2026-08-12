namespace Rochas.CacheIndexer.Helpers
{
    /// <summary>
    /// Resultado da tokenização + hashing de um par título/corpo.
    /// </summary>
    public class TextHashResult
    {
        public uint[] TitleHashCodes { get; set; }

        public uint[] BodyHashCodes { get; set; }

        public string TitleKeywords { get; set; }

        public string BodyKeywords { get; set; }
    }
}
