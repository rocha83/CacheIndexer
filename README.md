# Rochas.CacheIndexer

[![NuGet](https://img.shields.io/nuget/v/Rochas.CacheIndexer.svg)](https://www.nuget.org/packages/Rochas.CacheIndexer)

Índice léxico invertido em memória para busca de conhecimento/respostas com cache de hashes, segregação por segmento e **features de normalização liga/desliga**, mais **provedores de cache de objetos** plugáveis (in-memory, distribuído Redis/Garnet, composto e persistência por evento):

- **Sinônimos** — dicionário PT-BR embarcado (`pt_br_synonyms.json`) ou customizado;
- **Stemming** — Stemmer de Porter para PT-BR (`Rochas.PTStemmer`);
- **Soundex** — filtro fonético Soundex adaptado para PT-BR;
- **Cache de objetos** — `ICacheProvider` com `InMemoryCacheProvider`, `DistributedCacheProvider` (Redis/Garnet), `CompositeCacheProvider` (L1+L2) e `PersistenceChannelCacheProvider` (replicação assíncrona por evento a 1+ SGDB).

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
await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

// Busca progressiva: base -> sinonimos -> stemming -> soundex
var result = await indexer.FindBestMatch("quero emitir uma fatura", loadDocs);
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
| `EnsureIndexLoaded(loader)` | Constrói o índice invertido (hash -> ids) e computa a frequência de documentos (IDF) |
| `SearchIndex(query, minMatchScore, segmentId?)` | Busca no índice: cobertura de palavras + ranking IDF |
| `Search(documents, query, minMatchScore)` | Busca direta sobre uma colecao (sem indexacao previa) |
| `FindBestMatch(message, loader, segmentId?)` | Busca progressiva em 4 tiers, do mais preciso ao mais permissivo |
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

## 💾 Cache de objetos (ICacheProvider)

Cache de entidades/POCOs com provedores plugáveis, desacoplado de implementação concreta. A fachada estática `DataCache` concentra o acesso:

```csharp
using Rochas.CacheIndexer.Providers;

// Inicialização única (startup da aplicação):
DataCache.Initialize(new InMemoryCacheProvider());                              // default
DataCache.Initialize(memorySizeLimit: 100);                                     // in-memory limitado a 100 MB
DataCache.Initialize(new DistributedCacheProvider("localhost:6379"));           // Redis/Garnet
DataCache.Initialize(new CompositeCacheProvider(                                 // L1 + L2
    new InMemoryCacheProvider(),
    new DistributedCacheProvider("localhost:6379")));
DataCache.Initialize(new PersistenceChannelCacheProvider(new InMemoryCacheProvider())); // master

// Uso:
DataCache.Put(new Product { Id = 1 }, product);
var product = DataCache.Get(new Product { Id = 1 });
DataCache.Del(new Product { Id = 1 }, deleteAll: true);
DataCache.Clear();
```

### Provedores

| Provedor | Descrição | Use quando |
|---|---|---|
| `InMemoryCacheProvider` | `ConcurrentDictionary` thread-safe, chave = hash FNV do tipo + chave JSON | desenvolvimento, catálogos pequenos, L1 |
| `DistributedCacheProvider` | `IDistributedCache` — **Redis** ou **Microsoft Garnet** | multi-instância/pods, alta disponibilidade |
| `CompositeCacheProvider` | L1 in-memory + L2 distribuído, write-through e **promoção de L2→L1** na leitura | latência + compartilhamento |
| `PersistenceChannelCacheProvider` | canal assíncrono por assinante (fan-out real via `Subscribe`), consumidores persistindo em 1+ SGDB | replicação master→slave por evento |

### Pipeline típico

```
L1 in-memory (microssegundos) → L2 distribuído Redis/Garnet (milissegundos) → banco SQL
```

- **Leitura** no composto: tenta L1 → em miss busca na L2 e promove para L1 (a partir da primeira leitura o item é servido em memória);
- **Escrita** no composto: L1 e L2 juntas (write-through).

### Cache distribuído (Redis / Garnet)

`DistributedCacheProvider` usa a abstração `IDistributedCache` — funciona com qualquer implementação compatível. Para ASP.NET Core (injeção de dependência):

```csharp
// Program.cs
builder.Services.AddStackExchangeRedisCache(o =>
{
    o.Configuration = builder.Configuration.GetConnectionString("Redis");
    o.InstanceName = "cache:";
});

DataCache.Initialize(new DistributedCacheProvider(
    cache, instanceName: "cache:", defaultExpiration: TimeSpan.FromMinutes(5)));
```

O **Microsoft Garnet** é um servidor Redis-compatible; basta apontar o cliente Redis para o endpoint Garnet — o `DistributedCacheProvider` funciona inalterado.

### Persistência por evento (master → 1+ SGDB)

O master grava no cache local e publica no canal; cada consumidor (slave) se inscreve e recebe uma **cópia** de cada evento (canal privado por assinante — fan-out real):

```csharp
// Slave A (SGDB A):
var readerA = provider.Subscribe(capacity: 1000);
await foreach (var msg in readerA.ReadAllAsync())
{
    switch (msg.Action)
    {
        case PersistenceChannelCacheProvider.ChannelAction.Put:
            await repoA.AddAsync(msg.CacheItem);      // SGDB A
            break;
        case PersistenceChannelCacheProvider.ChannelAction.Del:
            await repoA.RemoveAsync(msg.CacheKey);    // SGDB B
            break;
        case PersistenceChannelCacheProvider.ChannelAction.Clear:
            await repoA.ClearAsync();
            break;
    }
}

// Slave B (SGDB B): subscribe idêntico, sem afetar o Slave A.
```

Conveniência para um único consumidor: `await foreach (var msg in provider.Consume(ct))`.

Backpressure: canais bounded (`Wait`); um consumidor lento além da capacidade descarta eventos só para ele. `Subscribe(capacity <= 0)` cria canal unbounded (nenhuma perda).

### Replicação automática para banco (Background Worker + DataDispatcher)

Para persistir os eventos do canal em um SGDB sem escrever o `foreach` na mão,
use o **`PersistenceChannelWorker<T>`** (`BackgroundService` de
`Microsoft.Extensions.Hosting`) + **`DataDispatcher<T>`**, que conecta no banco
via `IPersistenceRepository<T>` (interface base do `Rochas.Data.Specification`).

```csharp
using Rochas.CacheIndexer.Helpers;
using Rochas.Data.Specification.Interfaces;
using Rochas.DapperRepository;

// Master publica no canal (como antes):
DataCache.Initialize(new PersistenceChannelCacheProvider(new InMemoryCacheProvider()));

// Slave: registra o worker no DI (consumo + persistência no banco do slave):
var slaveRepo = new GenericRepository<Product>(DatabaseEngine.SQLite, slaveConnString);
var dispatcher = new DataDispatcher<Product>(slaveRepo);

// ASP.NET Core:
builder.Services.AddHostedService(sp =>
    new PersistenceChannelWorker<Product>(channelProvider, dispatcher));
```

Mapeamento de ações no `DataDispatcher<T>`:

| Ação do canal | Chamada no `IPersistenceRepository<T>` |
|---|---|
| `Put` | `Add(entity)` |
| `Del` | `Remove(filter)` |
| `Clear` / `Del(deleteAll: true)` | `NotSupportedException` (interface não expõe limpeza global) |

`DispatchAsync` é `virtual` — para replicação idempotente (upsert) ou suporte a
`DeleteAll`/`Clear`, sobrescreva no consumidor. Falhas por mensagem são
registradas no logger e o consumo continua.

### Marcação de entidade cacheável

```csharp
using Rochas.CacheIndexer.Annotations;
using Rochas.CacheIndexer.Providers;

[Cacheable(typeof(InMemoryCacheProvider))]
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; }
}
```

---

## 📄 Licença

GPL v2 — livre para uso comercial e pessoal.
