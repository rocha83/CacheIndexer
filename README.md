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
| `EnsureIndexLoadedAsync(loader)` | Constrói o índice invertido (hash -> ids) e computa a frequência de documentos (IDF) |
| `SearchIndex(query, minMatchScore, segmentId?)` | Busca no índice: cobertura de palavras + ranking IDF |
| `Search(documents, query, minMatchScore)` | Busca direta sobre uma colecao (sem indexacao previa) |
| `FindBestMatchAsync(message, loader, segmentId?)` | Busca progressiva em 4 tiers, do mais preciso ao mais permissivo |
| `ProcessText(title, body, documentId?)` | Tokeniza, hasheia e atualiza a frequência em memória (base do IDF) |
| `ExtractHashes(text)` / `ExtractHashes(text, syn, stem, sx)` | Extrai hashes de um texto |
| `InvalidateIndex()` | Limpa o indice e forca reindexacao no proximo uso |
| `SetFeatures` / `GetFeatures` | Liga/desliga features em conjunto |

---

## 🎯 Estratégia de relevância (cobertura + ranking)

Dois critérios combinados na busca:

1. **Cobertura (obtenção)** — vence o conteúdo que dá match com o **máximo de palavras distintas** da expressão. `Score = palavras casadas / palavras da expressão`.

2. **Ranking (desempate por IDF)** — a **frequência de documentos** é mantida em um **dicionário em memória** e atualizada **durante o `ProcessText`**: quanto menos registros aquele hash/palavra aponta, maior seu peso (`idf = ln(1 + N / (1 + df))`). O desempate é calculado **no instante da busca** usando essa frequência corrente.

Empatados em cobertura, vence quem tem os termos mais raros; em último caso, o menor `Id`.

```
Doc A: "fatura, boleto"          <- termo "cancelamento" é raro no índice
Doc B: "cancelamento"            <- só casou 1 palavra (rara)

query: "fatura boleto cancelamento"
-> Doc A vence (2/3 palavras) mesmo com IDF menor por termo
-> Doc B só vence se a cobertura empatar (1/1 x 1/1) e o termo for mais raro
```

Como a frequência vive no dicionário em memória, o aprendizado em runtime muda o ranking sem reindexar:

```csharp
indexer.ProcessText("fatura", null, documentId: 42); // soma df("fatura") em memória
// a próxima busca já recalcula o IDF de "fatura" no desempate
```

> `TitleWeight`/`BodyWeight` foram descontinuados (pesos fixos por campo não refletem raridade).

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
    MinMatchScore = 0.3
};
```

---

## 📄 Licença

GPL v2 — livre para uso comercial e pessoal.
