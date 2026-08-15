using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Rochas.Data.Specification.Annotations;
using Rochas.Data.Specification.Enums;

namespace Rochas.CacheIndexer.Helpers
{
    /// <summary>
    /// Constrói um IndexedDocument a partir de uma entidade tipada, respeitando a
    /// anotação [Indexable] (Rochas.Data.Specification). Somente propriedades
    /// anotadas são consideradas: [Indexable(Section = Title)] alimenta Title e
    /// [Indexable(Section = Body)] alimenta Body. Propriedades sem anotação são
    /// ignoradas pelo índice.
    /// </summary>
    public static class IndexedDocumentMapper
    {
        private static readonly Type _indexableType = typeof(IndexableAttribute);

        /// <summary>
        /// Mapeia uma entidade em IndexedDocument, agrupando por seção [Indexable].
        /// Valores de múltiplas colunas da mesma seção são concatenados com espaço.
        /// </summary>
        public static IndexedDocument Build<T>(T entity, int id = 0, int? segmentId = null) where T : class
        {
            if (entity == null)
                return null;

            var props = entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                              .Where(prp => prp.GetCustomAttribute(_indexableType) != null)
                              .ToArray();

            var title = BuildSectionText(entity, props, IndexSection.Title);
            var body = BuildSectionText(entity, props, IndexSection.Body);

            return new IndexedDocument
            {
                Id = id,
                SegmentId = segmentId,
                Title = title,
                Body = body
            };
        }

        /// <summary>Extrai apenas os textos (hases) de uma seção indexável.</summary>
        public static string[] ExtractIndexedStrings<T>(T entity, IndexSection section) where T : class
        {
            if (entity == null)
                return Array.Empty<string>();

            var props = entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                              .Where(prp => prp.GetCustomAttribute(_indexableType) is IndexableAttribute attr
                                            && attr.Section == section)
                              .ToArray();

            var values = new List<string>();
            foreach (var prop in props)
            {
                var value = prop.GetValue(entity);
                if (value != null)
                    values.Add(value.ToString());
            }

            return values.ToArray();
        }

        private static string BuildSectionText<T>(T entity, PropertyInfo[] props, IndexSection section)
        {
            var values = new List<string>();
            foreach (var prop in props)
            {
                if (!(prop.GetCustomAttribute(_indexableType) is IndexableAttribute attr))
                    continue;
                if (attr.Section != section)
                    continue;

                var value = prop.GetValue(entity);
                if (value != null)
                {
                    var text = value.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        values.Add(text);
                }
            }

            return values.Count == 0 ? null : string.Join(" ", values);
        }
    }
}