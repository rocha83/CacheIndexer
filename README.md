# Rochas.CacheIndexer

[![NuGet](https://img.shields.io/nuget/v/Rochas.CacheIndexer.svg)](https://www.nuget.org/packages/Rochas.CacheIndexer)

Índice léxico invertido em memória para busca de conhecimento/respostas com cache de hashes, segregação por segmento e **features de normalização liga/desliga**:

- **Sinônimos** — dicionário PT-BR embarcado (`pt_br_synonyms.json`) ou customizado;
- **Stemming** — Stemmer de Porter para PT-BR (`Rochas.PTStemmer`);
- **Soundex** — filtro fonético Soundex adaptado para PT-BR.

Baseada em `Rochas.PTStemmer` e `Rochas.Extensions`, compatível com **.NET Standard 2.1+**.

---

## 📦 Instalação

```bash
dotnet add package Rochas.CacheIndexer
```

---

## 🚀 Uso rápido

```csharp
using Rochas.CacheIndexer;
using Rochas.CacheIndexer.Helpers;

var indexer = new CacheIndexer(new CacheIndexerConfig
{
    EnableStemming = false,
    EnablePhoneticFilter = false,
    EnableSynonyms = true,
    MinMatchScore = 0.3
});

// Carrega o índice a partir dos documentos
await indexer.EnsureIndexLoadedAsync(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

// Busca progressiva: base -> sinonimos -> stemming -> soundex
var result = await indexer.FindBestMatchAsync("quero emitir uma fatura", loadDocs);
if (result.Found)
    Console.WriteLine($"Melhor doc: {result.BestId} (score {result.Score:F2}, tier {result.Tier})");
```

---

## 🎚 Ligando/desligando as features

### Via propriedades

```csharp
indexer.EnableStemming = true;        // radicaliza termos antes de hashear
indexer.EnablePhoneticFilter = true;  // adiciona hash fonetico (Soundex PT-BR)
indexer.EnableSynonyms = false;       // desliga o dicionario de sinonimos
```

### Via flag enum (todas de uma vez)

```csharp
using Rochas.CacheIndexer.Enumerators;

indexer.SetFeatures(CacheIndexerFeature.All);                    // sinonimos + stemming + fonetica
indexer.SetFeatures(CacheIndexerFeature.None);                   // tudo desligado
indexer.SetFeatures(CacheIndexerFeature.Synonyms | CacheIndexerFeature.Phonetic);
```

### Efeito prático

| Feature | Comportamento | Exemplo |
|---|---|---|
| `Synonyms` | `fatura` expande para `boleto`, `duplicata`, `cobranca`... | busca por "boleto" acha "fatura" |
| `Stemming` | `pagamentos` -> `pagament` == `pagamento` -> `pagament` | busca por "pagamentos" acha "pagamento" |
| `Phonetic` | `casa` e `caza` geram o mesmo código Soundex (`C200`) | tolera erros de digitação/sons |

---

## 🧠 API principal

| Método | Descrição |
|---|---|
| `EnsureIndexLoadedAsync(loader)` | Constrói o índice invertido (hash -> ids) a partir dos documentos |
| `SearchIndex(query, minMatchScore, segmentId?)` | Busca no índice; titulo pesa 3.0, corpo 1.0 |
| `Search(documents, query, minMatchScore)` | Busca direta sobre uma colecao (sem indexacao previa) |
| `FindBestMatchAsync(message, loader, segmentId?)` | Busca progressiva em 4 tiers, do mais preciso ao mais permissivo |
| `ProcessText(title, body)` | Tokeniza e hasheia um par titulo/corpo (para persistencia/learning) |
| `ExtractHashes(text)` / `ExtractHashes(text, syn, stem, sx)` | Extrai hashes de um texto |
| `InvalidateIndex()` | Limpa o indice e forca reindexacao no proximo uso |
| `SetFeatures` / `GetFeatures` | Liga/desliga features em conjunto |

---

## ⚙️ Configuração (CacheIndexerConfig)

```csharp
var config = new CacheIndexerConfig
{
    EnableStemming = false,
    EnablePhoneticFilter = false,
    EnableSynonyms = true,
    SynonymsFilePath = "custom_synonyms.json",   // custom path (opcional)
    LoadEmbeddedSynonyms = true,                 // fallback: dicionario embarcado
    MinMatchScore = 0.3,
    TitleWeight = 3.0,
    BodyWeight = 1.0
};
```

---

## 📄 Licença

GPL v2 — livre para uso comercial e pessoal.
