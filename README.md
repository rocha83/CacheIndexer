# Rochas.CacheIndexer

[English](#english) | [Português](#português) | [Español](#español) | [Français](#français) | [Deutsch](#deutsch)

---

## English

In-memory inverted lexical index for knowledge/answer retrieval with hash caching, per-segment segregation, and **on/off normalization features**, plus pluggable **object cache providers** (in-memory, distributed Redis/Garnet, composite, and event-based persistence):

- **Synonyms** — embedded PT-BR dictionary (`pt_br_synonyms.json`) or custom;
- **Stemming** — Porter stemmer for PT-BR (`Rochas.PTStemmer`);
- **Soundex** — phonetic Soundex filter adapted to PT-BR;
- **Object cache** — `ICacheProvider` with `InMemoryCacheProvider`, `DistributedCacheProvider` (Redis/Garnet), `CompositeCacheProvider` (L1+L2), and `PersistenceChannelCacheProvider` (asynchronous event-based replication to 1+ databases).

Built on `Rochas.PTStemmer` and `Rochas.Extensions`, targeting **.NET Standard 2.1+**.

### Installation

```bash
dotnet add package Rochas.CacheIndexer
```

### Quick start

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

// Loads the index from the documents
await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

// Progressive search: base -> synonyms -> stemming -> soundex
var result = await indexer.FindBestMatch("quero emitir uma fatura", loadDocs);
if (result.Found)
    Console.WriteLine($"Best doc: {result.BestId} (score {result.Score:F2}, tier {result.Tier})");
```

### Turning features on/off

#### Via properties

```csharp
indexer.EnableStemming = true;        // stems terms before hashing
indexer.EnablePhoneticFilter = true;  // adds phonetic hash (PT-BR Soundex)
indexer.EnableSynonyms = false;       // disables the synonyms dictionary
```

#### Via flag enum (all at once)

```csharp
using Rochas.CacheIndexer.Enumerators;

indexer.SetFeatures(CacheIndexerFeature.All);                    // synonyms + stemming + phonetic
indexer.SetFeatures(CacheIndexerFeature.None);                   // everything off
indexer.SetFeatures(CacheIndexerFeature.Synonyms | CacheIndexerFeature.Phonetic);
```

#### Practical effect

| Feature | Behavior | Example |
|---|---|---|
| `Synonyms` | `fatura` expands to `boleto`, `duplicata`, `cobranca`... | search for "boleto" finds "fatura" |
| `Stemming` | `pagamentos` -> `pagament` == `pagamento` -> `pagament` | search for "pagamentos" finds "pagamento" |
| `Phonetic` | `casa` and `caza` produce the same Soundex code (`C200`) | tolerates typos/similar sounds |

### Main API

| Method | Description |
|---|---|
| `EnsureIndexLoaded(loader)` | Builds the inverted index (hash -> ids) and computes document frequency (IDF) |
| `SearchIndex(query, minMatchScore, segmentId?)` | Searches the index: word coverage + IDF ranking |
| `Search(documents, query, minMatchScore)` | Direct search over a collection (no prior indexing) |
| `FindBestMatch(message, loader, segmentId?)` | Progressive search across 4 tiers, from most precise to most permissive |
| `ProcessText(title, body, documentId?)` | Tokenizes, hashes, and updates in-memory frequency (basis for IDF) |
| `ExtractHashes(text)` / `ExtractHashes(text, syn, stem, sx)` | Extracts hashes from a text |
| `InvalidateIndex()` | Clears the index and forces reindexing on next use |
| `SetFeatures` / `GetFeatures` | Toggles features together |

### Relevance strategy (coverage + ranking)

Two combined criteria in the search:

1. **Coverage (retrieval)** — the content that matches the **maximum number of distinct words** of the expression wins. `Score = matched words / expression words`.

2. **Ranking (tie-break by IDF)** — document frequency is kept in an **in-memory dictionary** and updated **during `ProcessText`**: the fewer records a hash/word points to, the higher its weight (`idf = ln(1 + N / (1 + df))`). The tie-break is computed **at search time** using the current frequency.

Tied on coverage, the winner is the one with the rarest terms; as a last resort, the smallest `Id`.

```
Doc A: "fatura, boleto"          <- term "cancelamento" is rare in the index
Doc B: "cancelamento"            <- matched only 1 (rare) word

query: "fatura boleto cancelamento"
-> Doc A wins (2/3 words) even with lower per-term IDF
-> Doc B only wins if coverage ties (1/1 x 1/1) and the term is rarer
```

Because frequency lives in the in-memory dictionary, runtime learning changes ranking without reindexing:

```csharp
indexer.ProcessText("fatura", null, documentId: 42); // increments df("fatura") in memory
// the next search already recalculates the IDF of "fatura" in the tie-break
```

> `TitleWeight`/`BodyWeight` were discontinued (fixed per-field weights do not reflect rarity).

### Configuration (CacheIndexerConfig)

```csharp
var config = new CacheIndexerConfig
{
    EnableStemming = false,
    EnablePhoneticFilter = false,
    EnableSynonyms = true,
    SynonymsFilePath = "custom_synonyms.json",   // custom path (optional)
    LoadEmbeddedSynonyms = true,                 // fallback: embedded dictionary
    MinMatchScore = 0.3
};
```

### Object cache (ICacheProvider)

Object/POCO caching with pluggable providers, decoupled from concrete implementations. The static `DataCache` facade centralizes access:

```csharp
using Rochas.CacheIndexer.Providers;

// One-time initialization (app startup):
DataCache.Initialize(new InMemoryCacheProvider());                              // default
DataCache.Initialize(memorySizeLimit: 100);                                     // in-memory limited to 100 MB
DataCache.Initialize(new DistributedCacheProvider("localhost:6379"));           // Redis/Garnet
DataCache.Initialize(new CompositeCacheProvider(                                 // L1 + L2
    new InMemoryCacheProvider(),
    new DistributedCacheProvider("localhost:6379")));
DataCache.Initialize(new PersistenceChannelCacheProvider(new InMemoryCacheProvider())); // master

// Usage:
DataCache.Put(new Product { Id = 1 }, product);
var product = DataCache.Get(new Product { Id = 1 });
DataCache.Del(new Product { Id = 1 }, deleteAll: true);
DataCache.Clear();
```

#### Providers

| Provider | Description | Use when |
|---|---|---|
| `InMemoryCacheProvider` | Thread-safe `ConcurrentDictionary`, key = FNV hash of type + JSON key | development, small catalogs, L1 |
| `DistributedCacheProvider` | `IDistributedCache` — **Redis** or **Microsoft Garnet** | multi-instance/pods, high availability |
| `CompositeCacheProvider` | L1 in-memory + L2 distributed, write-through and **L2->L1 promotion** on read | latency + sharing |
| `PersistenceChannelCacheProvider` | async per-subscriber channel (real fan-out via `Subscribe`), consumers persisting to 1+ DBs | master->slave event replication |

#### Typical pipeline

```
L1 in-memory (microseconds) → L2 distributed Redis/Garnet (milliseconds) → SQL database
```

- **Read** on composite: tries L1 → on miss, reads L2 and promotes to L1 (after the first read the item is served from memory);
- **Write** on composite: L1 and L2 together (write-through).

#### Distributed cache (Redis / Garnet)

`DistributedCacheProvider` uses the `IDistributedCache` abstraction — works with any compatible implementation. For ASP.NET Core (dependency injection):

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

**Microsoft Garnet** is a Redis-compatible server; just point the Redis client at the Garnet endpoint — `DistributedCacheProvider` works unchanged.

#### Event-based persistence (master → 1+ DBs)

The master writes to the local cache and publishes to the channel; each consumer (slave) subscribes and receives a **copy** of every event (private channel per subscriber — real fan-out):

```csharp
// Slave A (DB A):
var readerA = provider.Subscribe(capacity: 1000);
await foreach (var msg in readerA.ReadAllAsync())
{
    switch (msg.Action)
    {
        case PersistenceChannelCacheProvider.ChannelAction.Put:
            await repoA.AddAsync(msg.CacheItem);      // DB A
            break;
        case PersistenceChannelCacheProvider.ChannelAction.Del:
            await repoA.RemoveAsync(msg.CacheKey);    // DB B
            break;
        case PersistenceChannelCacheProvider.ChannelAction.Clear:
            await repoA.ClearAsync();
            break;
    }
}

// Slave B (DB B): identical subscribe, without affecting Slave A.
```

Convenience for a single consumer: `await foreach (var msg in provider.Consume(ct))`.

Backpressure: bounded channels (`Wait`); a slow consumer beyond capacity drops events only for itself. `Subscribe(capacity <= 0)` creates an unbounded channel (no loss).

#### Automatic DB replication (Background Worker + DataDispatcher)

To persist channel events into a DB without writing the `foreach` by hand, use **`PersistenceChannelWorker<T>`** (`BackgroundService` from `Microsoft.Extensions.Hosting`) + **`DataDispatcher<T>`**, which connects to the database via `IGenericRepository<T>` (interface from `Rochas.DapperRepository.Specification`).

```csharp
using Rochas.CacheIndexer.Helpers;
using Rochas.DapperRepository.Specification.Interfaces;
using Rochas.DapperRepository;

// Master publishes to the channel (as before):
DataCache.Initialize(new PersistenceChannelCacheProvider(new InMemoryCacheProvider()));

// Slave: register the worker in DI (consumption + persistence in the slave DB):
var slaveRepo = new GenericRepository<Product>(DatabaseEngine.SQLite, slaveConnString);
var dispatcher = new DataDispatcher<Product>(slaveRepo);

// ASP.NET Core:
builder.Services.AddHostedService(sp =>
    new PersistenceChannelWorker<Product>(channelProvider, dispatcher));
```

Action mapping in `DataDispatcher<T>`:

| Channel action | Call on `IGenericRepository<T>` |
|---|---|
| `Put` | `Add(entity)` |
| `Del` | `Remove(filter)` |
| `Clear` / `Del(deleteAll: true)` | `NotSupportedException` (interface does not expose global cleanup) |

`DispatchAsync` is `virtual` — for idempotent replication (upsert) or `DeleteAll`/`Clear` support, override it in the consumer. Per-message failures are logged and consumption continues.

#### Marking a cacheable entity

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

### Tests and coverage

Suite with **106 tests** (xUnit + FluentAssertions) measuring **95.69% line coverage** (866/905) and **88.14% branch** in the `Rochas.CacheIndexer` assembly (coverlet + XPlat Code Coverage, filtered to `Rochas.CacheIndexer.dll` only). Most components reach 100% (`CacheIndexer` + `FindBestMatch`, `CompositeCacheProvider`, `DataCache`, `DistributedCacheProvider`, `PersistenceChannelCacheProvider`, `DataDispatcher<T>`, `PersistenceChannelWorker<T>`); `InMemoryCacheProvider` at 97%, `LexicalIndexEngine` at 92%, and `PhoneticFilter` at 91%.

Scenario coverage: word-coverage + IDF search, in-memory frequency tie-break, body/segment search, pre-computed hashes, all cache providers, and dispatcher/worker with per-message failure (log + continuity).

### License

GPL v2 — free for commercial and personal use.

---

## Português

Índice léxico invertido em memória para busca de conhecimento/respostas com cache de hashes, segregação por segmento e **features de normalização liga/desliga**, mais **provedores de cache de objetos** plugáveis (in-memory, distribuído Redis/Garnet, composto e persistência por evento):

- **Sinônimos** — dicionário PT-BR embarcado (`pt_br_synonyms.json`) ou customizado;
- **Stemming** — Stemmer de Porter para PT-BR (`Rochas.PTStemmer`);
- **Soundex** — filtro fonético Soundex adaptado para PT-BR;
- **Cache de objetos** — `ICacheProvider` com `InMemoryCacheProvider`, `DistributedCacheProvider` (Redis/Garnet), `CompositeCacheProvider` (L1+L2) e `PersistenceChannelCacheProvider` (replicação assíncrona por evento a 1+ SGDB).

Baseada em `Rochas.PTStemmer` e `Rochas.Extensions`, compatível com **.NET Standard 2.1+**.

### Instalação

```bash
dotnet add package Rochas.CacheIndexer
```

### Uso rápido

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

### Ligando/desligando as features

#### Via propriedades

```csharp
indexer.EnableStemming = true;        // radicaliza termos antes de hashear
indexer.EnablePhoneticFilter = true;  // adiciona hash fonetico (Soundex PT-BR)
indexer.EnableSynonyms = false;       // desliga o dicionario de sinonimos
```

#### Via flag enum (todas de uma vez)

```csharp
using Rochas.CacheIndexer.Enumerators;

indexer.SetFeatures(CacheIndexerFeature.All);                    // sinonimos + stemming + fonetica
indexer.SetFeatures(CacheIndexerFeature.None);                   // tudo desligado
indexer.SetFeatures(CacheIndexerFeature.Synonyms | CacheIndexerFeature.Phonetic);
```

#### Efeito prático

| Feature | Comportamento | Exemplo |
|---|---|---|
| `Synonyms` | `fatura` expande para `boleto`, `duplicata`, `cobranca`... | busca por "boleto" acha "fatura" |
| `Stemming` | `pagamentos` -> `pagament` == `pagamento` -> `pagament` | busca por "pagamentos" acha "pagamento" |
| `Phonetic` | `casa` e `caza` geram o mesmo código Soundex (`C200`) | tolera erros de digitação/sons |

### API principal

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

### Estratégia de relevância (cobertura + ranking)

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

### Configuração (CacheIndexerConfig)

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

### Cache de objetos (ICacheProvider)

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

#### Provedores

| Provedor | Descrição | Use quando |
|---|---|---|
| `InMemoryCacheProvider` | `ConcurrentDictionary` thread-safe, chave = hash FNV do tipo + chave JSON | desenvolvimento, catálogos pequenos, L1 |
| `DistributedCacheProvider` | `IDistributedCache` — **Redis** ou **Microsoft Garnet** | multi-instância/pods, alta disponibilidade |
| `CompositeCacheProvider` | L1 in-memory + L2 distribuído, write-through e **promoção de L2→L1** na leitura | latência + compartilhamento |
| `PersistenceChannelCacheProvider` | canal assíncrono por assinante (fan-out real via `Subscribe`), consumidores persistindo em 1+ SGDB | replicação master→slave por evento |

#### Pipeline típico

```
L1 in-memory (microssegundos) → L2 distribuído Redis/Garnet (milissegundos) → banco SQL
```

- **Leitura** no composto: tenta L1 → em miss busca na L2 e promove para L1 (a partir da primeira leitura o item é servido em memória);
- **Escrita** no composto: L1 e L2 juntas (write-through).

#### Cache distribuído (Redis / Garnet)

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

#### Persistência por evento (master → 1+ SGDB)

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

#### Replicação automática para banco (Background Worker + DataDispatcher)

Para persistir os eventos do canal em um SGDB sem escrever o `foreach` na mão, use o **`PersistenceChannelWorker<T>`** (`BackgroundService` de `Microsoft.Extensions.Hosting`) + **`DataDispatcher<T>`**, que conecta no banco via `IGenericRepository<T>` (interface do `Rochas.DapperRepository.Specification`).

```csharp
using Rochas.CacheIndexer.Helpers;
using Rochas.DapperRepository.Specification.Interfaces;
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

| Ação do canal | Chamada no `IGenericRepository<T>` |
|---|---|
| `Put` | `Add(entity)` |
| `Del` | `Remove(filter)` |
| `Clear` / `Del(deleteAll: true)` | `NotSupportedException` (interface não expõe limpeza global) |

`DispatchAsync` é `virtual` — para replicação idempotente (upsert) ou suporte a `DeleteAll`/`Clear`, sobrescreva no consumidor. Falhas por mensagem são registradas no logger e o consumo continua.

#### Marcação de entidade cacheável

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

### Testes e cobertura

Suíte com **106 testes** (xUnit + FluentAssertions) medindo **95,69% de cobertura de linhas** (866/905) e **88,14% de branch** no assembly `Rochas.CacheIndexer` (coverlet + XPlat Code Coverage, filtrado apenas para `Rochas.CacheIndexer.dll`). A maioria dos componentes atinge 100% (`CacheIndexer` + `FindBestMatch`, `CompositeCacheProvider`, `DataCache`, `DistributedCacheProvider`, `PersistenceChannelCacheProvider`, `DataDispatcher<T>`, `PersistenceChannelWorker<T>`); `InMemoryCacheProvider` com 97%, `LexicalIndexEngine` com 92% e `PhoneticFilter` com 91%.

Cobertura por cenário: busca com cobertura de palavras + IDF, desempate por frequência em memória, busca por corpo/segmento, hashes pré-computados, todos os providers de cache e dispatcher/worker com falha por mensagem (log + continuidade).

### Licença

GPL v2 — livre para uso comercial e pessoal.

---

## Español

Índice léxico invertido en memoria para la búsqueda de conocimiento/respuestas con caché de hashes, segregación por segmento y **funciones de normalización activables/desactivables**, además de **proveedores de caché de objetos** conectables (en memoria, distribuido Redis/Garnet, compuesto y persistencia por evento):

- **Sinónimos** — diccionario PT-BR integrado (`pt_br_synonyms.json`) o personalizado;
- **Stemming** — Stemmer de Porter para PT-BR (`Rochas.PTStemmer`);
- **Soundex** — filtro fonético Soundex adaptado a PT-BR;
- **Caché de objetos** — `ICacheProvider` con `InMemoryCacheProvider`, `DistributedCacheProvider` (Redis/Garnet), `CompositeCacheProvider` (L1+L2) y `PersistenceChannelCacheProvider` (replicación asíncrona por evento a 1+ bases de datos).

Basado en `Rochas.PTStemmer` y `Rochas.Extensions`, compatible con **.NET Standard 2.1+**.

### Instalación

```bash
dotnet add package Rochas.CacheIndexer
```

### Inicio rápido

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

// Carga el índice a partir de los documentos
await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

// Búsqueda progresiva: base -> sinónimos -> stemming -> soundex
var result = await indexer.FindBestMatch("quero emitir uma fatura", loadDocs);
if (result.Found)
    Console.WriteLine($"Mejor doc: {result.BestId} (score {result.Score:F2}, tier {result.Tier})");
```

### Activando/desactivando las funciones

#### Mediante propiedades

```csharp
indexer.EnableStemming = true;        // radicaliza los términos antes de hacer el hash
indexer.EnablePhoneticFilter = true;  // añade el hash fonético (Soundex PT-BR)
indexer.EnableSynonyms = false;       // desactiva el diccionario de sinónimos
```

#### Mediante enum de flags (todas a la vez)

```csharp
using Rochas.CacheIndexer.Enumerators;

indexer.SetFeatures(CacheIndexerFeature.All);                    // sinónimos + stemming + fonética
indexer.SetFeatures(CacheIndexerFeature.None);                   // todo desactivado
indexer.SetFeatures(CacheIndexerFeature.Synonyms | CacheIndexerFeature.Phonetic);
```

#### Efecto práctico

| Función | Comportamiento | Ejemplo |
|---|---|---|
| `Synonyms` | `fatura` se expande a `boleto`, `duplicata`, `cobranca`... | buscar "boleto" encuentra "fatura" |
| `Stemming` | `pagamentos` -> `pagament` == `pagamento` -> `pagament` | buscar "pagamentos" encuentra "pagamento" |
| `Phonetic` | `casa` y `caza` generan el mismo código Soundex (`C200`) | tolera erratas/sonidos similares |

### API principal

| Método | Descripción |
|---|---|
| `EnsureIndexLoaded(loader)` | Construye el índice invertido (hash -> ids) y calcula la frecuencia de documentos (IDF) |
| `SearchIndex(query, minMatchScore, segmentId?)` | Busca en el índice: cobertura de palabras + ranking IDF |
| `Search(documents, query, minMatchScore)` | Búsqueda directa sobre una colección (sin indexación previa) |
| `FindBestMatch(message, loader, segmentId?)` | Búsqueda progresiva en 4 tiers, del más preciso al más permisivo |
| `ProcessText(title, body, documentId?)` | Tokeniza, aplica hash y actualiza la frecuencia en memoria (base del IDF) |
| `ExtractHashes(text)` / `ExtractHashes(text, syn, stem, sx)` | Extrae los hashes de un texto |
| `InvalidateIndex()` | Limpia el índice y fuerza la reindexación en el siguiente uso |
| `SetFeatures` / `GetFeatures` | Activa/desactiva funciones en conjunto |

### Estrategia de relevancia (cobertura + ranking)

Dos criterios combinados en la búsqueda:

1. **Cobertura (obtención)** — gana el contenido que hace match con el **máximo de palabras distintas** de la expresión. `Score = palabras coincidentes / palabras de la expresión`.

2. **Ranking (desempate por IDF)** — la **frecuencia de documentos** se mantiene en un **diccionario en memoria** y se actualiza **durante el `ProcessText`**: cuantos menos registros apunta ese hash/palabra, mayor su peso (`idf = ln(1 + N / (1 + df))`). El desempate se calcula **en el momento de la búsqueda** usando esa frecuencia actual.

Empatados en cobertura, gana quien tiene los términos más raros; como último recurso, el `Id` más pequeño.

```
Doc A: "fatura, boleto"          <- el término "cancelamento" es raro en el índice
Doc B: "cancelamento"            <- solo coincidió 1 palabra (rara)

query: "fatura boleto cancelamento"
-> Doc A gana (2/3 palabras) aunque su IDF por término sea menor
-> Doc B solo gana si la cobertura empata (1/1 x 1/1) y el término es más raro
```

Como la frecuencia vive en el diccionario en memoria, el aprendizaje en tiempo de ejecución cambia el ranking sin reindexar:

```csharp
indexer.ProcessText("fatura", null, documentId: 42); // incrementa df("fatura") en memoria
// la siguiente búsqueda ya recalcula el IDF de "fatura" en el desempate
```

> `TitleWeight`/`BodyWeight` fueron descontinuados (los pesos fijos por campo no reflejan la rareza).

### Configuración (CacheIndexerConfig)

```csharp
var config = new CacheIndexerConfig
{
    EnableStemming = false,
    EnablePhoneticFilter = false,
    EnableSynonyms = true,
    SynonymsFilePath = "custom_synonyms.json",   // ruta personalizada (opcional)
    LoadEmbeddedSynonyms = true,                 // fallback: diccionario integrado
    MinMatchScore = 0.3
};
```

### Caché de objetos (ICacheProvider)

Caché de entidades/POCOs con proveedores conectables, desacoplado de la implementación concreta. La fachada estática `DataCache` centraliza el acceso:

```csharp
using Rochas.CacheIndexer.Providers;

// Inicialización única (arranque de la aplicación):
DataCache.Initialize(new InMemoryCacheProvider());                              // default
DataCache.Initialize(memorySizeLimit: 100);                                     // en memoria limitado a 100 MB
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

#### Proveedores

| Proveedor | Descripción | Úsalo cuando |
|---|---|---|
| `InMemoryCacheProvider` | `ConcurrentDictionary` thread-safe, clave = hash FNV del tipo + clave JSON | desarrollo, catálogos pequeños, L1 |
| `DistributedCacheProvider` | `IDistributedCache` — **Redis** o **Microsoft Garnet** | multi-instancia/pods, alta disponibilidad |
| `CompositeCacheProvider` | L1 en memoria + L2 distribuido, write-through y **promoción de L2→L1** en la lectura | latencia + compartición |
| `PersistenceChannelCacheProvider` | canal asíncrono por suscriptor (fan-out real vía `Subscribe`), consumidores persistiendo en 1+ bases de datos | replicación master→slave por evento |

#### Pipeline típico

```
L1 en memoria (microsegundos) → L2 distribuido Redis/Garnet (milisegundos) → base de datos SQL
```

- **Lectura** en el compuesto: intenta L1 → en miss busca en la L2 y promueve a L1 (desde la primera lectura el ítem se sirve en memoria);
- **Escritura** en el compuesto: L1 y L2 juntas (write-through).

#### Caché distribuido (Redis / Garnet)

`DistributedCacheProvider` usa la abstracción `IDistributedCache` — funciona con cualquier implementación compatible. Para ASP.NET Core (inyección de dependencias):

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

**Microsoft Garnet** es un servidor compatible con Redis; basta apuntar el cliente Redis al endpoint Garnet — el `DistributedCacheProvider` funciona sin cambios.

#### Persistencia por evento (master → 1+ bases de datos)

El master escribe en la caché local y publica en el canal; cada consumidor (slave) se suscribe y recibe una **copia** de cada evento (canal privado por suscriptor — fan-out real):

```csharp
// Slave A (BD A):
var readerA = provider.Subscribe(capacity: 1000);
await foreach (var msg in readerA.ReadAllAsync())
{
    switch (msg.Action)
    {
        case PersistenceChannelCacheProvider.ChannelAction.Put:
            await repoA.AddAsync(msg.CacheItem);      // BD A
            break;
        case PersistenceChannelCacheProvider.ChannelAction.Del:
            await repoA.RemoveAsync(msg.CacheKey);    // BD B
            break;
        case PersistenceChannelCacheProvider.ChannelAction.Clear:
            await repoA.ClearAsync();
            break;
    }
}

// Slave B (BD B): suscripción idéntica, sin afectar al Slave A.
```

Conveniencia para un solo consumidor: `await foreach (var msg in provider.Consume(ct))`.

Backpressure: canales bounded (`Wait`); un consumidor lento más allá de la capacidad descarta eventos solo para él. `Subscribe(capacity <= 0)` crea un canal unbounded (sin pérdidas).

#### Replicación automática a base de datos (Background Worker + DataDispatcher)

Para persistir los eventos del canal en una base de datos sin escribir el `foreach` a mano, usa **`PersistenceChannelWorker<T>`** (`BackgroundService` de `Microsoft.Extensions.Hosting`) + **`DataDispatcher<T>`**, que se conecta a la base de datos vía `IGenericRepository<T>` (interfaz de `Rochas.DapperRepository.Specification`).

```csharp
using Rochas.CacheIndexer.Helpers;
using Rochas.DapperRepository.Specification.Interfaces;
using Rochas.DapperRepository;

// Master publica en el canal (como antes):
DataCache.Initialize(new PersistenceChannelCacheProvider(new InMemoryCacheProvider()));

// Slave: registra el worker en el DI (consumo + persistencia en la BD del slave):
var slaveRepo = new GenericRepository<Product>(DatabaseEngine.SQLite, slaveConnString);
var dispatcher = new DataDispatcher<Product>(slaveRepo);

// ASP.NET Core:
builder.Services.AddHostedService(sp =>
    new PersistenceChannelWorker<Product>(channelProvider, dispatcher));
```

Mapeo de acciones en `DataDispatcher<T>`:

| Acción del canal | Llamada en `IGenericRepository<T>` |
|---|---|
| `Put` | `Add(entity)` |
| `Del` | `Remove(filter)` |
| `Clear` / `Del(deleteAll: true)` | `NotSupportedException` (la interfaz no expone limpieza global) |

`DispatchAsync` es `virtual` — para replicación idempotente (upsert) o soporte de `DeleteAll`/`Clear`, sobrescríbelo en el consumidor. Los fallos por mensaje se registran en el logger y el consumo continúa.

#### Marcando una entidad cacheable

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

### Pruebas y cobertura

Suite con **106 pruebas** (xUnit + FluentAssertions) midiendo **95,69% de cobertura de líneas** (866/905) y **88,14% de branch** en el ensamblado `Rochas.CacheIndexer` (coverlet + XPlat Code Coverage, filtrado solo para `Rochas.CacheIndexer.dll`). La mayoría de los componentes alcanza el 100% (`CacheIndexer` + `FindBestMatch`, `CompositeCacheProvider`, `DataCache`, `DistributedCacheProvider`, `PersistenceChannelCacheProvider`, `DataDispatcher<T>`, `PersistenceChannelWorker<T>`); `InMemoryCacheProvider` con 97%, `LexicalIndexEngine` con 92% y `PhoneticFilter` con 91%.

Cobertura por escenario: búsqueda con cobertura de palabras + IDF, desempate por frecuencia en memoria, búsqueda por cuerpo/segmento, hashes precalculados, todos los proveedores de caché y dispatcher/worker con fallo por mensaje (log + continuidad).

### Licencia

GPL v2 — libre para uso comercial y personal.

---

## Français

Index lexical inversé en mémoire pour la recherche de connaissances/réponses avec cache de hachages, ségrégation par segment et **fonctionnalités de normalisation activables/désactivables**, plus des **fournisseurs de cache d'objets** enfichables (en mémoire, distribué Redis/Garnet, composite et persistance par événement) :

- **Synonymes** — dictionnaire PT-BR embarqué (`pt_br_synonyms.json`) ou personnalisé ;
- **Stemming** — stemmer de Porter pour le PT-BR (`Rochas.PTStemmer`) ;
- **Soundex** — filtre phonétique Soundex adapté au PT-BR ;
- **Cache d'objets** — `ICacheProvider` avec `InMemoryCacheProvider`, `DistributedCacheProvider` (Redis/Garnet), `CompositeCacheProvider` (L1+L2) et `PersistenceChannelCacheProvider` (réplication asynchrone par événement vers 1+ bases de données).

Basé sur `Rochas.PTStemmer` et `Rochas.Extensions`, compatible avec **.NET Standard 2.1+**.

### Installation

```bash
dotnet add package Rochas.CacheIndexer
```

### Démarrage rapide

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

// Charge l'index à partir des documents
await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

// Recherche progressive : base -> synonymes -> stemming -> soundex
var result = await indexer.FindBestMatch("quero emitir uma fatura", loadDocs);
if (result.Found)
    Console.WriteLine($"Meilleur doc : {result.BestId} (score {result.Score:F2}, tier {result.Tier})");
```

### Activer/désactiver les fonctionnalités

#### Via les propriétés

```csharp
indexer.EnableStemming = true;        // radicalise les termes avant le hachage
indexer.EnablePhoneticFilter = true;  // ajoute le hachage phonétique (Soundex PT-BR)
indexer.EnableSynonyms = false;       // désactive le dictionnaire de synonymes
```

#### Via un enum de flags (tout d'un coup)

```csharp
using Rochas.CacheIndexer.Enumerators;

indexer.SetFeatures(CacheIndexerFeature.All);                    // synonymes + stemming + phonétique
indexer.SetFeatures(CacheIndexerFeature.None);                   // tout désactivé
indexer.SetFeatures(CacheIndexerFeature.Synonyms | CacheIndexerFeature.Phonetic);
```

#### Effet pratique

| Fonctionnalité | Comportement | Exemple |
|---|---|---|
| `Synonyms` | `fatura` s'étend en `boleto`, `duplicata`, `cobranca`... | rechercher "boleto" trouve "fatura" |
| `Stemming` | `pagamentos` -> `pagament` == `pagamento` -> `pagament` | rechercher "pagamentos" trouve "pagamento" |
| `Phonetic` | `casa` et `caza` produisent le même code Soundex (`C200`) | tolère les fautes de frappe/sons similaires |

### API principale

| Méthode | Description |
|---|---|
| `EnsureIndexLoaded(loader)` | Construit l'index inversé (hash -> ids) et calcule la fréquence de documents (IDF) |
| `SearchIndex(query, minMatchScore, segmentId?)` | Recherche dans l'index : couverture de mots + classement IDF |
| `Search(documents, query, minMatchScore)` | Recherche directe sur une collection (sans indexation préalable) |
| `FindBestMatch(message, loader, segmentId?)` | Recherche progressive sur 4 tiers, du plus précis au plus permissif |
| `ProcessText(title, body, documentId?)` | Tokenise, hache et met à jour la fréquence en mémoire (base de l'IDF) |
| `ExtractHashes(text)` / `ExtractHashes(text, syn, stem, sx)` | Extrait les hachages d'un texte |
| `InvalidateIndex()` | Vide l'index et force une réindexation à la prochaine utilisation |
| `SetFeatures` / `GetFeatures` | Active/désactive les fonctionnalités ensemble |

### Stratégie de pertinence (couverture + classement)

Deux critères combinés dans la recherche :

1. **Couverture (récupération)** — gagne le contenu qui correspond au **maximum de mots distincts** de l'expression. `Score = mots correspondants / mots de l'expression`.

2. **Classement (départage par IDF)** — la **fréquence de documents** est conservée dans un **dictionnaire en mémoire** et mise à jour **pendant `ProcessText`** : moins ce hash/mot pointe vers d'enregistrements, plus son poids est élevé (`idf = ln(1 + N / (1 + df))`). Le départage est calculé **au moment de la recherche** avec cette fréquence courante.

À égalité de couverture, celui qui a les termes les plus rares gagne ; en dernier recours, le plus petit `Id`.

```
Doc A : "fatura, boleto"          <- le terme "cancelamento" est rare dans l'index
Doc B : "cancelamento"            <- seulement 1 mot (rare) correspondu

query : "fatura boleto cancelamento"
-> Doc A gagne (2/3 mots) même avec un IDF par terme plus faible
-> Doc B ne gagne que si la couverture est à égalité (1/1 x 1/1) et le terme plus rare
```

Comme la fréquence vit dans le dictionnaire en mémoire, l'apprentissage en runtime modifie le classement sans réindexer :

```csharp
indexer.ProcessText("fatura", null, documentId: 42); // incrémente df("fatura") en mémoire
// la prochaine recherche recalcule déjà l'IDF de "fatura" lors du départage
```

> `TitleWeight`/`BodyWeight` ont été abandonnés (des poids fixes par champ ne reflètent pas la rareté).

### Configuration (CacheIndexerConfig)

```csharp
var config = new CacheIndexerConfig
{
    EnableStemming = false,
    EnablePhoneticFilter = false,
    EnableSynonyms = true,
    SynonymsFilePath = "custom_synonyms.json",   // chemin personnalisé (facultatif)
    LoadEmbeddedSynonyms = true,                 // repli : dictionnaire embarqué
    MinMatchScore = 0.3
};
```

### Cache d'objets (ICacheProvider)

Cache d'entités/POCO avec des fournisseurs enfichables, découplé de l'implémentation concrète. La façade statique `DataCache` centralise l'accès :

```csharp
using Rochas.CacheIndexer.Providers;

// Initialisation unique (démarrage de l'application) :
DataCache.Initialize(new InMemoryCacheProvider());                              // défaut
DataCache.Initialize(memorySizeLimit: 100);                                     // en mémoire limité à 100 Mo
DataCache.Initialize(new DistributedCacheProvider("localhost:6379"));           // Redis/Garnet
DataCache.Initialize(new CompositeCacheProvider(                                 // L1 + L2
    new InMemoryCacheProvider(),
    new DistributedCacheProvider("localhost:6379")));
DataCache.Initialize(new PersistenceChannelCacheProvider(new InMemoryCacheProvider())); // master

// Utilisation :
DataCache.Put(new Product { Id = 1 }, product);
var product = DataCache.Get(new Product { Id = 1 });
DataCache.Del(new Product { Id = 1 }, deleteAll: true);
DataCache.Clear();
```

#### Fournisseurs

| Fournisseur | Description | Utiliser quand |
|---|---|---|
| `InMemoryCacheProvider` | `ConcurrentDictionary` thread-safe, clé = hash FNV du type + clé JSON | développement, petits catalogues, L1 |
| `DistributedCacheProvider` | `IDistributedCache` — **Redis** ou **Microsoft Garnet** | multi-instances/pods, haute disponibilité |
| `CompositeCacheProvider` | L1 en mémoire + L2 distribué, write-through et **promotion L2→L1** en lecture | latence + partage |
| `PersistenceChannelCacheProvider` | canal asynchrone par abonné (vrai fan-out via `Subscribe`), consommateurs persistant sur 1+ bases de données | réplication master→slave par événement |

#### Pipeline type

```
L1 en mémoire (microsecondes) → L2 distribué Redis/Garnet (millisecondes) → base de données SQL
```

- **Lecture** sur le composite : essaie L1 → en cas de miss, lit la L2 et promeut vers L1 (dès la première lecture, l'élément est servi en mémoire) ;
- **Écriture** sur le composite : L1 et L2 ensemble (write-through).

#### Cache distribué (Redis / Garnet)

`DistributedCacheProvider` utilise l'abstraction `IDistributedCache` — fonctionne avec toute implémentation compatible. Pour ASP.NET Core (injection de dépendances) :

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

**Microsoft Garnet** est un serveur compatible Redis ; pointez simplement le client Redis vers le point de terminaison Garnet — le `DistributedCacheProvider` fonctionne sans modification.

#### Persistance par événement (master → 1+ bases de données)

Le master écrit dans le cache local et publie sur le canal ; chaque consommateur (slave) s'abonne et reçoit une **copie** de chaque événement (canal privé par abonné — vrai fan-out) :

```csharp
// Slave A (BD A) :
var readerA = provider.Subscribe(capacity: 1000);
await foreach (var msg in readerA.ReadAllAsync())
{
    switch (msg.Action)
    {
        case PersistenceChannelCacheProvider.ChannelAction.Put:
            await repoA.AddAsync(msg.CacheItem);      // BD A
            break;
        case PersistenceChannelCacheProvider.ChannelAction.Del:
            await repoA.RemoveAsync(msg.CacheKey);    // BD B
            break;
        case PersistenceChannelCacheProvider.ChannelAction.Clear:
            await repoA.ClearAsync();
            break;
    }
}

// Slave B (BD B) : abonnement identique, sans affecter le Slave A.
```

Convenance pour un seul consommateur : `await foreach (var msg in provider.Consume(ct))`.

Backpressure : canaux bornés (`Wait`) ; un consommateur lent au-delà de la capacité abandonne des événements seulement pour lui. `Subscribe(capacity <= 0)` crée un canal illimité (aucune perte).

#### Réplication automatique vers la base de données (Background Worker + DataDispatcher)

Pour persister les événements du canal dans une base de données sans écrire le `foreach` à la main, utilisez **`PersistenceChannelWorker<T>`** (`BackgroundService` de `Microsoft.Extensions.Hosting`) + **`DataDispatcher<T>`**, qui se connecte à la base via `IGenericRepository<T>` (interface de `Rochas.DapperRepository.Specification`).

```csharp
using Rochas.CacheIndexer.Helpers;
using Rochas.DapperRepository.Specification.Interfaces;
using Rochas.DapperRepository;

// Master publie sur le canal (comme avant) :
DataCache.Initialize(new PersistenceChannelCacheProvider(new InMemoryCacheProvider()));

// Slave : enregistrez le worker dans le DI (consommation + persistance dans la BD du slave) :
var slaveRepo = new GenericRepository<Product>(DatabaseEngine.SQLite, slaveConnString);
var dispatcher = new DataDispatcher<Product>(slaveRepo);

// ASP.NET Core :
builder.Services.AddHostedService(sp =>
    new PersistenceChannelWorker<Product>(channelProvider, dispatcher));
```

Mappage des actions dans `DataDispatcher<T>` :

| Action du canal | Appel sur `IGenericRepository<T>` |
|---|---|
| `Put` | `Add(entity)` |
| `Del` | `Remove(filter)` |
| `Clear` / `Del(deleteAll: true)` | `NotSupportedException` (l'interface n'expose pas de nettoyage global) |

`DispatchAsync` est `virtual` — pour une réplication idempotente (upsert) ou la prise en charge de `DeleteAll`/`Clear`, remplacez-le dans le consommateur. Les échecs par message sont consignés dans le logger et la consommation continue.

#### Marquer une entité cacheable

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

### Tests et couverture

Suite de **106 tests** (xUnit + FluentAssertions) mesurant **95,69 % de couverture de lignes** (866/905) et **88,14 % de branches** dans l'assembly `Rochas.CacheIndexer` (coverlet + XPlat Code Coverage, filtré uniquement sur `Rochas.CacheIndexer.dll`). La plupart des composants atteignent 100 % (`CacheIndexer` + `FindBestMatch`, `CompositeCacheProvider`, `DataCache`, `DistributedCacheProvider`, `PersistenceChannelCacheProvider`, `DataDispatcher<T>`, `PersistenceChannelWorker<T>`) ; `InMemoryCacheProvider` à 97 %, `LexicalIndexEngine` à 92 % et `PhoneticFilter` à 91 %.

Couverture par scénario : recherche avec couverture de mots + IDF, départage par fréquence en mémoire, recherche par corps/segment, hachages précalculés, tous les fournisseurs de cache et dispatcher/worker avec échec par message (log + continuité).

### Licence

GPL v2 — libre pour un usage commercial et personnel.

---

## Deutsch

In-Memory-invertierter lexikalischer Index für die Suche nach Wissen/Antworten mit Hash-Cache, Segmentierung pro Segment und **ein-/ausschaltbaren Normalisierungsfunktionen**, plus **ansteckbare Objekt-Cache-Anbieter** (In-Memory, verteilt Redis/Garnet, zusammengesetzt und ereignisbasierte Persistenz):

- **Synonyme** — eingebettetes PT-BR-Wörterbuch (`pt_br_synonyms.json`) oder benutzerdefiniert;
- **Stemming** — Porter-Stemmer für PT-BR (`Rochas.PTStemmer`);
- **Soundex** — phonetischer Soundex-Filter, angepasst an PT-BR;
- **Objekt-Cache** — `ICacheProvider` mit `InMemoryCacheProvider`, `DistributedCacheProvider` (Redis/Garnet), `CompositeCacheProvider` (L1+L2) und `PersistenceChannelCacheProvider` (asynchrone ereignisbasierte Replikation auf 1+ Datenbanken).

Basiert auf `Rochas.PTStemmer` und `Rochas.Extensions`, kompatibel mit **.NET Standard 2.1+**.

### Installation

```bash
dotnet add package Rochas.CacheIndexer
```

### Schnellstart

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

// Lädt den Index aus den Dokumenten
await indexer.EnsureIndexLoaded(() => Task.FromResult<IReadOnlyList<IndexedDocument>>(docs));

// Progressive Suche: base -> Synonyme -> Stemming -> Soundex
var result = await indexer.FindBestMatch("quero emitir uma fatura", loadDocs);
if (result.Found)
    Console.WriteLine($"Bestes Doc: {result.BestId} (Score {result.Score:F2}, Tier {result.Tier})");
```

### Funktionen ein-/ausschalten

#### Über Eigenschaften

```csharp
indexer.EnableStemming = true;        // radikalisiert Begriffe vor dem Hashing
indexer.EnablePhoneticFilter = true;  // fügt phonetischen Hash hinzu (PT-BR Soundex)
indexer.EnableSynonyms = false;       // deaktiviert das Synonymwörterbuch
```

#### Über Flag-Enum (alles auf einmal)

```csharp
using Rochas.CacheIndexer.Enumerators;

indexer.SetFeatures(CacheIndexerFeature.All);                    // Synonyme + Stemming + Phonetik
indexer.SetFeatures(CacheIndexerFeature.None);                   // alles aus
indexer.SetFeatures(CacheIndexerFeature.Synonyms | CacheIndexerFeature.Phonetic);
```

#### Praktischer Effekt

| Funktion | Verhalten | Beispiel |
|---|---|---|
| `Synonyms` | `fatura` erweitert sich zu `boleto`, `duplicata`, `cobranca`... | Suche nach "boleto" findet "fatura" |
| `Stemming` | `pagamentos` -> `pagament` == `pagamento` -> `pagament` | Suche nach "pagamentos" findet "pagamento" |
| `Phonetic` | `casa` und `caza` erzeugen denselben Soundex-Code (`C200`) | toleriert Tippfehler/ähnliche Laute |

### Haupt-API

| Methode | Beschreibung |
|---|---|
| `EnsureIndexLoaded(loader)` | Baut den invertierten Index (Hash -> ids) und berechnet die Dokumentfrequenz (IDF) |
| `SearchIndex(query, minMatchScore, segmentId?)` | Suche im Index: Wortabdeckung + IDF-Ranking |
| `Search(documents, query, minMatchScore)` | Direkte Suche über eine Sammlung (ohne vorherige Indexierung) |
| `FindBestMatch(message, loader, segmentId?)` | Progressive Suche über 4 Tiers, vom präzisesten zum permissivsten |
| `ProcessText(title, body, documentId?)` | Tokenisiert, hasht und aktualisiert die In-Memory-Frequenz (Grundlage für IDF) |
| `ExtractHashes(text)` / `ExtractHashes(text, syn, stem, sx)` | Extrahiert Hashes aus einem Text |
| `InvalidateIndex()` | Leert den Index und erzwingt eine Neuindexierung bei der nächsten Verwendung |
| `SetFeatures` / `GetFeatures` | Schaltet Funktionen gemeinsam um |

### Relevanzstrategie (Abdeckung + Ranking)

Zwei kombinierte Kriterien bei der Suche:

1. **Abdeckung (Abruf)** — gewonnen hat der Inhalt, der mit der **maximalen Anzahl unterschiedlicher Wörter** der Abfrage übereinstimmt. `Score = übereinstimmende Wörter / Abfragewörter`.

2. **Ranking (Tie-Break per IDF)** — die **Dokumentfrequenz** wird in einem **In-Memory-Wörterbuch** gehalten und **während `ProcessText`** aktualisiert: Je weniger Datensätze ein Hash/Wort referenziert, desto höher sein Gewicht (`idf = ln(1 + N / (1 + df))`). Der Tie-Break wird **zum Zeitpunkt der Suche** mit der aktuellen Frequenz berechnet.

Bei Gleichstand in der Abdeckung gewinnt, wer die selteneren Begriffe hat; als letzte Instanz der kleinste `Id`.

```
Doc A: "fatura, boleto"          <- Begriff "cancelamento" ist im Index selten
Doc B: "cancelamento"            <- nur 1 (seltenes) Wort gematcht

query: "fatura boleto cancelamento"
-> Doc A gewinnt (2/3 Wörter), selbst bei niedrigerem IDF pro Begriff
-> Doc B gewinnt nur bei Abdeckungsgleichstand (1/1 x 1/1) und seltenerem Begriff
```

Da die Frequenz im In-Memory-Wörterbuch lebt, verändert Runtime-Lernen das Ranking ohne Neuindexierung:

```csharp
indexer.ProcessText("fatura", null, documentId: 42); // erhöht df("fatura") im Speicher
// die nächste Suche berechnet das IDF von "fatura" bereits im Tie-Break neu
```

> `TitleWeight`/`BodyWeight` wurden eingestellt (feste Gewichte pro Feld spiegeln Seltenheit nicht wider).

### Konfiguration (CacheIndexerConfig)

```csharp
var config = new CacheIndexerConfig
{
    EnableStemming = false,
    EnablePhoneticFilter = false,
    EnableSynonyms = true,
    SynonymsFilePath = "custom_synonyms.json",   // benutzerdefinierter Pfad (optional)
    LoadEmbeddedSynonyms = true,                 // Fallback: eingebettetes Wörterbuch
    MinMatchScore = 0.3
};
```

### Objekt-Cache (ICacheProvider)

Objekte/POCOs mit ansteckbaren Anbietern cachen, entkoppelt von konkreten Implementierungen. Die statische Fassade `DataCache` bündelt den Zugriff:

```csharp
using Rochas.CacheIndexer.Providers;

// Einmalige Initialisierung (App-Start):
DataCache.Initialize(new InMemoryCacheProvider());                              // default
DataCache.Initialize(memorySizeLimit: 100);                                     // In-Memory auf 100 MB begrenzt
DataCache.Initialize(new DistributedCacheProvider("localhost:6379"));           // Redis/Garnet
DataCache.Initialize(new CompositeCacheProvider(                                 // L1 + L2
    new InMemoryCacheProvider(),
    new DistributedCacheProvider("localhost:6379")));
DataCache.Initialize(new PersistenceChannelCacheProvider(new InMemoryCacheProvider())); // master

// Verwendung:
DataCache.Put(new Product { Id = 1 }, product);
var product = DataCache.Get(new Product { Id = 1 });
DataCache.Del(new Product { Id = 1 }, deleteAll: true);
DataCache.Clear();
```

#### Anbieter

| Anbieter | Beschreibung | Verwenden, wenn |
|---|---|---|
| `InMemoryCacheProvider` | Thread-sicheres `ConcurrentDictionary`, Schlüssel = FNV-Hash des Typs + JSON-Schlüssel | Entwicklung, kleine Kataloge, L1 |
| `DistributedCacheProvider` | `IDistributedCache` — **Redis** oder **Microsoft Garnet** | Multi-Instanz/Pods, hohe Verfügbarkeit |
| `CompositeCacheProvider` | L1 In-Memory + L2 verteilt, Write-through und **L2→L1-Promotion** beim Lesen | Latenz + Freigabe |
| `PersistenceChannelCacheProvider` | asynchroner Kanal pro Abonnent (echtes Fan-out via `Subscribe`), Verbraucher persistieren auf 1+ Datenbanken | Master→Slave-Ereignisreplikation |

#### Typische Pipeline

```
L1 In-Memory (Mikrosekunden) → L2 verteilt Redis/Garnet (Millisekunden) → SQL-Datenbank
```

- **Lesen** im Composite: versucht L1 → bei Miss L2 lesen und nach L1 befördern (ab der ersten Lektüre wird das Element aus dem Speicher bedient);
- **Schreiben** im Composite: L1 und L2 zusammen (Write-through).

#### Verteilte Cache (Redis / Garnet)

`DistributedCacheProvider` nutzt die Abstraktion `IDistributedCache` — funktioniert mit jeder kompatiblen Implementierung. Für ASP.NET Core (Dependency Injection):

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

**Microsoft Garnet** ist ein Redis-kompatibler Server; einfach den Redis-Client auf den Garnet-Endpunkt richten — der `DistributedCacheProvider` funktioniert unverändert.

#### Ereignisbasierte Persistenz (Master → 1+ Datenbanken)

Der Master schreibt in den lokalen Cache und veröffentlicht im Kanal; jeder Verbraucher (Slave) abonniert und erhält eine **Kopie** jedes Ereignisses (privater Kanal pro Abonnent — echtes Fan-out):

```csharp
// Slave A (DB A):
var readerA = provider.Subscribe(capacity: 1000);
await foreach (var msg in readerA.ReadAllAsync())
{
    switch (msg.Action)
    {
        case PersistenceChannelCacheProvider.ChannelAction.Put:
            await repoA.AddAsync(msg.CacheItem);      // DB A
            break;
        case PersistenceChannelCacheProvider.ChannelAction.Del:
            await repoA.RemoveAsync(msg.CacheKey);    // DB B
            break;
        case PersistenceChannelCacheProvider.ChannelAction.Clear:
            await repoA.ClearAsync();
            break;
    }
}

// Slave B (DB B): identisches Abo, ohne Slave A zu beeinflussen.
```

Komfort für einen einzelnen Verbraucher: `await foreach (var msg in provider.Consume(ct))`.

Backpressure: gebundene Kanäle (`Wait`); ein langsamer Verbraucher über der Kapazität verwirft Ereignisse nur für sich selbst. `Subscribe(capacity <= 0)` erzeugt einen unbegrenzten Kanal (keine Verluste).

#### Automatische DB-Replikation (Background Worker + DataDispatcher)

Um Kanalereignisse ohne manuelles `foreach` in einer Datenbank zu persistieren, verwende **`PersistenceChannelWorker<T>`** (`BackgroundService` aus `Microsoft.Extensions.Hosting`) + **`DataDispatcher<T>`**, das über `IGenericRepository<T>` (Schnittstelle aus `Rochas.DapperRepository.Specification`) mit der Datenbank verbindet.

```csharp
using Rochas.CacheIndexer.Helpers;
using Rochas.DapperRepository.Specification.Interfaces;
using Rochas.DapperRepository;

// Master veröffentlicht im Kanal (wie zuvor):
DataCache.Initialize(new PersistenceChannelCacheProvider(new InMemoryCacheProvider()));

// Slave: Worker im DI registrieren (Konsum + Persistenz in der Slave-DB):
var slaveRepo = new GenericRepository<Product>(DatabaseEngine.SQLite, slaveConnString);
var dispatcher = new DataDispatcher<Product>(slaveRepo);

// ASP.NET Core:
builder.Services.AddHostedService(sp =>
    new PersistenceChannelWorker<Product>(channelProvider, dispatcher));
```

Aktionszuordnung in `DataDispatcher<T>`:

| Kanalaktion | Aufruf auf `IGenericRepository<T>` |
|---|---|
| `Put` | `Add(entity)` |
| `Del` | `Remove(filter)` |
| `Clear` / `Del(deleteAll: true)` | `NotSupportedException` (Schnittstelle bietet keine globale Bereinigung) |

`DispatchAsync` ist `virtual` — für idempotente Replikation (Upsert) oder `DeleteAll`/`Clear`-Unterstützung im Verbraucher überschreiben. Fehler pro Nachricht werden protokolliert und der Konsum läuft weiter.

#### Cachebare Entität markieren

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

### Tests und Abdeckung

Suite mit **106 Tests** (xUnit + FluentAssertions), die **95,69 % Zeilenabdeckung** (866/905) und **88,14 % Branch** in der Assembly `Rochas.CacheIndexer` misst (coverlet + XPlat Code Coverage, nur auf `Rochas.CacheIndexer.dll` gefiltert). Die meisten Komponenten erreichen 100 % (`CacheIndexer` + `FindBestMatch`, `CompositeCacheProvider`, `DataCache`, `DistributedCacheProvider`, `PersistenceChannelCacheProvider`, `DataDispatcher<T>`, `PersistenceChannelWorker<T>`); `InMemoryCacheProvider` 97 %, `LexicalIndexEngine` 92 % und `PhoneticFilter` 91 %.

Szenarioabdeckung: Suche mit Wortabdeckung + IDF, Tie-Break per In-Memory-Frequenz, Suche nach Body/Segment, vorberechnete Hashes, alle Cache-Anbieter und Dispatcher/Worker mit Fehler pro Nachricht (Log + Kontinuität).

### Lizenz

GPL v2 — frei für kommerzielle und private Nutzung.
