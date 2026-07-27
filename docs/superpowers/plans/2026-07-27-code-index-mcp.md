# code-index-mcp Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Локальный MCP-сервер, дающий семантический поиск по C#-коду через векторный индекс на Ollama-эмбеддингах, чтобы заменить широкие заходы `Grep` и сократить расход контекста.

**Architecture:** Ядро `CodeIndex.Core` не знает о протоколе MCP: чанкинг через Roslyn на уровне членов типа, эмбеддинги через `IEmbeddingClient`, доступ к файлам только через `ISourceProvider`, плоский `float32`-кэш с zero-copy чтением и SIMD-косинусом. `CodeIndex.Server` — тонкая stdio-обвязка с четырьмя инструментами. Поиск гибридный: векторная и символьная ветки сливаются через Reciprocal Rank Fusion.

**Tech Stack:** .NET 10, `Microsoft.CodeAnalysis.CSharp` (Roslyn), `System.Numerics.Tensors`, `ModelContextProtocol` 1.3.0, `xunit.v3` со встроенным `Assert` (без FluentAssertions и Moq).

**Spec:** `docs/superpowers/specs/2026-07-27-code-index-mcp-design.md`

---

## Обязательные соглашения по стилю

Действуют во всех задачах, повторно не проговариваются:

- Явные типы вместо `var` везде, где тип не анонимный.
- `async`/`await` для всех IO, `CancellationToken` протаскивается по цепочке вызовов.
- `ConfigureAwait(false)` в коде `CodeIndex.Core` (это библиотека).
- Приватные поля — `_camelCase`, публичные члены — `PascalCase`.
- `StringComparison.Ordinal` или `OrdinalIgnoreCase` при сравнении строк.
- Комментарии только на английском и только там, где код неочевиден.
- **Запрещены поэлементные циклы по элементам векторов** — при чтении, записи и вычислении сходства. Только блочные операции и SIMD.

---

## Карта файлов

| Файл | Ответственность |
|---|---|
| `src/CodeIndex.Core/Sources/ISourceProvider.cs` | Контракт доступа к исходникам |
| `src/CodeIndex.Core/Sources/FileSystemSourceProvider.cs` | Единственная боевая реализация — локальная ФС |
| `src/CodeIndex.Core/Chunking/CodeChunk.cs` | Модель чанка |
| `src/CodeIndex.Core/Chunking/RoslynChunker.cs` | Разбор C# на члены типов |
| `src/CodeIndex.Core/Chunking/FallbackChunker.cs` | Разбиение по строкам, когда Roslyn не справился |
| `src/CodeIndex.Core/Chunking/ChunkerPipeline.cs` | Выбор чанкера и защита от падения парсера |
| `src/CodeIndex.Core/CodeIndexOptions.cs` | `ProjectId`, корень проекта, каталог кэша |
| `src/CodeIndex.Core/Indexing/FileFingerprint.cs` | Отпечаток файла для проверки свежести |
| `src/CodeIndex.Core/Indexing/FileScanner.cs` | Обход проекта и отбор `.cs` |
| `src/CodeIndex.Core/Indexing/IndexBuilder.cs` | Полная и инкрементальная сборка |
| `src/CodeIndex.Core/Embedding/IEmbeddingClient.cs` | Контракт вычисления векторов |
| `src/CodeIndex.Core/Embedding/OllamaEmbeddingClient.cs` | HTTP-клиент Ollama, батчинг, усечение |
| `src/CodeIndex.Core/Storage/IndexHeader.cs` | Заголовок кэша и его валидация |
| `src/CodeIndex.Core/Storage/IndexSnapshot.cs` | Индекс в памяти: чанки, отпечатки, плоский массив векторов |
| `src/CodeIndex.Core/Storage/IndexStore.cs` | Чтение и запись `manifest.json` / `vectors.bin` |
| `src/CodeIndex.Core/Search/SearchHit.cs` | Модели результата поиска |
| `src/CodeIndex.Core/Search/VectorSearcher.cs` | SIMD-косинус по всему массиву |
| `src/CodeIndex.Core/Search/SymbolMatcher.cs` | Совпадения по именам символов |
| `src/CodeIndex.Core/Search/HybridRanker.cs` | Слияние двух веток через RRF |
| `src/CodeIndex.Core/Search/CodeIndexService.cs` | Фасад: обновление перед запросом, фильтры, деградация |
| `src/CodeIndex.Server/Tools/CodeSearchTools.cs` | Четыре инструмента MCP |
| `src/CodeIndex.Server/Program.cs` | stdio-хост, DI, конфигурация |

---

## Task 0: Каркас решения

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`
- Create: `src/CodeIndex.Core/CodeIndex.Core.csproj`
- Create: `src/CodeIndex.Server/CodeIndex.Server.csproj`
- Create: `tests/CodeIndex.Core.Tests/CodeIndex.Core.Tests.csproj`
- Create: `tests/CodeIndex.Server.Tests/CodeIndex.Server.Tests.csproj`

- [ ] **Step 1: Создать проекты и решение**

```bash
cd <repo>
dotnet new sln -n CodeIndexMcp
dotnet new classlib -n CodeIndex.Core -o src/CodeIndex.Core
dotnet new console -n CodeIndex.Server -o src/CodeIndex.Server
dotnet new classlib -n CodeIndex.Core.Tests -o tests/CodeIndex.Core.Tests
dotnet new classlib -n CodeIndex.Server.Tests -o tests/CodeIndex.Server.Tests
rm src/CodeIndex.Core/Class1.cs tests/CodeIndex.Core.Tests/Class1.cs tests/CodeIndex.Server.Tests/Class1.cs
dotnet sln add src/CodeIndex.Core src/CodeIndex.Server tests/CodeIndex.Core.Tests tests/CodeIndex.Server.Tests
```

Тестовые проекты создаются как `classlib`, а не по шаблону `xunit`, потому что шаблон тянет xunit v2 — пакеты v3 добавляются вручную на шаге 4.

- [ ] **Step 2: Записать `global.json`**

```json
{
  "sdk": {
    "version": "10.0.203",
    "rollForward": "latestFeature"
  }
}
```

- [ ] **Step 3: Записать `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Записать `Directory.Packages.props`**

`FluentAssertions`, `Moq` и `AutoFixture` сюда не добавляются — это осознанное отличие от остальных репозиториев, обоснование в разделе 11 спеки.

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>
  </PropertyGroup>
  <ItemGroup>
    <!-- MCP -->
    <PackageVersion Include="ModelContextProtocol" Version="1.3.0" />
    <!-- Hosting / DI / Configuration -->
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Options" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Http" Version="10.0.0" />
    <!-- Roslyn / SIMD -->
    <PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="4.14.0" />
    <PackageVersion Include="System.Numerics.Tensors" Version="10.0.0" />
    <!-- Testing -->
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageVersion Include="coverlet.collector" Version="6.0.2" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Прописать ссылки в четырёх `.csproj`**

`src/CodeIndex.Core/CodeIndex.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" />
    <PackageReference Include="System.Numerics.Tensors" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
  </ItemGroup>
</Project>
```

`src/CodeIndex.Server/CodeIndex.Server.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>CodeIndex.Server</AssemblyName>
    <RootNamespace>CodeIndex.Server</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Microsoft.Extensions.Http" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\CodeIndex.Core\CodeIndex.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="CodeIndex.Server.Tests" />
  </ItemGroup>
</Project>
```

`tests/CodeIndex.Core.Tests/CodeIndex.Core.Tests.csproj` (и аналогично `CodeIndex.Server.Tests`, где `ProjectReference` указывает на `src/CodeIndex.Server`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\CodeIndex.Core\CodeIndex.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Восстановить и собрать**

Run: `dotnet build`
Expected: `Build succeeded`, 0 Warning(s), 0 Error(s)

Если restore падает с `NU1102` (версия пакета не найдена), выяснить доступную версию и подставить её в `Directory.Packages.props`:

```bash
dotnet package search Microsoft.CodeAnalysis.CSharp --take 5
dotnet package search System.Numerics.Tensors --take 5
```

- [ ] **Step 7: Коммит**

```bash
git add -A
git commit -m "chore: scaffold solution with core, server and test projects"
```

---

## Task 1: ISourceProvider

Абстракция вводится первой, потому что от неё зависят чанкер и индексатор, и она же делает их тестируемыми без временных папок.

**Files:**
- Create: `src/CodeIndex.Core/Sources/ISourceProvider.cs`
- Create: `src/CodeIndex.Core/Sources/FileSystemSourceProvider.cs`
- Test: `tests/CodeIndex.Core.Tests/Sources/InMemorySourceProvider.cs`
- Test: `tests/CodeIndex.Core.Tests/Sources/FileSystemSourceProviderTests.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/CodeIndex.Core.Tests/Sources/FileSystemSourceProviderTests.cs`:

```csharp
using CodeIndex.Core.Sources;
using Xunit;

namespace CodeIndex.Core.Tests.Sources;

public sealed class FileSystemSourceProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ci-" + Guid.NewGuid().ToString("N"));

    public FileSystemSourceProviderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "obj"));
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "line1\nline2\nline3\nline4\n");
        File.WriteAllText(Path.Combine(_root, "obj", "Generated.cs"), "skip me");
        File.WriteAllText(Path.Combine(_root, "src", "notes.txt"), "not code");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task EnumerateAsync_ReturnsRelativeCsPaths_ExcludingBuildOutput()
    {
        FileSystemSourceProvider provider = new(_root);

        List<string> found = new();
        await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
            found.Add(path);

        Assert.Equal(new[] { "src/A.cs" }, found);
    }

    [Fact]
    public async Task ReadLinesAsync_ReturnsInclusiveRange()
    {
        FileSystemSourceProvider provider = new(_root);

        string text = await provider.ReadLinesAsync("src/A.cs", 2, 3, TestContext.Current.CancellationToken);

        Assert.Equal("line2\nline3", text);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~FileSystemSourceProviderTests"`
Expected: ошибка компиляции — `FileSystemSourceProvider` не существует

- [ ] **Step 3: Написать интерфейс**

`src/CodeIndex.Core/Sources/ISourceProvider.cs`:

```csharp
namespace CodeIndex.Core.Sources;

/// <summary>
/// The only route to project sources. Nothing outside this namespace touches
/// <see cref="File"/> or <see cref="Directory"/> directly, which keeps chunking and
/// indexing testable against in-memory inputs.
/// </summary>
public interface ISourceProvider
{
    /// <summary>Paths of indexable files, relative to the project root, with '/' separators.</summary>
    IAsyncEnumerable<string> EnumerateAsync(CancellationToken cancellationToken);

    Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken);

    /// <summary>Reads an inclusive, 1-based line range.</summary>
    Task<string> ReadLinesAsync(string relativePath, int startLine, int endLine, CancellationToken cancellationToken);

    Task<SourceFileStat> StatAsync(string relativePath, CancellationToken cancellationToken);
}

public readonly record struct SourceFileStat(long Length, DateTime LastWriteTimeUtc);
```

- [ ] **Step 4: Написать реализацию**

`src/CodeIndex.Core/Sources/FileSystemSourceProvider.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace CodeIndex.Core.Sources;

public sealed class FileSystemSourceProvider : ISourceProvider
{
    private static readonly string[] ExcludedSegments = ["bin", "obj", ".git", "node_modules", "packages", "TestResults"];

    private readonly string _root;

    public FileSystemSourceProvider(string root) => _root = Path.GetFullPath(root);

    public async IAsyncEnumerable<string> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        foreach (string absolute in Directory.EnumerateFiles(_root, "*.cs", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(_root, absolute).Replace('\\', '/');
            if (IsExcluded(relative))
                continue;

            yield return relative;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(Resolve(relativePath), cancellationToken);

    public async Task<string> ReadLinesAsync(
        string relativePath, int startLine, int endLine, CancellationToken cancellationToken)
    {
        string[] lines = await File.ReadAllLinesAsync(Resolve(relativePath), cancellationToken).ConfigureAwait(false);

        int from = Math.Max(1, startLine);
        int to = Math.Min(lines.Length, endLine);
        if (from > to)
            return string.Empty;

        return string.Join('\n', lines[(from - 1)..to]);
    }

    public Task<SourceFileStat> StatAsync(string relativePath, CancellationToken cancellationToken)
    {
        FileInfo info = new(Resolve(relativePath));
        return Task.FromResult(new SourceFileStat(info.Length, info.LastWriteTimeUtc));
    }

    private string Resolve(string relativePath) => Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static bool IsExcluded(string relativePath)
    {
        ReadOnlySpan<char> span = relativePath;
        foreach (Range segment in span.Split('/'))
        {
            ReadOnlySpan<char> part = span[segment];
            foreach (string excluded in ExcludedSegments)
            {
                if (part.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
```

- [ ] **Step 5: Убедиться, что тесты проходят**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~FileSystemSourceProviderTests"`
Expected: `Passed! - Failed: 0, Passed: 2`

- [ ] **Step 6: Написать тестовую реализацию для остальных задач**

`tests/CodeIndex.Core.Tests/Sources/InMemorySourceProvider.cs`:

```csharp
using CodeIndex.Core.Sources;

namespace CodeIndex.Core.Tests.Sources;

/// <summary>Backs chunker and indexer tests so they never touch the disk.</summary>
public sealed class InMemorySourceProvider : ISourceProvider
{
    private readonly Dictionary<string, string> _files;

    public InMemorySourceProvider(Dictionary<string, string> files) => _files = files;

    public void Set(string relativePath, string content) => _files[relativePath] = content;

    public void Remove(string relativePath) => _files.Remove(relativePath);

    public async IAsyncEnumerable<string> EnumerateAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (string path in _files.Keys.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return path;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken) =>
        Task.FromResult(_files[relativePath]);

    public Task<string> ReadLinesAsync(
        string relativePath, int startLine, int endLine, CancellationToken cancellationToken)
    {
        string[] lines = _files[relativePath].Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int from = Math.Max(1, startLine);
        int to = Math.Min(lines.Length, endLine);
        return Task.FromResult(from > to ? string.Empty : string.Join('\n', lines[(from - 1)..to]));
    }

    public Task<SourceFileStat> StatAsync(string relativePath, CancellationToken cancellationToken) =>
        Task.FromResult(new SourceFileStat(_files[relativePath].Length, DateTime.UnixEpoch));
}
```

- [ ] **Step 7: Коммит**

```bash
git add -A
git commit -m "feat: add ISourceProvider with filesystem and in-memory implementations"
```

---

## Task 2: Модель чанка и базовый разбор Roslyn

**Files:**
- Create: `src/CodeIndex.Core/Chunking/CodeChunk.cs`
- Create: `src/CodeIndex.Core/Chunking/RoslynChunker.cs`
- Test: `tests/CodeIndex.Core.Tests/Chunking/RoslynChunkerTests.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/CodeIndex.Core.Tests/Chunking/RoslynChunkerTests.cs`:

```csharp
using CodeIndex.Core.Chunking;
using Xunit;

namespace CodeIndex.Core.Tests.Chunking;

public sealed class RoslynChunkerTests
{
    private const string Sample = """
        namespace Acme.Payments
        {
            /// <summary>Charges cards.</summary>
            public class PaymentGateway
            {
                public int Charge(decimal amount)
                {
                    return 1;
                }
            }
        }
        """;

    [Fact]
    public void Chunk_ProducesTypeAndMemberChunks()
    {
        RoslynChunker chunker = new();

        IReadOnlyList<CodeChunk> chunks = chunker.Chunk("src/PaymentGateway.cs", Sample);

        Assert.Equal(2, chunks.Count);
        Assert.Contains(chunks, c => c.Kind == ChunkKind.Class && c.Symbol == "Acme.Payments.PaymentGateway");
        Assert.Contains(chunks, c => c.Kind == ChunkKind.Method && c.Symbol == "Acme.Payments.PaymentGateway.Charge");
    }

    [Fact]
    public void Chunk_CapturesSignatureAndDocComment()
    {
        RoslynChunker chunker = new();

        CodeChunk type = chunker.Chunk("src/PaymentGateway.cs", Sample).Single(c => c.Kind == ChunkKind.Class);
        CodeChunk method = chunker.Chunk("src/PaymentGateway.cs", Sample).Single(c => c.Kind == ChunkKind.Method);

        Assert.Contains("Charges cards.", type.DocComment, StringComparison.Ordinal);
        Assert.Equal("int Charge(decimal amount)", method.Signature);
    }

    [Fact]
    public void Chunk_RecordsOneBasedInclusiveLineRange()
    {
        RoslynChunker chunker = new();

        CodeChunk method = chunker.Chunk("src/PaymentGateway.cs", Sample).Single(c => c.Kind == ChunkKind.Method);

        Assert.Equal(6, method.StartLine);
        Assert.Equal(9, method.EndLine);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~RoslynChunkerTests"`
Expected: ошибка компиляции — `RoslynChunker` и `CodeChunk` не существуют

- [ ] **Step 3: Написать модель**

`src/CodeIndex.Core/Chunking/CodeChunk.cs`:

```csharp
namespace CodeIndex.Core.Chunking;

public enum ChunkKind
{
    Unknown = 0,
    Class,
    Interface,
    Struct,
    Record,
    Enum,
    Method,
    Constructor,
    Property,
    Field,
    FileFragment,
}

/// <summary>
/// One indexable unit — a type declaration header or a single member. Bodies are NOT
/// stored here; they are read from the source provider on demand at query time.
/// </summary>
public sealed record CodeChunk
{
    public required string FilePath { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required ChunkKind Kind { get; init; }

    /// <summary>Fully-qualified name, e.g. <c>Acme.Payments.PaymentGateway.Charge</c>.</summary>
    public required string Symbol { get; init; }

    public required string Signature { get; init; }
    public string DocComment { get; init; } = string.Empty;

    /// <summary>Text handed to the embedding model. Structure carries as much signal as the body.</summary>
    public required string EmbedText { get; init; }
}
```

- [ ] **Step 4: Написать чанкер**

`src/CodeIndex.Core/Chunking/RoslynChunker.cs`:

```csharp
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeIndex.Core.Chunking;

public sealed class RoslynChunker
{
    private const int MaxBodyChars = 2000;

    public IReadOnlyList<CodeChunk> Chunk(string filePath, string sourceText)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceText);
        CompilationUnitSyntax root = (CompilationUnitSyntax)tree.GetRoot();

        List<CodeChunk> chunks = new();

        foreach (BaseTypeDeclarationSyntax type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            string typeSymbol = BuildQualifiedName(type);
            chunks.Add(CreateTypeChunk(filePath, type, typeSymbol, sourceText));

            if (type is not TypeDeclarationSyntax declaration)
                continue;

            foreach (MemberDeclarationSyntax member in declaration.Members)
            {
                if (member is BaseTypeDeclarationSyntax)
                    continue; // nested types are visited by the outer loop

                CodeChunk? chunk = CreateMemberChunk(filePath, member, typeSymbol, sourceText);
                if (chunk is not null)
                    chunks.Add(chunk);
            }
        }

        return chunks;
    }

    private static CodeChunk CreateTypeChunk(
        string filePath, BaseTypeDeclarationSyntax type, string symbol, string sourceText)
    {
        FileLinePositionSpan span = type.SyntaxTree.GetLineSpan(type.Span);
        string signature = $"{type.Keyword.ValueText} {type.Identifier.ValueText}";
        string doc = ExtractDocComment(type);

        return new CodeChunk
        {
            FilePath = filePath,
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
            Kind = MapTypeKind(type),
            Symbol = symbol,
            Signature = signature,
            DocComment = doc,
            EmbedText = BuildEmbedText(filePath, symbol, MapTypeKind(type), signature, doc, DescribeMembers(type)),
        };
    }

    private static CodeChunk? CreateMemberChunk(
        string filePath, MemberDeclarationSyntax member, string typeSymbol, string sourceText)
    {
        (ChunkKind kind, string name, string signature) = member switch
        {
            MethodDeclarationSyntax m => (
                ChunkKind.Method,
                m.Identifier.ValueText,
                $"{m.ReturnType} {m.Identifier.ValueText}{m.TypeParameterList}{m.ParameterList}"),
            ConstructorDeclarationSyntax c => (
                ChunkKind.Constructor,
                c.Identifier.ValueText,
                $"{c.Identifier.ValueText}{c.ParameterList}"),
            PropertyDeclarationSyntax p => (
                ChunkKind.Property,
                p.Identifier.ValueText,
                $"{p.Type} {p.Identifier.ValueText}"),
            _ => (ChunkKind.Unknown, string.Empty, string.Empty),
        };

        if (kind == ChunkKind.Unknown)
            return null;

        FileLinePositionSpan span = member.SyntaxTree.GetLineSpan(member.Span);
        string doc = ExtractDocComment(member);
        string body = Truncate(member.ToString(), MaxBodyChars);
        string symbol = $"{typeSymbol}.{name}";

        return new CodeChunk
        {
            FilePath = filePath,
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
            Kind = kind,
            Symbol = symbol,
            Signature = signature.Trim(),
            DocComment = doc,
            EmbedText = BuildEmbedText(filePath, symbol, kind, signature.Trim(), doc, body),
        };
    }

    private static string BuildEmbedText(
        string filePath, string symbol, ChunkKind kind, string signature, string doc, string body)
    {
        StringBuilder builder = new();
        builder.Append("File: ").AppendLine(filePath);
        builder.Append("Symbol: ").AppendLine(symbol);
        builder.Append("Kind: ").AppendLine(kind.ToString());
        builder.Append("Signature: ").AppendLine(signature);
        if (!string.IsNullOrWhiteSpace(doc))
            builder.Append("Doc: ").AppendLine(doc);
        builder.AppendLine("Code:");
        builder.Append(body);
        return builder.ToString();
    }

    private static string DescribeMembers(BaseTypeDeclarationSyntax type)
    {
        if (type is not TypeDeclarationSyntax declaration)
            return string.Empty;

        IEnumerable<string> names = declaration.Members
            .Select(m => m switch
            {
                MethodDeclarationSyntax x => x.Identifier.ValueText,
                PropertyDeclarationSyntax x => x.Identifier.ValueText,
                ConstructorDeclarationSyntax x => x.Identifier.ValueText,
                _ => string.Empty,
            })
            .Where(n => n.Length > 0);

        return string.Join(", ", names);
    }

    private static string BuildQualifiedName(BaseTypeDeclarationSyntax type)
    {
        List<string> parts = [type.Identifier.ValueText];

        for (SyntaxNode? node = type.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax outer:
                    parts.Insert(0, outer.Identifier.ValueText);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Insert(0, ns.Name.ToString());
                    break;
            }
        }

        return string.Join('.', parts);
    }

    private static ChunkKind MapTypeKind(BaseTypeDeclarationSyntax type) => type switch
    {
        RecordDeclarationSyntax => ChunkKind.Record,
        InterfaceDeclarationSyntax => ChunkKind.Interface,
        StructDeclarationSyntax => ChunkKind.Struct,
        EnumDeclarationSyntax => ChunkKind.Enum,
        _ => ChunkKind.Class,
    };

    private static string ExtractDocComment(SyntaxNode node)
    {
        SyntaxTriviaList trivia = node.GetLeadingTrivia();
        StringBuilder builder = new();

        foreach (SyntaxTrivia item in trivia)
        {
            if (item.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                item.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                builder.Append(item.ToFullString());
            }
        }

        return builder.ToString()
            .Replace("///", string.Empty, StringComparison.Ordinal)
            .Replace("<summary>", string.Empty, StringComparison.Ordinal)
            .Replace("</summary>", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
```

- [ ] **Step 5: Убедиться, что тесты проходят**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~RoslynChunkerTests"`
Expected: `Passed! - Failed: 0, Passed: 3`

- [ ] **Step 6: Коммит**

```bash
git add -A
git commit -m "feat: add Roslyn member-level chunker"
```

---

## Task 3: Крайние случаи разбора

**Files:**
- Modify: `src/CodeIndex.Core/Chunking/RoslynChunker.cs`
- Test: `tests/CodeIndex.Core.Tests/Chunking/RoslynChunkerEdgeCaseTests.cs`

- [ ] **Step 1: Написать падающие тесты**

`tests/CodeIndex.Core.Tests/Chunking/RoslynChunkerEdgeCaseTests.cs`:

```csharp
using CodeIndex.Core.Chunking;
using Xunit;

namespace CodeIndex.Core.Tests.Chunking;

public sealed class RoslynChunkerEdgeCaseTests
{
    private readonly RoslynChunker _chunker = new();

    [Fact]
    public void Chunk_HandlesFileScopedNamespace()
    {
        const string source = """
            namespace Acme.Core;

            public class Widget
            {
                public void Spin() { }
            }
            """;

        IReadOnlyList<CodeChunk> chunks = _chunker.Chunk("Widget.cs", source);

        Assert.Contains(chunks, c => c.Symbol == "Acme.Core.Widget.Spin");
    }

    [Fact]
    public void Chunk_HandlesNestedTypes()
    {
        const string source = """
            namespace Acme;

            public class Outer
            {
                public class Inner
                {
                    public void Go() { }
                }
            }
            """;

        IReadOnlyList<CodeChunk> chunks = _chunker.Chunk("Outer.cs", source);

        Assert.Contains(chunks, c => c.Symbol == "Acme.Outer.Inner");
        Assert.Contains(chunks, c => c.Symbol == "Acme.Outer.Inner.Go");
    }

    [Fact]
    public void Chunk_HandlesRecordsAndPositionalParameters()
    {
        const string source = """
            namespace Acme;

            public record Money(decimal Amount, string Currency)
            {
                public Money Doubled() => this with { Amount = Amount * 2 };
            }
            """;

        IReadOnlyList<CodeChunk> chunks = _chunker.Chunk("Money.cs", source);

        Assert.Contains(chunks, c => c.Kind == ChunkKind.Record && c.Symbol == "Acme.Money");
        Assert.Contains(chunks, c => c.Symbol == "Acme.Money.Doubled");
    }

    [Fact]
    public void Chunk_HandlesGenericMethodSignature()
    {
        const string source = """
            namespace Acme;

            public class Box
            {
                public T? Unwrap<T>(object value) where T : class => value as T;
            }
            """;

        CodeChunk method = _chunker.Chunk("Box.cs", source).Single(c => c.Kind == ChunkKind.Method);

        Assert.Equal("T? Unwrap<T>(object value)", method.Signature);
    }

    [Fact]
    public void Chunk_TreatsPartialHalvesIndependently()
    {
        const string first = """
            namespace Acme;

            public partial class Service
            {
                public void One() { }
            }
            """;
        const string second = """
            namespace Acme;

            public partial class Service
            {
                public void Two() { }
            }
            """;

        IReadOnlyList<CodeChunk> a = _chunker.Chunk("Service.A.cs", first);
        IReadOnlyList<CodeChunk> b = _chunker.Chunk("Service.B.cs", second);

        Assert.Contains(a, c => c.Symbol == "Acme.Service.One" && c.FilePath == "Service.A.cs");
        Assert.Contains(b, c => c.Symbol == "Acme.Service.Two" && c.FilePath == "Service.B.cs");
    }

    [Fact]
    public void Chunk_ReturnsEmptyForTopLevelStatementsWithoutTypes()
    {
        const string source = """
            Console.WriteLine("hello");
            """;

        IReadOnlyList<CodeChunk> chunks = _chunker.Chunk("Program.cs", source);

        Assert.Empty(chunks);
    }
}
```

- [ ] **Step 2: Прогнать тесты и зафиксировать, какие падают**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~RoslynChunkerEdgeCaseTests"`
Expected: падает `Chunk_HandlesGenericMethodSignature`. Причина: текущая сборка сигнатуры подставляет `m.ParameterList` целиком, а `ToString()` списка параметров у Roslyn сохраняет исходное форматирование и не включает ограничение `where`, из-за чего результат не совпадает с ожидаемым `T? Unwrap<T>(object value)`. Остальные пять тестов задачи должны пройти сразу — они проверяют уже реализованное поведение.

- [ ] **Step 3: Поправить сборку сигнатуры метода**

В `CreateMemberChunk` заменить ветку `MethodDeclarationSyntax`:

```csharp
            MethodDeclarationSyntax m => (
                ChunkKind.Method,
                m.Identifier.ValueText,
                BuildMethodSignature(m)),
```

И добавить метод в класс:

```csharp
    private static string BuildMethodSignature(MethodDeclarationSyntax method)
    {
        string typeParameters = method.TypeParameterList?.ToString() ?? string.Empty;
        string parameters = string.Join(", ", method.ParameterList.Parameters
            .Select(p => $"{p.Type} {p.Identifier.ValueText}"));

        // Constraint clauses are intentionally dropped: they add noise to the signature
        // without helping either the symbol matcher or a human scanning results.
        return $"{method.ReturnType} {method.Identifier.ValueText}{typeParameters}({parameters})";
    }
```

- [ ] **Step 4: Убедиться, что все тесты проходят**

Run: `dotnet test tests/CodeIndex.Core.Tests`
Expected: `Passed! - Failed: 0`

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "fix: handle generics, records, nested types and partials in chunker"
```

---

## Task 4: FallbackChunker

**Files:**
- Create: `src/CodeIndex.Core/Chunking/FallbackChunker.cs`
- Modify: `src/CodeIndex.Core/Chunking/RoslynChunker.cs`
- Test: `tests/CodeIndex.Core.Tests/Chunking/FallbackChunkerTests.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/CodeIndex.Core.Tests/Chunking/FallbackChunkerTests.cs`:

```csharp
using CodeIndex.Core.Chunking;
using Xunit;

namespace CodeIndex.Core.Tests.Chunking;

public sealed class FallbackChunkerTests
{
    [Fact]
    public void Chunk_SplitsByLineWindowsWithOverlap()
    {
        string source = string.Join('\n', Enumerable.Range(1, 250).Select(i => $"line {i}"));
        FallbackChunker chunker = new(windowLines: 100, overlapLines: 20);

        IReadOnlyList<CodeChunk> chunks = chunker.Chunk("weird.cs", source);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(100, chunks[0].EndLine);
        Assert.Equal(81, chunks[1].StartLine);
        Assert.All(chunks, c => Assert.Equal(ChunkKind.FileFragment, c.Kind));
    }

    [Fact]
    public void Chunk_UsesFilePathAndLineRangeAsSymbol()
    {
        FallbackChunker chunker = new(windowLines: 100, overlapLines: 20);

        CodeChunk chunk = chunker.Chunk("a/b.cs", "one\ntwo").Single();

        Assert.Equal("a/b.cs:1-2", chunk.Symbol);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~FallbackChunkerTests"`
Expected: ошибка компиляции — `FallbackChunker` не существует

- [ ] **Step 3: Написать реализацию**

`src/CodeIndex.Core/Chunking/FallbackChunker.cs`:

```csharp
using System.Text;

namespace CodeIndex.Core.Chunking;

/// <summary>
/// Used when Roslyn produced nothing for a file. A file must never silently vanish from
/// the index, so it is split into overlapping line windows instead.
/// </summary>
public sealed class FallbackChunker
{
    private readonly int _windowLines;
    private readonly int _overlapLines;

    public FallbackChunker(int windowLines = 100, int overlapLines = 20)
    {
        if (overlapLines >= windowLines)
            throw new ArgumentOutOfRangeException(nameof(overlapLines), "Overlap must be smaller than the window.");

        _windowLines = windowLines;
        _overlapLines = overlapLines;
    }

    public IReadOnlyList<CodeChunk> Chunk(string filePath, string sourceText)
    {
        string[] lines = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<CodeChunk> chunks = new();
        int step = _windowLines - _overlapLines;

        for (int start = 0; start < lines.Length; start += step)
        {
            int end = Math.Min(start + _windowLines, lines.Length);
            string body = string.Join('\n', lines[start..end]);
            string symbol = $"{filePath}:{start + 1}-{end}";

            StringBuilder embed = new();
            embed.Append("File: ").AppendLine(filePath);
            embed.Append("Symbol: ").AppendLine(symbol);
            embed.AppendLine("Kind: FileFragment");
            embed.AppendLine("Code:");
            embed.Append(body);

            chunks.Add(new CodeChunk
            {
                FilePath = filePath,
                StartLine = start + 1,
                EndLine = end,
                Kind = ChunkKind.FileFragment,
                Symbol = symbol,
                Signature = symbol,
                EmbedText = embed.ToString(),
            });

            if (end == lines.Length)
                break;
        }

        return chunks;
    }
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~FallbackChunkerTests"`
Expected: `Passed! - Failed: 0, Passed: 2`

- [ ] **Step 5: Написать тест на выбор чанкера**

Добавить в `tests/CodeIndex.Core.Tests/Chunking/FallbackChunkerTests.cs`:

```csharp
    [Fact]
    public void ChunkFile_FallsBackWhenRoslynFindsNoTypes()
    {
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());

        IReadOnlyList<CodeChunk> chunks = pipeline.ChunkFile("Program.cs", "Console.WriteLine(\"hi\");");

        Assert.Single(chunks);
        Assert.Equal(ChunkKind.FileFragment, chunks[0].Kind);
    }

    [Fact]
    public void ChunkFile_PrefersRoslynWhenTypesExist()
    {
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());

        IReadOnlyList<CodeChunk> chunks = pipeline.ChunkFile(
            "W.cs",
            "namespace A;\npublic class W { public void M() { } }");

        Assert.DoesNotContain(chunks, c => c.Kind == ChunkKind.FileFragment);
    }
```

- [ ] **Step 6: Написать `ChunkerPipeline`**

`src/CodeIndex.Core/Chunking/ChunkerPipeline.cs`:

```csharp
namespace CodeIndex.Core.Chunking;

public sealed class ChunkerPipeline
{
    private readonly RoslynChunker _roslyn;
    private readonly FallbackChunker _fallback;

    public ChunkerPipeline(RoslynChunker roslyn, FallbackChunker fallback)
    {
        _roslyn = roslyn;
        _fallback = fallback;
    }

    public IReadOnlyList<CodeChunk> ChunkFile(string filePath, string sourceText)
    {
        IReadOnlyList<CodeChunk> chunks;
        try
        {
            chunks = _roslyn.Chunk(filePath, sourceText);
        }
        catch (Exception)
        {
            // A parser crash must not drop the file from the index.
            chunks = [];
        }

        return chunks.Count > 0 ? chunks : _fallback.Chunk(filePath, sourceText);
    }
}
```

- [ ] **Step 7: Убедиться, что тесты проходят, и закоммитить**

Run: `dotnet test tests/CodeIndex.Core.Tests`
Expected: `Passed! - Failed: 0`

```bash
git add -A
git commit -m "feat: add fallback line chunker and chunker pipeline"
```

---

## Task 5: Отпечатки файлов

**Files:**
- Create: `src/CodeIndex.Core/Indexing/FileFingerprint.cs`
- Test: `tests/CodeIndex.Core.Tests/Indexing/FileFingerprintTests.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/CodeIndex.Core.Tests/Indexing/FileFingerprintTests.cs`:

```csharp
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Sources;
using Xunit;

namespace CodeIndex.Core.Tests.Indexing;

public sealed class FileFingerprintTests
{
    [Fact]
    public void NeedsContentCheck_IsFalseWhenSizeAndTimeMatch()
    {
        FileFingerprint stored = new("a.cs", 120, DateTime.UnixEpoch, "hash1");
        SourceFileStat current = new(120, DateTime.UnixEpoch);

        Assert.False(stored.NeedsContentCheck(current));
    }

    [Fact]
    public void NeedsContentCheck_IsTrueWhenTimeDiffers()
    {
        FileFingerprint stored = new("a.cs", 120, DateTime.UnixEpoch, "hash1");
        SourceFileStat current = new(120, DateTime.UnixEpoch.AddHours(1));

        Assert.True(stored.NeedsContentCheck(current));
    }

    [Fact]
    public void Matches_IsTrueWhenContentHashIsUnchanged()
    {
        // This is the git-checkout case: every timestamp moved, no content did.
        FileFingerprint stored = new("a.cs", 120, DateTime.UnixEpoch, FileFingerprint.ComputeHash("public class A { }"));

        Assert.True(stored.MatchesContent("public class A { }"));
        Assert.False(stored.MatchesContent("public class B { }"));
    }

    [Fact]
    public void ComputeHash_IsStableAcrossLineEndings()
    {
        // Windows and WSL checkouts of the same repo must not look different.
        Assert.Equal(
            FileFingerprint.ComputeHash("a\r\nb"),
            FileFingerprint.ComputeHash("a\nb"));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~FileFingerprintTests"`
Expected: ошибка компиляции — `FileFingerprint` не существует

- [ ] **Step 3: Написать реализацию**

`src/CodeIndex.Core/Indexing/FileFingerprint.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using CodeIndex.Core.Sources;

namespace CodeIndex.Core.Indexing;

/// <summary>
/// Cheap staleness check with an exact fallback. Size and timestamp settle the common case;
/// a content hash settles the rest, because git rewrites timestamps without touching content.
/// </summary>
public sealed record FileFingerprint(string RelativePath, long Length, DateTime LastWriteTimeUtc, string ContentHash)
{
    public bool NeedsContentCheck(SourceFileStat current) =>
        current.Length != Length || current.LastWriteTimeUtc != LastWriteTimeUtc;

    public bool MatchesContent(string sourceText) =>
        string.Equals(ContentHash, ComputeHash(sourceText), StringComparison.Ordinal);

    public static string ComputeHash(string sourceText)
    {
        // Normalise line endings so the same commit hashes identically on Windows and Linux.
        string normalised = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexStringLower(hash);
    }
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~FileFingerprintTests"`
Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "feat: add file fingerprint with content-hash fallback"
```

---

## Task 6: Клиент эмбеддингов Ollama

**Files:**
- Create: `src/CodeIndex.Core/Embedding/IEmbeddingClient.cs`
- Create: `src/CodeIndex.Core/Embedding/OllamaEmbeddingClient.cs`
- Create: `src/CodeIndex.Core/Embedding/EmbeddingOptions.cs`
- Test: `tests/CodeIndex.Core.Tests/Embedding/FakeHttpMessageHandler.cs`
- Test: `tests/CodeIndex.Core.Tests/Embedding/OllamaEmbeddingClientTests.cs`

- [ ] **Step 1: Написать фейковый HTTP-обработчик**

`tests/CodeIndex.Core.Tests/Embedding/FakeHttpMessageHandler.cs`:

```csharp
using System.Net;

namespace CodeIndex.Core.Tests.Embedding;

/// <summary>
/// Hand-written stand-in for a mocking library: the canned response is visible right
/// next to the assertion instead of hidden behind a setup DSL.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<string> CapturedBodies { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    public static FakeHttpMessageHandler Returning(string json) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

    public static FakeHttpMessageHandler Failing(HttpStatusCode code) =>
        new(_ => new HttpResponseMessage(code) { Content = new StringContent("{}") });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
            CapturedBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        return _responder(request);
    }
}
```

- [ ] **Step 2: Написать падающий тест**

`tests/CodeIndex.Core.Tests/Embedding/OllamaEmbeddingClientTests.cs`:

```csharp
using CodeIndex.Core.Embedding;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeIndex.Core.Tests.Embedding;

public sealed class OllamaEmbeddingClientTests
{
    private static OllamaEmbeddingClient CreateClient(FakeHttpMessageHandler handler, int dimensions)
    {
        HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost:11434") };
        EmbeddingOptions options = new() { Model = "qwen3-embedding:4b", Dimensions = dimensions };
        return new OllamaEmbeddingClient(http, Options.Create(options));
    }

    [Fact]
    public async Task EmbedAsync_TruncatesVectorToConfiguredDimensions()
    {
        // Qwen3-Embedding is MRL-trained, so truncating the tail is a supported operation.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1,2,3,4,5,6]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 4);

        IReadOnlyList<float[]> result = await client.EmbedAsync(["text"], TestContext.Current.CancellationToken);

        Assert.Equal(new float[] { 1, 2, 3, 4 }, result[0]);
    }

    [Fact]
    public async Task EmbedAsync_NormalisesVectorsToUnitLength()
    {
        // Pre-normalising lets search use a plain dot product later.
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[3,4]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        IReadOnlyList<float[]> result = await client.EmbedAsync(["text"], TestContext.Current.CancellationToken);

        Assert.Equal(0.6f, result[0][0], tolerance: 0.0001f);
        Assert.Equal(0.8f, result[0][1], tolerance: 0.0001f);
    }

    [Fact]
    public async Task EmbedAsync_SendsModelAndInputInOneRequest()
    {
        FakeHttpMessageHandler handler = FakeHttpMessageHandler.Returning("""{"embeddings":[[1,0],[0,1]]}""");
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        await client.EmbedAsync(["a", "b"], TestContext.Current.CancellationToken);

        Assert.Single(handler.CapturedBodies);
        Assert.Contains("qwen3-embedding:4b", handler.CapturedBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsEmbeddingUnavailableWhenOllamaIsDown()
    {
        FakeHttpMessageHandler handler = new(_ => throw new HttpRequestException("connection refused"));
        OllamaEmbeddingClient client = CreateClient(handler, dimensions: 2);

        EmbeddingUnavailableException error = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));

        Assert.Contains("ollama serve", error.Message, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 3: Убедиться, что тест падает**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~OllamaEmbeddingClientTests"`
Expected: ошибка компиляции — `OllamaEmbeddingClient` не существует

- [ ] **Step 4: Написать опции и контракт**

`src/CodeIndex.Core/Embedding/EmbeddingOptions.cs`:

```csharp
namespace CodeIndex.Core.Embedding;

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3-embedding:4b";

    /// <summary>Truncation target. Qwen3-Embedding is MRL-trained, so 1024 keeps almost all quality.</summary>
    public int Dimensions { get; set; } = 1024;
}
```

`src/CodeIndex.Core/Embedding/IEmbeddingClient.cs`:

```csharp
namespace CodeIndex.Core.Embedding;

public interface IEmbeddingClient
{
    int Dimensions { get; }

    string Model { get; }

    /// <summary>Returns one unit-length vector per input, in the same order.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken);
}

public sealed class EmbeddingUnavailableException : Exception
{
    public EmbeddingUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
```

- [ ] **Step 5: Написать клиент**

`src/CodeIndex.Core/Embedding/OllamaEmbeddingClient.cs`:

```csharp
using System.Net.Http.Json;
using System.Numerics.Tensors;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CodeIndex.Core.Embedding;

public sealed class OllamaEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly EmbeddingOptions _options;

    public OllamaEmbeddingClient(HttpClient http, IOptions<EmbeddingOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public int Dimensions => _options.Dimensions;

    public string Model => _options.Model;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        if (inputs.Count == 0)
            return [];

        OllamaEmbedRequest request = new(_options.Model, inputs);

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("/api/embed", request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new EmbeddingUnavailableException(
                $"Ollama is unreachable at {_options.Endpoint}. Start it with 'ollama serve'.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new EmbeddingUnavailableException(
                $"Ollama returned {(int)response.StatusCode}. If the model is missing, run 'ollama pull {_options.Model}'. Body: {body}");
        }

        OllamaEmbedResponse? payload = await response.Content
            .ReadFromJsonAsync<OllamaEmbedResponse>(cancellationToken).ConfigureAwait(false);

        if (payload?.Embeddings is null || payload.Embeddings.Count != inputs.Count)
            throw new EmbeddingUnavailableException(
                $"Ollama returned {payload?.Embeddings?.Count ?? 0} vectors for {inputs.Count} inputs.");

        List<float[]> result = new(payload.Embeddings.Count);
        foreach (float[] raw in payload.Embeddings)
        {
            float[] vector = raw.Length > _options.Dimensions ? raw[.._options.Dimensions] : raw;
            Normalise(vector);
            result.Add(vector);
        }

        return result;
    }

    /// <summary>
    /// Unit-normalises in place so that cosine similarity later reduces to a dot product.
    /// Block SIMD operation — no per-element loop.
    /// </summary>
    private static void Normalise(float[] vector)
    {
        float norm = TensorPrimitives.Norm<float>(vector);
        if (norm > 0f)
            TensorPrimitives.Divide<float>(vector, norm, vector);
    }

    private sealed record OllamaEmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    private sealed record OllamaEmbedResponse(
        [property: JsonPropertyName("embeddings")] IReadOnlyList<float[]>? Embeddings);
}
```

- [ ] **Step 6: Убедиться, что тесты проходят**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~OllamaEmbeddingClientTests"`
Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 7: Коммит**

```bash
git add -A
git commit -m "feat: add Ollama embedding client with MRL truncation and normalisation"
```

---

## Task 7: Хранилище индекса

Формат: `manifest.json` — заголовок, чанки и отпечатки файлов; `vectors.bin` — чистый массив `float32` без заголовка и разделителей. Отсутствие заголовка в бинарнике позволяет читать его одним `MemoryMarshal.Cast` без арифметики смещений, а корректность длины проверяется по манифесту.

**Files:**
- Create: `src/CodeIndex.Core/Storage/IndexHeader.cs`
- Create: `src/CodeIndex.Core/Storage/IndexSnapshot.cs`
- Create: `src/CodeIndex.Core/Storage/IndexStore.cs`
- Test: `tests/CodeIndex.Core.Tests/Storage/IndexStoreTests.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/CodeIndex.Core.Tests/Storage/IndexStoreTests.cs`:

```csharp
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Storage;
using Xunit;

namespace CodeIndex.Core.Tests.Storage;

public sealed class IndexStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ci-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static IndexSnapshot BuildSnapshot(int dimensions = 4)
    {
        CodeChunk chunk = new()
        {
            FilePath = "src/A.cs",
            StartLine = 1,
            EndLine = 10,
            Kind = ChunkKind.Method,
            Symbol = "A.B.C",
            Signature = "void C()",
            EmbedText = "irrelevant",
        };

        return new IndexSnapshot
        {
            Header = new IndexHeader
            {
                SchemaVersion = IndexHeader.CurrentSchemaVersion,
                Model = "qwen3-embedding:4b",
                Dimensions = dimensions,
                ChunkCount = 2,
                BuiltAtUtc = DateTime.UnixEpoch,
            },
            Chunks = [chunk, chunk with { Symbol = "A.B.D" }],
            Fingerprints = [new FileFingerprint("src/A.cs", 42, DateTime.UnixEpoch, "hash")],
            Vectors = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f],
        };
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsVectorsByteForByte()
    {
        IndexStore store = new(_dir);
        IndexSnapshot original = BuildSnapshot();

        await store.SaveAsync(original, TestContext.Current.CancellationToken);
        IndexSnapshot loaded = await store.LoadAsync(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("expected a snapshot");

        Assert.Equal(original.Vectors, loaded.Vectors);
        Assert.Equal(2, loaded.Chunks.Count);
        Assert.Equal("A.B.D", loaded.Chunks[1].Symbol);
        Assert.Equal("src/A.cs", loaded.Fingerprints[0].RelativePath);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNullWhenNothingWasSaved()
    {
        IndexStore store = new(_dir);

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenVectorFileLengthDisagreesWithHeader()
    {
        IndexStore store = new(_dir);
        await store.SaveAsync(BuildSnapshot(), TestContext.Current.CancellationToken);

        string vectorPath = Path.Combine(_dir, "vectors.bin");
        byte[] truncated = (await File.ReadAllBytesAsync(vectorPath, TestContext.Current.CancellationToken))[..16];
        await File.WriteAllBytesAsync(vectorPath, truncated, TestContext.Current.CancellationToken);

        IndexCorruptedException error = await Assert.ThrowsAsync<IndexCorruptedException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains("vectors.bin", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsCompatibleWith_RejectsDifferentModelOrDimensions()
    {
        IndexHeader header = BuildSnapshot().Header;

        Assert.True(header.IsCompatibleWith("qwen3-embedding:4b", 4));
        Assert.False(header.IsCompatibleWith("nomic-embed-text", 4));
        Assert.False(header.IsCompatibleWith("qwen3-embedding:4b", 1024));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~IndexStoreTests"`
Expected: ошибка компиляции — `IndexStore`, `IndexSnapshot`, `IndexHeader` не существуют

- [ ] **Step 3: Написать заголовок и снимок**

`src/CodeIndex.Core/Storage/IndexHeader.cs`:

```csharp
namespace CodeIndex.Core.Storage;

public sealed record IndexHeader
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required string Model { get; init; }
    public required int Dimensions { get; init; }
    public required int ChunkCount { get; init; }
    public required DateTime BuiltAtUtc { get; init; }

    /// <summary>
    /// Searching vectors produced by a different model is silently wrong, so any mismatch
    /// forces a full rebuild rather than a degraded result.
    /// </summary>
    public bool IsCompatibleWith(string model, int dimensions) =>
        SchemaVersion == CurrentSchemaVersion &&
        Dimensions == dimensions &&
        string.Equals(Model, model, StringComparison.Ordinal);
}

public sealed class IndexCorruptedException : Exception
{
    public IndexCorruptedException(string message) : base(message) { }
}
```

`src/CodeIndex.Core/Storage/IndexSnapshot.cs`:

```csharp
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Indexing;

namespace CodeIndex.Core.Storage;

/// <summary>
/// The whole index in memory. <see cref="Vectors"/> is a flat row-major array of
/// ChunkCount x Dimensions floats — chunk i occupies [i*D, (i+1)*D).
/// </summary>
public sealed record IndexSnapshot
{
    public required IndexHeader Header { get; init; }
    public required IReadOnlyList<CodeChunk> Chunks { get; init; }
    public required IReadOnlyList<FileFingerprint> Fingerprints { get; init; }
    public required float[] Vectors { get; init; }

    public ReadOnlySpan<float> VectorAt(int index) =>
        Vectors.AsSpan(index * Header.Dimensions, Header.Dimensions);
}
```

- [ ] **Step 4: Написать хранилище**

`src/CodeIndex.Core/Storage/IndexStore.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Indexing;

namespace CodeIndex.Core.Storage;

public sealed class IndexStore
{
    private const string ManifestFileName = "manifest.json";
    private const string VectorsFileName = "vectors.bin";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly string _directory;

    public IndexStore(string directory) => _directory = directory;

    public string ManifestPath => Path.Combine(_directory, ManifestFileName);

    public string VectorsPath => Path.Combine(_directory, VectorsFileName);

    public async Task SaveAsync(IndexSnapshot snapshot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);

        Manifest manifest = new(snapshot.Header, snapshot.Chunks, snapshot.Fingerprints);
        await using (FileStream json = File.Create(ManifestPath))
        {
            await JsonSerializer.SerializeAsync(json, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        // Whole-buffer reinterpretation: no per-element loop, no intermediate copy.
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes<float>(snapshot.Vectors);
        await File.WriteAllBytesAsync(VectorsPath, bytes.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IndexSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ManifestPath) || !File.Exists(VectorsPath))
            return null;

        Manifest? manifest;
        await using (FileStream json = File.OpenRead(ManifestPath))
        {
            manifest = await JsonSerializer
                .DeserializeAsync<Manifest>(json, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        if (manifest is null)
            throw new IndexCorruptedException($"{ManifestFileName} could not be deserialised.");

        byte[] raw = await File.ReadAllBytesAsync(VectorsPath, cancellationToken).ConfigureAwait(false);
        long expectedBytes = (long)manifest.Header.ChunkCount * manifest.Header.Dimensions * sizeof(float);

        if (raw.LongLength != expectedBytes)
            throw new IndexCorruptedException(
                $"vectors.bin holds {raw.LongLength} bytes but the manifest expects {expectedBytes}.");

        // Zero-copy view over the buffer, then a single block copy into the owned array.
        float[] vectors = MemoryMarshal.Cast<byte, float>(raw).ToArray();

        return new IndexSnapshot
        {
            Header = manifest.Header,
            Chunks = manifest.Chunks,
            Fingerprints = manifest.Fingerprints,
            Vectors = vectors,
        };
    }

    public void Delete()
    {
        if (File.Exists(ManifestPath))
            File.Delete(ManifestPath);
        if (File.Exists(VectorsPath))
            File.Delete(VectorsPath);
    }

    private sealed record Manifest(
        IndexHeader Header,
        IReadOnlyList<CodeChunk> Chunks,
        IReadOnlyList<FileFingerprint> Fingerprints);
}
```

- [ ] **Step 5: Убедиться, что тесты проходят**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~IndexStoreTests"`
Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 6: Коммит**

```bash
git add -A
git commit -m "feat: add index store with manifest json and flat vector file"
```

---

## Task 8: Векторный поиск

**Files:**
- Create: `src/CodeIndex.Core/Search/SearchHit.cs`
- Create: `src/CodeIndex.Core/Search/VectorSearcher.cs`
- Test: `tests/CodeIndex.Core.Tests/Search/VectorSearcherTests.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/CodeIndex.Core.Tests/Search/VectorSearcherTests.cs`:

```csharp
using CodeIndex.Core.Search;
using Xunit;

namespace CodeIndex.Core.Tests.Search;

public sealed class VectorSearcherTests
{
    [Fact]
    public void Search_RanksByCosineSimilarityDescending()
    {
        // Three unit vectors; the query points exactly at index 1.
        float[] vectors =
        [
            1f, 0f,
            0f, 1f,
            0.7071f, 0.7071f,
        ];
        VectorSearcher searcher = new(vectors, dimensions: 2);

        IReadOnlyList<ScoredIndex> hits = searcher.Search([0f, 1f], topK: 3);

        Assert.Equal(1, hits[0].Index);
        Assert.Equal(2, hits[1].Index);
        Assert.Equal(0, hits[2].Index);
    }

    [Fact]
    public void Search_ClampsTopKToAvailableChunks()
    {
        VectorSearcher searcher = new([1f, 0f], dimensions: 2);

        Assert.Single(searcher.Search([1f, 0f], topK: 50));
    }

    [Fact]
    public void Search_ReturnsEmptyForAnEmptyIndex()
    {
        VectorSearcher searcher = new([], dimensions: 2);

        Assert.Empty(searcher.Search([1f, 0f], topK: 5));
    }

    [Fact]
    public void Search_ThrowsWhenQueryDimensionsDiffer()
    {
        VectorSearcher searcher = new([1f, 0f], dimensions: 2);

        Assert.Throws<ArgumentException>(() => searcher.Search([1f, 0f, 0f], topK: 1));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~VectorSearcherTests"`
Expected: ошибка компиляции — `VectorSearcher` не существует

- [ ] **Step 3: Написать модель результата**

`src/CodeIndex.Core/Search/SearchHit.cs`:

```csharp
using CodeIndex.Core.Chunking;

namespace CodeIndex.Core.Search;

public readonly record struct ScoredIndex(int Index, float Score);

public sealed record SearchHit
{
    public required int ChunkId { get; init; }
    public required CodeChunk Chunk { get; init; }
    public required double Score { get; init; }

    /// <summary>Body excerpt, read from the source provider at query time. Never cached.</summary>
    public string Excerpt { get; init; } = string.Empty;
}
```

- [ ] **Step 4: Написать поисковик**

`src/CodeIndex.Core/Search/VectorSearcher.cs`:

```csharp
using System.Numerics.Tensors;

namespace CodeIndex.Core.Search;

/// <summary>
/// Brute-force scan over the whole vector array. At the target scale (under ten thousand
/// chunks) a SIMD dot product beats any ANN structure while adding no approximation and
/// no build step.
/// </summary>
public sealed class VectorSearcher
{
    private readonly float[] _vectors;
    private readonly int _dimensions;
    private readonly int _count;

    public VectorSearcher(float[] vectors, int dimensions)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (vectors.Length % dimensions != 0)
            throw new ArgumentException("Vector buffer length must be a multiple of the dimension count.", nameof(vectors));

        _vectors = vectors;
        _dimensions = dimensions;
        _count = vectors.Length / dimensions;
    }

    public int Count => _count;

    public IReadOnlyList<ScoredIndex> Search(ReadOnlySpan<float> query, int topK)
    {
        if (query.Length != _dimensions)
            throw new ArgumentException($"Query has {query.Length} dimensions, index has {_dimensions}.", nameof(query));

        if (_count == 0 || topK <= 0)
            return [];

        float[] scores = new float[_count];
        ReadOnlySpan<float> all = _vectors;

        for (int i = 0; i < _count; i++)
        {
            // Vectors are unit-normalised on ingest, so the dot product IS the cosine.
            // TensorPrimitives runs this as a SIMD block operation, not element by element.
            scores[i] = TensorPrimitives.Dot(all.Slice(i * _dimensions, _dimensions), query);
        }

        int take = Math.Min(topK, _count);
        int[] order = new int[_count];
        for (int i = 0; i < _count; i++)
            order[i] = i;

        Array.Sort(order, (a, b) => scores[b].CompareTo(scores[a]));

        ScoredIndex[] result = new ScoredIndex[take];
        for (int i = 0; i < take; i++)
            result[i] = new ScoredIndex(order[i], scores[order[i]]);

        return result;
    }
}
```

- [ ] **Step 5: Убедиться, что тесты проходят**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~VectorSearcherTests"`
Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 6: Коммит**

```bash
git add -A
git commit -m "feat: add SIMD brute-force vector searcher"
```

---

## Task 9: Символьный поиск и слияние RRF

**Files:**
- Create: `src/CodeIndex.Core/Search/SymbolMatcher.cs`
- Create: `src/CodeIndex.Core/Search/HybridRanker.cs`
- Test: `tests/CodeIndex.Core.Tests/Search/SymbolMatcherTests.cs`
- Test: `tests/CodeIndex.Core.Tests/Search/HybridRankerTests.cs`

- [ ] **Step 1: Написать падающие тесты для символьной ветки**

`tests/CodeIndex.Core.Tests/Search/SymbolMatcherTests.cs`:

```csharp
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Search;
using Xunit;

namespace CodeIndex.Core.Tests.Search;

public sealed class SymbolMatcherTests
{
    private static CodeChunk Chunk(string symbol, string signature = "") => new()
    {
        FilePath = "f.cs",
        StartLine = 1,
        EndLine = 2,
        Kind = ChunkKind.Method,
        Symbol = symbol,
        Signature = signature,
        EmbedText = string.Empty,
    };

    [Fact]
    public void Match_ScoresExactLeafNameHighestThenPrefixThenSubstring()
    {
        List<CodeChunk> chunks =
        [
            Chunk("A.B.TrustSetFlags"),
            Chunk("A.B.TrustSetFlagsBuilder"),
            Chunk("A.B.ParseTrustSetFlagsFrom"),
            Chunk("A.B.Unrelated"),
        ];
        SymbolMatcher matcher = new(chunks);

        IReadOnlyList<ScoredIndex> hits = matcher.Match("TrustSetFlags", topK: 10);

        Assert.Equal(0, hits[0].Index);
        Assert.Equal(1, hits[1].Index);
        Assert.Equal(2, hits[2].Index);
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        SymbolMatcher matcher = new([Chunk("A.B.AccountInfo")]);

        Assert.Single(matcher.Match("accountinfo", topK: 5));
    }

    [Fact]
    public void Match_AlsoLooksAtSignature()
    {
        SymbolMatcher matcher = new([Chunk("A.B.Run", "Task<AccountInfoResponse> Run()")]);

        Assert.Single(matcher.Match("AccountInfoResponse", topK: 5));
    }

    [Fact]
    public void Match_ReturnsEmptyWhenQueryHasNoIdentifierLikeToken()
    {
        SymbolMatcher matcher = new([Chunk("A.B.Run")]);

        Assert.Empty(matcher.Match("   ", topK: 5));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~SymbolMatcherTests"`
Expected: ошибка компиляции — `SymbolMatcher` не существует

- [ ] **Step 3: Написать символьный матчер**

`src/CodeIndex.Core/Search/SymbolMatcher.cs`:

```csharp
using CodeIndex.Core.Chunking;

namespace CodeIndex.Core.Search;

/// <summary>
/// Literal identifier matching. Embeddings handle intent well but treat an exact type or
/// method name as a weak signal, so "where is TrustSetFlags" needs this branch to land.
/// </summary>
public sealed class SymbolMatcher
{
    private const float ExactLeafScore = 1.0f;
    private const float PrefixScore = 0.7f;
    private const float SubstringScore = 0.4f;
    private const float SignatureScore = 0.2f;

    private readonly IReadOnlyList<CodeChunk> _chunks;

    public SymbolMatcher(IReadOnlyList<CodeChunk> chunks) => _chunks = chunks;

    public IReadOnlyList<ScoredIndex> Match(string query, int topK)
    {
        string term = query.Trim();
        if (term.Length == 0)
            return [];

        List<ScoredIndex> hits = new();

        for (int i = 0; i < _chunks.Count; i++)
        {
            float score = ScoreOne(_chunks[i], term);
            if (score > 0f)
                hits.Add(new ScoredIndex(i, score));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Index)
            .Take(topK)
            .ToArray();
    }

    private static float ScoreOne(CodeChunk chunk, string term)
    {
        string symbol = chunk.Symbol;
        int lastDot = symbol.LastIndexOf('.');
        string leaf = lastDot >= 0 ? symbol[(lastDot + 1)..] : symbol;

        if (leaf.Equals(term, StringComparison.OrdinalIgnoreCase))
            return ExactLeafScore;

        if (leaf.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            return PrefixScore;

        if (symbol.Contains(term, StringComparison.OrdinalIgnoreCase))
            return SubstringScore;

        if (chunk.Signature.Contains(term, StringComparison.OrdinalIgnoreCase))
            return SignatureScore;

        return 0f;
    }
}
```

- [ ] **Step 4: Написать падающий тест для слияния**

`tests/CodeIndex.Core.Tests/Search/HybridRankerTests.cs`:

```csharp
using CodeIndex.Core.Search;
using Xunit;

namespace CodeIndex.Core.Tests.Search;

public sealed class HybridRankerTests
{
    [Fact]
    public void Fuse_RanksAnItemFoundByBothBranchesAboveEitherBranchLeader()
    {
        ScoredIndex[] vector = [new(10, 0.9f), new(20, 0.8f), new(30, 0.7f)];
        ScoredIndex[] symbol = [new(30, 1.0f), new(40, 0.7f)];

        IReadOnlyList<ScoredIndex> fused = HybridRanker.Fuse(vector, symbol, topK: 4);

        // Chunk 30 is rank 3 in one list and rank 1 in the other; that beats a single
        // first place, which is exactly the behaviour RRF is chosen for.
        Assert.Equal(30, fused[0].Index);
    }

    [Fact]
    public void Fuse_KeepsItemsSeenByOnlyOneBranch()
    {
        ScoredIndex[] vector = [new(1, 0.9f)];
        ScoredIndex[] symbol = [new(2, 0.9f)];

        IReadOnlyList<ScoredIndex> fused = HybridRanker.Fuse(vector, symbol, topK: 10);

        Assert.Equal(2, fused.Count);
    }

    [Fact]
    public void Fuse_HonoursTopK()
    {
        ScoredIndex[] vector = [new(1, 0.9f), new(2, 0.8f), new(3, 0.7f)];

        Assert.Equal(2, HybridRanker.Fuse(vector, [], topK: 2).Count);
    }

    [Fact]
    public void Fuse_ReturnsEmptyWhenBothBranchesAreEmpty()
    {
        Assert.Empty(HybridRanker.Fuse([], [], topK: 5));
    }
}
```

- [ ] **Step 5: Написать ранкер**

`src/CodeIndex.Core/Search/HybridRanker.cs`:

```csharp
namespace CodeIndex.Core.Search;

/// <summary>
/// Reciprocal Rank Fusion. Chosen over weighted score blending because cosine similarity
/// and literal match scores live on incomparable scales — fusing by rank sidesteps the
/// need to tune weights that would drift with every model change.
/// </summary>
public static class HybridRanker
{
    private const double RankConstant = 60.0;

    public static IReadOnlyList<ScoredIndex> Fuse(
        IReadOnlyList<ScoredIndex> vectorHits,
        IReadOnlyList<ScoredIndex> symbolHits,
        int topK)
    {
        Dictionary<int, double> fused = new();

        Accumulate(fused, vectorHits);
        Accumulate(fused, symbolHits);

        return fused
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(topK)
            .Select(pair => new ScoredIndex(pair.Key, (float)pair.Value))
            .ToArray();
    }

    private static void Accumulate(Dictionary<int, double> fused, IReadOnlyList<ScoredIndex> hits)
    {
        for (int rank = 0; rank < hits.Count; rank++)
        {
            double contribution = 1.0 / (RankConstant + rank + 1);
            fused[hits[rank].Index] = fused.GetValueOrDefault(hits[rank].Index) + contribution;
        }
    }
}
```

- [ ] **Step 6: Убедиться, что тесты проходят, и закоммитить**

Run: `dotnet test tests/CodeIndex.Core.Tests`
Expected: `Passed! - Failed: 0`

```bash
git add -A
git commit -m "feat: add symbol matcher and RRF hybrid ranker"
```

---

## Task 10: Сборка и инкрементальное обновление индекса

> **Требование, вскрытое замерами на `<indexed-project>`.** `CodeChunk.Symbol` **не уникален**: 125 значений повторяются, а `Xrpl.Models.Transactions.Validation` встречается 76 раз, потому что `partial class` разложен по 76 файлам. Перегрузки и дженерики разной арности схлопываются в одно имя тоже. Поэтому ни `Symbol`, ни пара `Symbol + Kind` не могут быть ключом: словарь по такому ключу потерял бы 75 чанков из 76. Идентичность чанка — это его порядковый номер в `IndexSnapshot.Chunks`, а группировка при инкрементальном обновлении идёт строго по `FilePath`.

**Files:**
- Create: `src/CodeIndex.Core/Indexing/IndexBuilder.cs`
- Create: `src/CodeIndex.Core/CodeIndexOptions.cs`
- Test: `tests/CodeIndex.Core.Tests/Indexing/IndexBuilderTests.cs`
- Test: `tests/CodeIndex.Core.Tests/Embedding/StubEmbeddingClient.cs`

- [ ] **Step 1: Написать детерминированный клиент эмбеддингов для тестов**

`tests/CodeIndex.Core.Tests/Embedding/StubEmbeddingClient.cs`:

```csharp
using CodeIndex.Core.Embedding;

namespace CodeIndex.Core.Tests.Embedding;

/// <summary>Deterministic vectors derived from the input hash — no Ollama, no network.</summary>
public sealed class StubEmbeddingClient : IEmbeddingClient
{
    public int Dimensions { get; } = 4;

    public string Model => "stub-model";

    public int CallCount { get; private set; }

    public int TotalInputs { get; private set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        CallCount++;
        TotalInputs += inputs.Count;

        List<float[]> vectors = new(inputs.Count);
        foreach (string input in inputs)
        {
            int seed = input.GetHashCode(StringComparison.Ordinal);
            float[] vector = new float[Dimensions];
            for (int i = 0; i < Dimensions; i++)
                vector[i] = ((seed >> (i * 4)) & 0xF) + 1;

            float norm = MathF.Sqrt(vector.Sum(v => v * v));
            for (int i = 0; i < Dimensions; i++)
                vector[i] /= norm;

            vectors.Add(vector);
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }
}
```

- [ ] **Step 2: Написать падающий тест**

`tests/CodeIndex.Core.Tests/Indexing/IndexBuilderTests.cs`:

```csharp
using CodeIndex.Core;
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Storage;
using CodeIndex.Core.Tests.Embedding;
using CodeIndex.Core.Tests.Sources;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeIndex.Core.Tests.Indexing;

public sealed class IndexBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ci-build-" + Guid.NewGuid().ToString("N"));
    private readonly StubEmbeddingClient _embedder = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private IndexBuilder CreateBuilder(InMemorySourceProvider source) => new(
        source,
        new ChunkerPipeline(new RoslynChunker(), new FallbackChunker()),
        _embedder,
        new IndexStore(_dir),
        Options.Create(new CodeIndexOptions { ProjectId = "test", ProjectRoot = "/irrelevant" }));

    private static InMemorySourceProvider TwoFiles() => new(new Dictionary<string, string>
    {
        ["a.cs"] = "namespace N;\npublic class A { public void One() { } }",
        ["b.cs"] = "namespace N;\npublic class B { public void Two() { } }",
    });

    [Fact]
    public async Task BuildAsync_EmbedsEveryChunkAndPersists()
    {
        InMemorySourceProvider source = TwoFiles();
        IndexBuilder builder = CreateBuilder(source);

        IndexSnapshot snapshot = await builder.BuildAsync(TestContext.Current.CancellationToken);

        // Two files, each producing one type chunk and one method chunk.
        Assert.Equal(4, snapshot.Chunks.Count);
        Assert.Equal(4 * _embedder.Dimensions, snapshot.Vectors.Length);
        Assert.True(File.Exists(Path.Combine(_dir, "vectors.bin")));
    }

    [Fact]
    public async Task RefreshAsync_DoesNothingWhenNothingChanged()
    {
        InMemorySourceProvider source = TwoFiles();
        IndexBuilder builder = CreateBuilder(source);
        await builder.BuildAsync(TestContext.Current.CancellationToken);
        int callsAfterBuild = _embedder.CallCount;

        await builder.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(callsAfterBuild, _embedder.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_ReEmbedsOnlyTheChangedFile()
    {
        InMemorySourceProvider source = TwoFiles();
        IndexBuilder builder = CreateBuilder(source);
        await builder.BuildAsync(TestContext.Current.CancellationToken);
        int inputsAfterBuild = _embedder.TotalInputs;

        source.Set("b.cs", "namespace N;\npublic class B { public void Renamed() { } }");
        IndexSnapshot snapshot = await builder.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(inputsAfterBuild + 2, _embedder.TotalInputs);
        Assert.Contains(snapshot.Chunks, c => c.Symbol == "N.B.Renamed");
        Assert.DoesNotContain(snapshot.Chunks, c => c.Symbol == "N.B.Two");
    }

    [Fact]
    public async Task RefreshAsync_DropsChunksOfDeletedFiles()
    {
        InMemorySourceProvider source = TwoFiles();
        IndexBuilder builder = CreateBuilder(source);
        await builder.BuildAsync(TestContext.Current.CancellationToken);

        source.Remove("b.cs");
        IndexSnapshot snapshot = await builder.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.Chunks.Count);
        Assert.Equal(2 * _embedder.Dimensions, snapshot.Vectors.Length);
        Assert.All(snapshot.Chunks, c => Assert.Equal("a.cs", c.FilePath));
    }

    [Fact]
    public async Task RefreshAsync_RebuildsWhenStoredModelDiffers()
    {
        InMemorySourceProvider source = TwoFiles();
        IndexBuilder builder = CreateBuilder(source);
        await builder.BuildAsync(TestContext.Current.CancellationToken);

        IndexStore store = new(_dir);
        IndexSnapshot stored = await store.LoadAsync(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("expected a snapshot");
        await store.SaveAsync(
            stored with { Header = stored.Header with { Model = "some-other-model" } },
            TestContext.Current.CancellationToken);

        int inputsBefore = _embedder.TotalInputs;
        await builder.RefreshAsync(TestContext.Current.CancellationToken);

        // Everything must be recomputed — mixing models silently corrupts ranking.
        Assert.Equal(inputsBefore + 4, _embedder.TotalInputs);
    }
}
```

- [ ] **Step 3: Убедиться, что тест падает**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~IndexBuilderTests"`
Expected: ошибка компиляции — `IndexBuilder` и `CodeIndexOptions` не существуют

- [ ] **Step 4: Написать опции**

`src/CodeIndex.Core/CodeIndexOptions.cs`:

```csharp
namespace CodeIndex.Core;

public sealed class CodeIndexOptions
{
    public const string SectionName = "CodeIndex";

    /// <summary>
    /// Cache key. Deliberately NOT derived from the project path: the same repository sits
    /// under different roots on different machines, and a path-derived key would make the
    /// cache non-portable for no benefit.
    /// </summary>
    public string ProjectId { get; set; } = "default";

    public string ProjectRoot { get; set; } = string.Empty;

    public string? CacheDirectory { get; set; }

    public string ResolveCacheDirectory() =>
        CacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "code-index-mcp",
            ProjectId);
}
```

- [ ] **Step 5: Написать сборщик**

`src/CodeIndex.Core/Indexing/IndexBuilder.cs`:

```csharp
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Embedding;
using CodeIndex.Core.Sources;
using CodeIndex.Core.Storage;
using Microsoft.Extensions.Options;

namespace CodeIndex.Core.Indexing;

public sealed class IndexBuilder
{
    private readonly ISourceProvider _source;
    private readonly ChunkerPipeline _chunker;
    private readonly IEmbeddingClient _embedder;
    private readonly IndexStore _store;
    private readonly CodeIndexOptions _options;

    public IndexBuilder(
        ISourceProvider source,
        ChunkerPipeline chunker,
        IEmbeddingClient embedder,
        IndexStore store,
        IOptions<CodeIndexOptions> options)
    {
        _source = source;
        _chunker = chunker;
        _embedder = embedder;
        _store = store;
        _options = options.Value;
    }

    public async Task<IndexSnapshot> BuildAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, FileEntry> entries = new(StringComparer.Ordinal);

        await foreach (string path in _source.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            entries[path] = await IndexFileAsync(path, cancellationToken).ConfigureAwait(false);
        }

        IndexSnapshot snapshot = Assemble(entries);
        await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>
    /// Brings the stored index up to date, re-embedding only what actually changed.
    /// Falls back to a full build when there is no cache or the cache is incompatible.
    /// </summary>
    public async Task<IndexSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        IndexSnapshot? existing;
        try
        {
            existing = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IndexCorruptedException)
        {
            _store.Delete();
            existing = null;
        }

        if (existing is null || !existing.Header.IsCompatibleWith(_embedder.Model, _embedder.Dimensions))
            return await BuildAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, FileEntry> entries = Decompose(existing);
        Dictionary<string, FileFingerprint> stored = existing.Fingerprints.ToDictionary(
            f => f.RelativePath, StringComparer.Ordinal);

        HashSet<string> seen = new(StringComparer.Ordinal);
        bool changed = false;

        await foreach (string path in _source.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            seen.Add(path);

            if (stored.TryGetValue(path, out FileFingerprint? fingerprint))
            {
                SourceFileStat stat = await _source.StatAsync(path, cancellationToken).ConfigureAwait(false);
                if (!fingerprint.NeedsContentCheck(stat))
                    continue;

                string text = await _source.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (fingerprint.MatchesContent(text))
                {
                    // git rewrote the timestamp but not the content — refresh the stat only.
                    entries[path] = entries[path] with
                    {
                        Fingerprint = fingerprint with { Length = stat.Length, LastWriteTimeUtc = stat.LastWriteTimeUtc },
                    };
                    changed = true;
                    continue;
                }
            }

            entries[path] = await IndexFileAsync(path, cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        foreach (string removed in entries.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            entries.Remove(removed);
            changed = true;
        }

        if (!changed)
            return existing;

        IndexSnapshot snapshot = Assemble(entries);
        await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async Task<FileEntry> IndexFileAsync(string path, CancellationToken cancellationToken)
    {
        string text = await _source.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        SourceFileStat stat = await _source.StatAsync(path, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CodeChunk> chunks = _chunker.ChunkFile(path, text);
        List<float> vectors = new();

        foreach (float[] vector in await EmbedInBatchesAsync(chunks, cancellationToken).ConfigureAwait(false))
            vectors.AddRange(vector);

        FileFingerprint fingerprint = new(path, stat.Length, stat.LastWriteTimeUtc, FileFingerprint.ComputeHash(text));
        return new FileEntry(chunks, vectors.ToArray(), fingerprint);
    }

    private async Task<IReadOnlyList<float[]>> EmbedInBatchesAsync(
        IReadOnlyList<CodeChunk> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
            return [];

        List<float[]> all = new(chunks.Count);
        const int batchSize = 16;

        for (int offset = 0; offset < chunks.Count; offset += batchSize)
        {
            string[] batch = chunks
                .Skip(offset)
                .Take(batchSize)
                .Select(c => c.EmbedText)
                .ToArray();

            all.AddRange(await _embedder.EmbedAsync(batch, cancellationToken).ConfigureAwait(false));
        }

        return all;
    }

    private IndexSnapshot Assemble(Dictionary<string, FileEntry> entries)
    {
        List<CodeChunk> chunks = new();
        List<FileFingerprint> fingerprints = new();
        List<float> vectors = new();

        foreach (string path in entries.Keys.Order(StringComparer.Ordinal))
        {
            FileEntry entry = entries[path];
            chunks.AddRange(entry.Chunks);
            vectors.AddRange(entry.Vectors);
            fingerprints.Add(entry.Fingerprint);
        }

        return new IndexSnapshot
        {
            Header = new IndexHeader
            {
                SchemaVersion = IndexHeader.CurrentSchemaVersion,
                Model = _embedder.Model,
                Dimensions = _embedder.Dimensions,
                ChunkCount = chunks.Count,
                BuiltAtUtc = DateTime.UtcNow,
            },
            Chunks = chunks,
            Fingerprints = fingerprints,
            Vectors = vectors.ToArray(),
        };
    }

    private Dictionary<string, FileEntry> Decompose(IndexSnapshot snapshot)
    {
        Dictionary<string, FileEntry> entries = new(StringComparer.Ordinal);
        Dictionary<string, FileFingerprint> fingerprints = snapshot.Fingerprints.ToDictionary(
            f => f.RelativePath, StringComparer.Ordinal);

        int dimensions = snapshot.Header.Dimensions;
        int cursor = 0;

        foreach (IGrouping<string, CodeChunk> group in snapshot.Chunks.GroupBy(c => c.FilePath, StringComparer.Ordinal))
        {
            CodeChunk[] chunks = group.ToArray();
            int length = chunks.Length * dimensions;

            // Block copy of the file's slice — never an element-by-element loop.
            float[] vectors = snapshot.Vectors.AsSpan(cursor, length).ToArray();
            cursor += length;

            entries[group.Key] = new FileEntry(chunks, vectors, fingerprints[group.Key]);
        }

        return entries;
    }

    private sealed record FileEntry(IReadOnlyList<CodeChunk> Chunks, float[] Vectors, FileFingerprint Fingerprint);
}
```

- [ ] **Step 6: Убедиться, что тесты проходят**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~IndexBuilderTests"`
Expected: `Passed! - Failed: 0, Passed: 5`

- [ ] **Step 7: Коммит**

```bash
git add -A
git commit -m "feat: add index builder with incremental refresh"
```

---

## Task 11: Фасад поиска

**Files:**
- Create: `src/CodeIndex.Core/Search/CodeIndexService.cs`
- Test: `tests/CodeIndex.Core.Tests/Search/CodeIndexServiceTests.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/CodeIndex.Core.Tests/Search/CodeIndexServiceTests.cs`:

```csharp
using CodeIndex.Core;
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Embedding;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Search;
using CodeIndex.Core.Storage;
using CodeIndex.Core.Tests.Embedding;
using CodeIndex.Core.Tests.Sources;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeIndex.Core.Tests.Search;

public sealed class CodeIndexServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ci-svc-" + Guid.NewGuid().ToString("N"));

    private readonly InMemorySourceProvider _source = new(new Dictionary<string, string>
    {
        ["Trust.cs"] = """
            namespace Xrpl.Models;

            public class TrustSetFlags
            {
                public void Apply() { }
            }
            """,
        ["Payment.cs"] = """
            namespace Xrpl.Models;

            public class PaymentBuilder
            {
                public void Build() { }
            }
            """,
    });

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private CodeIndexService CreateService(IEmbeddingClient embedder)
    {
        IOptions<CodeIndexOptions> options = Options.Create(
            new CodeIndexOptions { ProjectId = "test", CacheDirectory = _dir });

        IndexStore store = new(_dir);
        IndexBuilder builder = new(
            _source,
            new ChunkerPipeline(new RoslynChunker(), new FallbackChunker()),
            embedder,
            store,
            options);

        return new CodeIndexService(builder, _source, embedder);
    }

    [Fact]
    public async Task SearchAsync_FindsBySymbolEvenWhenEmbeddingsAreUninformative()
    {
        CodeIndexService service = CreateService(new StubEmbeddingClient());

        IReadOnlyList<SearchHit> hits = await service.SearchAsync(
            "TrustSetFlags", limit: 3, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.Contains(hits, h => h.Chunk.Symbol == "Xrpl.Models.TrustSetFlags");
    }

    [Fact]
    public async Task SearchAsync_PopulatesExcerptFromTheSourceProvider()
    {
        CodeIndexService service = CreateService(new StubEmbeddingClient());

        SearchHit hit = (await service.SearchAsync(
                "TrustSetFlags", limit: 1, kind: null, pathFilter: null, TestContext.Current.CancellationToken))
            .First();

        Assert.Contains("class TrustSetFlags", hit.Excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_FiltersByKindAndPath()
    {
        CodeIndexService service = CreateService(new StubEmbeddingClient());

        IReadOnlyList<SearchHit> hits = await service.SearchAsync(
            "Build", limit: 10, kind: ChunkKind.Method, pathFilter: "Payment", TestContext.Current.CancellationToken);

        Assert.All(hits, h => Assert.Equal(ChunkKind.Method, h.Chunk.Kind));
        Assert.All(hits, h => Assert.Equal("Payment.cs", h.Chunk.FilePath));
    }

    [Fact]
    public async Task SearchAsync_DegradesToSymbolBranchWhenEmbeddingsAreUnavailable()
    {
        // Ollama being down must not make the tool useless.
        CodeIndexService service = CreateService(new StubEmbeddingClient());
        await service.RefreshAsync(TestContext.Current.CancellationToken);

        CodeIndexService broken = CreateService(new UnavailableEmbeddingClient());
        SearchResult result = await broken.SearchWithStatusAsync(
            "TrustSetFlags", limit: 5, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.True(result.EmbeddingsUnavailable);
        Assert.Contains(result.Hits, h => h.Chunk.Symbol == "Xrpl.Models.TrustSetFlags");
    }

    private sealed class UnavailableEmbeddingClient : IEmbeddingClient
    {
        public int Dimensions => 4;

        public string Model => "stub-model";

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> inputs, CancellationToken cancellationToken) =>
            throw new EmbeddingUnavailableException("Ollama is unreachable. Start it with 'ollama serve'.");
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~CodeIndexServiceTests"`
Expected: ошибка компиляции — `CodeIndexService` и `SearchResult` не существуют

- [ ] **Step 3: Написать фасад**

`src/CodeIndex.Core/Search/CodeIndexService.cs`:

```csharp
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Embedding;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Sources;
using CodeIndex.Core.Storage;

namespace CodeIndex.Core.Search;

public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, bool EmbeddingsUnavailable, string? Warning);

public sealed class CodeIndexService
{
    private const int BranchDepth = 50;
    private const int ExcerptLines = 15;

    private readonly IndexBuilder _builder;
    private readonly ISourceProvider _source;
    private readonly IEmbeddingClient _embedder;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IndexSnapshot? _snapshot;

    public CodeIndexService(IndexBuilder builder, ISourceProvider source, IEmbeddingClient embedder)
    {
        _builder = builder;
        _source = source;
        _embedder = embedder;
    }

    public async Task<IndexSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _snapshot = await _builder.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IndexSnapshot> RebuildAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _snapshot = await _builder.BuildAsync(cancellationToken).ConfigureAwait(false);
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query, int limit, ChunkKind? kind, string? pathFilter, CancellationToken cancellationToken)
    {
        SearchResult result = await SearchWithStatusAsync(query, limit, kind, pathFilter, cancellationToken)
            .ConfigureAwait(false);
        return result.Hits;
    }

    public async Task<SearchResult> SearchWithStatusAsync(
        string query, int limit, ChunkKind? kind, string? pathFilter, CancellationToken cancellationToken)
    {
        IndexSnapshot snapshot = await RefreshAsync(cancellationToken).ConfigureAwait(false);

        int[] candidates = FilterCandidates(snapshot, kind, pathFilter);
        if (candidates.Length == 0)
            return new SearchResult([], false, null);

        IReadOnlyList<ScoredIndex> symbolHits = new SymbolMatcher(snapshot.Chunks).Match(query, BranchDepth);

        IReadOnlyList<ScoredIndex> vectorHits = [];
        bool unavailable = false;
        string? warning = null;

        try
        {
            IReadOnlyList<float[]> embedded = await _embedder
                .EmbedAsync([query], cancellationToken).ConfigureAwait(false);
            vectorHits = new VectorSearcher(snapshot.Vectors, snapshot.Header.Dimensions)
                .Search(embedded[0], BranchDepth);
        }
        catch (EmbeddingUnavailableException ex)
        {
            unavailable = true;
            warning = $"Semantic ranking is off: {ex.Message} Falling back to symbol matching only.";
        }

        HashSet<int> allowed = [.. candidates];
        IReadOnlyList<ScoredIndex> fused = HybridRanker.Fuse(
            [.. vectorHits.Where(h => allowed.Contains(h.Index))],
            [.. symbolHits.Where(h => allowed.Contains(h.Index))],
            limit);

        List<SearchHit> hits = new(fused.Count);
        foreach (ScoredIndex scored in fused)
        {
            CodeChunk chunk = snapshot.Chunks[scored.Index];
            hits.Add(new SearchHit
            {
                ChunkId = scored.Index,
                Chunk = chunk,
                Score = scored.Score,
                Excerpt = await ReadExcerptAsync(chunk, ExcerptLines, cancellationToken).ConfigureAwait(false),
            });
        }

        return new SearchResult(hits, unavailable, warning);
    }

    public async Task<SearchHit?> GetChunkAsync(int chunkId, CancellationToken cancellationToken)
    {
        IndexSnapshot snapshot = _snapshot ?? await RefreshAsync(cancellationToken).ConfigureAwait(false);

        if (chunkId < 0 || chunkId >= snapshot.Chunks.Count)
            return null;

        CodeChunk chunk = snapshot.Chunks[chunkId];
        return new SearchHit
        {
            ChunkId = chunkId,
            Chunk = chunk,
            Score = 0,
            Excerpt = await ReadExcerptAsync(chunk, int.MaxValue, cancellationToken).ConfigureAwait(false),
        };
    }

    public IndexSnapshot? Current => _snapshot;

    private async Task<string> ReadExcerptAsync(CodeChunk chunk, int maxLines, CancellationToken cancellationToken)
    {
        int end = maxLines == int.MaxValue
            ? chunk.EndLine
            : Math.Min(chunk.EndLine, chunk.StartLine + maxLines - 1);

        try
        {
            return await _source
                .ReadLinesAsync(chunk.FilePath, chunk.StartLine, end, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The file moved between refresh and read; the next query will re-index it.
            return string.Empty;
        }
    }

    private static int[] FilterCandidates(IndexSnapshot snapshot, ChunkKind? kind, string? pathFilter)
    {
        List<int> allowed = new();

        for (int i = 0; i < snapshot.Chunks.Count; i++)
        {
            CodeChunk chunk = snapshot.Chunks[i];

            if (kind is not null && chunk.Kind != kind)
                continue;

            if (!string.IsNullOrWhiteSpace(pathFilter) &&
                !chunk.FilePath.Contains(pathFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            allowed.Add(i);
        }

        return allowed.ToArray();
    }
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~CodeIndexServiceTests"`
Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "feat: add search facade with refresh-before-query and graceful degradation"
```

---

## Task 12: MCP-сервер

**Files:**
- Create: `src/CodeIndex.Server/Tools/CodeSearchTools.cs`
- Create: `src/CodeIndex.Server/Program.cs`
- Create: `src/CodeIndex.Server/appsettings.json`
- Test: `tests/CodeIndex.Server.Tests/CodeSearchToolsTests.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/CodeIndex.Server.Tests/CodeSearchToolsTests.cs`:

```csharp
using System.Text.Json;
using CodeIndex.Core;
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Embedding;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Search;
using CodeIndex.Core.Sources;
using CodeIndex.Core.Storage;
using CodeIndex.Server.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeIndex.Server.Tests;

public sealed class CodeSearchToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ci-tools-" + Guid.NewGuid().ToString("N"));
    private readonly string _cache;

    public CodeSearchToolsTests()
    {
        _cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(
            Path.Combine(_root, "src", "Ledger.cs"),
            "namespace Xrpl;\n\npublic class LedgerClient\n{\n    public void Fetch() { }\n}\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CodeSearchTools CreateTools()
    {
        FileSystemSourceProvider source = new(_root);
        IOptions<CodeIndexOptions> options = Options.Create(
            new CodeIndexOptions { ProjectId = "test", ProjectRoot = _root, CacheDirectory = _cache });

        IndexBuilder builder = new(
            source,
            new ChunkerPipeline(new RoslynChunker(), new FallbackChunker()),
            new ConstantEmbeddingClient(),
            new IndexStore(_cache),
            options);

        return new CodeSearchTools(new CodeIndexService(builder, source, new ConstantEmbeddingClient()), options);
    }

    [Fact]
    public async Task SearchAsync_ReturnsJsonWithChunkIdPathAndLines()
    {
        CodeSearchTools tools = CreateTools();

        string json = await tools.SearchAsync("LedgerClient", 5, null, null, TestContext.Current.CancellationToken);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement first = document.RootElement.GetProperty("hits")[0];

        Assert.True(first.TryGetProperty("id", out _));
        Assert.Equal("src/Ledger.cs", first.GetProperty("path").GetString());
        Assert.True(first.GetProperty("start_line").GetInt32() > 0);
    }

    [Fact]
    public async Task StatusAsync_ReportsModelDimensionsAndChunkCount()
    {
        CodeSearchTools tools = CreateTools();
        await tools.SearchAsync("LedgerClient", 1, null, null, TestContext.Current.CancellationToken);

        string json = await tools.StatusAsync(TestContext.Current.CancellationToken);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("constant", document.RootElement.GetProperty("model").GetString());
        Assert.True(document.RootElement.GetProperty("chunk_count").GetInt32() >= 2);
    }

    [Fact]
    public async Task GetChunkAsync_ReturnsFullBodyForAKnownId()
    {
        CodeSearchTools tools = CreateTools();
        string search = await tools.SearchAsync("Fetch", 5, null, null, TestContext.Current.CancellationToken);

        using JsonDocument document = JsonDocument.Parse(search);
        int id = document.RootElement.GetProperty("hits")[0].GetProperty("id").GetInt32();

        string json = await tools.GetChunkAsync(id, TestContext.Current.CancellationToken);

        Assert.Contains("LedgerClient", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetChunkAsync_ReturnsAnErrorPayloadForAnUnknownId()
    {
        CodeSearchTools tools = CreateTools();
        await tools.SearchAsync("Fetch", 1, null, null, TestContext.Current.CancellationToken);

        string json = await tools.GetChunkAsync(99999, TestContext.Current.CancellationToken);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("error", out _));
    }

    private sealed class ConstantEmbeddingClient : IEmbeddingClient
    {
        public int Dimensions => 2;

        public string Model => "constant";

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> inputs, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => new[] { 1f, 0f }).ToArray());
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/CodeIndex.Server.Tests`
Expected: ошибка компиляции — `CodeSearchTools` не существует

- [ ] **Step 3: Написать инструменты**

`src/CodeIndex.Server/Tools/CodeSearchTools.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using CodeIndex.Core;
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Search;
using CodeIndex.Core.Storage;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace CodeIndex.Server.Tools;

[McpServerToolType]
public sealed class CodeSearchTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly CodeIndexService _service;
    private readonly CodeIndexOptions _options;

    public CodeSearchTools(CodeIndexService service, IOptions<CodeIndexOptions> options)
    {
        _service = service;
        _options = options.Value;
    }

    [McpServerTool(Name = "code_search")]
    [Description(@"Semantic + symbol search over the indexed C# project. PREFER THIS OVER Grep when looking for where something is implemented — it returns a handful of relevant members instead of every textual match.
Returns for each hit: id (pass to code_get_chunk), path, line range, symbol, signature, doc comment and the first ~15 lines of the body.
The index refreshes itself before every query, so results always reflect the code on disk.")]
    public async Task<string> SearchAsync(
        [Description("What you are looking for. Natural language ('where are trust lines validated') or an exact identifier ('TrustSetFlags') both work.")] string query,
        [Description("Maximum number of hits. Default 10.")] int limit = 10,
        [Description("Optional filter: Method, Property, Constructor, Class, Interface, Struct, Record, Enum.")] string? kind = null,
        [Description("Optional case-insensitive substring filter on the file path.")] string? path_filter = null,
        CancellationToken cancellationToken = default)
    {
        ChunkKind? parsedKind = null;
        if (!string.IsNullOrWhiteSpace(kind) && Enum.TryParse(kind, ignoreCase: true, out ChunkKind value))
            parsedKind = value;

        SearchResult result = await _service
            .SearchWithStatusAsync(query, limit, parsedKind, path_filter, cancellationToken)
            .ConfigureAwait(false);

        var payload = new
        {
            query,
            count = result.Hits.Count,
            warning = result.Warning,
            hits = result.Hits.Select(h => new
            {
                id = h.ChunkId,
                path = h.Chunk.FilePath,
                start_line = h.Chunk.StartLine,
                end_line = h.Chunk.EndLine,
                kind = h.Chunk.Kind.ToString(),
                symbol = h.Chunk.Symbol,
                signature = h.Chunk.Signature,
                doc = string.IsNullOrWhiteSpace(h.Chunk.DocComment) ? null : h.Chunk.DocComment,
                excerpt = h.Excerpt,
            }),
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    [McpServerTool(Name = "code_get_chunk")]
    [Description("Return the complete body of one member by the id from code_search. Cheaper than reading the whole file when you only need that member.")]
    public async Task<string> GetChunkAsync(
        [Description("Chunk id from a code_search hit.")] int id,
        CancellationToken cancellationToken = default)
    {
        SearchHit? hit = await _service.GetChunkAsync(id, cancellationToken).ConfigureAwait(false);

        if (hit is null)
            return JsonSerializer.Serialize(new { error = $"No chunk with id {id}. Run code_search again — ids change after a reindex." }, JsonOptions);

        var payload = new
        {
            id = hit.ChunkId,
            path = hit.Chunk.FilePath,
            start_line = hit.Chunk.StartLine,
            end_line = hit.Chunk.EndLine,
            symbol = hit.Chunk.Symbol,
            signature = hit.Chunk.Signature,
            body = hit.Excerpt,
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    [McpServerTool(Name = "code_index_status")]
    [Description("Report index health: chunk count, embedding model, vector dimensions, and when it was last built.")]
    public async Task<string> StatusAsync(CancellationToken cancellationToken = default)
    {
        IndexSnapshot snapshot = _service.Current
            ?? await _service.RefreshAsync(cancellationToken).ConfigureAwait(false);

        var payload = new
        {
            project_id = _options.ProjectId,
            project_root = _options.ProjectRoot,
            cache_directory = _options.ResolveCacheDirectory(),
            model = snapshot.Header.Model,
            dimensions = snapshot.Header.Dimensions,
            chunk_count = snapshot.Header.ChunkCount,
            file_count = snapshot.Fingerprints.Count,
            built_at_utc = snapshot.Header.BuiltAtUtc,
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    [McpServerTool(Name = "code_reindex")]
    [Description("Force a full rebuild of the index. Normally unnecessary — the index refreshes itself before every search. Use after changing the embedding model.")]
    public async Task<string> ReindexAsync(CancellationToken cancellationToken = default)
    {
        IndexSnapshot snapshot = await _service.RebuildAsync(cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(
            new { rebuilt = true, chunk_count = snapshot.Header.ChunkCount, file_count = snapshot.Fingerprints.Count },
            JsonOptions);
    }
}
```

- [ ] **Step 4: Написать хост**

`src/CodeIndex.Server/Program.cs`:

```csharp
using System.Diagnostics;
using CodeIndex.Core;
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Embedding;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Search;
using CodeIndex.Core.Sources;
using CodeIndex.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeIndex.Server;

public static class Program
{
    public static async Task Main(string[] args)
    {
        HostApplicationBuilderSettings settings = new()
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        };
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(settings);

        // stdio carries the protocol, so every log line must go to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "CODEINDEX_");

        builder.Services.Configure<CodeIndexOptions>(
            builder.Configuration.GetSection(CodeIndexOptions.SectionName));
        builder.Services.Configure<EmbeddingOptions>(
            builder.Configuration.GetSection(EmbeddingOptions.SectionName));

        builder.Services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>((provider, client) =>
        {
            EmbeddingOptions embedding = provider.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            client.BaseAddress = new Uri(embedding.Endpoint);
            client.Timeout = TimeSpan.FromMinutes(10);
        });

        builder.Services.AddSingleton<RoslynChunker>();
        builder.Services.AddSingleton<FallbackChunker>();
        builder.Services.AddSingleton<ChunkerPipeline>();

        builder.Services.AddSingleton<ISourceProvider>(provider =>
        {
            CodeIndexOptions options = provider.GetRequiredService<IOptions<CodeIndexOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.ProjectRoot))
                throw new InvalidOperationException(
                    "CodeIndex:ProjectRoot is not set. Point it at the repository to index, e.g. /path/to/YourProject.");

            return new FileSystemSourceProvider(options.ProjectRoot);
        });

        builder.Services.AddSingleton(provider =>
        {
            CodeIndexOptions options = provider.GetRequiredService<IOptions<CodeIndexOptions>>().Value;
            return new IndexStore(options.ResolveCacheDirectory());
        });

        builder.Services.AddSingleton<IndexBuilder>();
        builder.Services.AddSingleton<CodeIndexService>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(Program).Assembly);

        IHost host = builder.Build();

        // Maintenance flags let the index be built and inspected without an MCP client
        // attached, which is what the first-run and benchmarking steps need.
        if (args.Contains("--build-only", StringComparer.Ordinal))
        {
            await RunBuildOnlyAsync(host).ConfigureAwait(false);
            return;
        }

        if (args.Contains("--status", StringComparer.Ordinal))
        {
            await RunStatusAsync(host).ConfigureAwait(false);
            return;
        }

        await host.RunAsync();
    }

    private static async Task RunBuildOnlyAsync(IHost host)
    {
        CodeIndexService service = host.Services.GetRequiredService<CodeIndexService>();

        long startedAt = Stopwatch.GetTimestamp();
        IndexSnapshot snapshot = await service.RebuildAsync(CancellationToken.None).ConfigureAwait(false);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

        Console.WriteLine(
            $"Indexed {snapshot.Fingerprints.Count} files into {snapshot.Header.ChunkCount} chunks in {elapsed:mm\\:ss}.");
    }

    private static async Task RunStatusAsync(IHost host)
    {
        CodeIndexService service = host.Services.GetRequiredService<CodeIndexService>();
        CodeIndexOptions options = host.Services.GetRequiredService<IOptions<CodeIndexOptions>>().Value;

        long startedAt = Stopwatch.GetTimestamp();
        IndexSnapshot snapshot = await service.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        TimeSpan refresh = Stopwatch.GetElapsedTime(startedAt);

        string vectorsPath = Path.Combine(options.ResolveCacheDirectory(), "vectors.bin");
        long cacheBytes = File.Exists(vectorsPath) ? new FileInfo(vectorsPath).Length : 0;

        startedAt = Stopwatch.GetTimestamp();
        await service.SearchAsync("account info", 10, null, null, CancellationToken.None).ConfigureAwait(false);
        TimeSpan query = Stopwatch.GetElapsedTime(startedAt);

        Console.WriteLine($"Model:        {snapshot.Header.Model} ({snapshot.Header.Dimensions} dims)");
        Console.WriteLine($"Files:        {snapshot.Fingerprints.Count}");
        Console.WriteLine($"Chunks:       {snapshot.Header.ChunkCount}");
        Console.WriteLine($"Built at UTC: {snapshot.Header.BuiltAtUtc:O}");
        Console.WriteLine($"Cache size:   {cacheBytes / 1024.0 / 1024.0:F1} MB");
        Console.WriteLine($"Refresh:      {refresh.TotalMilliseconds:F0} ms");
        Console.WriteLine($"Query:        {query.TotalMilliseconds:F0} ms");
    }
}
```

К списку `using` в начале файла добавить `System.Diagnostics` — он нужен для `Stopwatch`.

- [ ] **Step 5: Написать `appsettings.json`**

`src/CodeIndex.Server/appsettings.json`:

```json
{
  "CodeIndex": {
    "ProjectId": "myproject",
    "ProjectRoot": "/path/to/YourProject"
  },
  "Embedding": {
    "Endpoint": "http://localhost:11434",
    "Model": "qwen3-embedding:4b",
    "Dimensions": 1024
  }
}
```

Добавить в `src/CodeIndex.Server/CodeIndex.Server.csproj`:

```xml
  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
```

- [ ] **Step 6: Убедиться, что тесты проходят**

Run: `dotnet test`
Expected: `Passed! - Failed: 0` по обоим тестовым проектам

- [ ] **Step 7: Коммит**

```bash
git add -A
git commit -m "feat: expose search, chunk fetch, status and reindex as MCP tools"
```

---

## Task 13: Проверка изоляции файловой системы

Критерий приёмки 8 из спеки: ничто вне `Sources` не трогает `File` и `Directory` напрямую. Проверяется тестом, чтобы абстракция не протекла со временем.

**Files:**
- Test: `tests/CodeIndex.Core.Tests/Architecture/SourceIsolationTests.cs`

- [ ] **Step 1: Написать тест**

```csharp
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace CodeIndex.Core.Tests.Architecture;

public sealed class SourceIsolationTests
{
    [Fact]
    public void OnlyTheSourcesNamespaceReferencesFileSystemTypes()
    {
        Assembly core = typeof(CodeIndex.Core.Sources.ISourceProvider).Assembly;
        using FileStream stream = File.OpenRead(core.Location);
        using PEReader pe = new(stream);
        MetadataReader metadata = pe.GetMetadataReader();

        List<string> offenders = new();

        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            string ns = ResolveNamespace(metadata, type);
            string name = metadata.GetString(type.Name);

            if (ns.StartsWith("CodeIndex.Core.Sources", StringComparison.Ordinal))
                continue;

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                    continue;

                MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);
                if (UsesFileSystem(metadata, body))
                    offenders.Add($"{ns}.{name}.{metadata.GetString(method.Name)}");
            }
        }

        // IndexStore is the one sanctioned exception: it owns the cache files, which live
        // outside the indexed project and therefore outside ISourceProvider's remit.
        offenders.RemoveAll(o => o.StartsWith("CodeIndex.Core.Storage.IndexStore", StringComparison.Ordinal));

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Compiler-generated async state machines are nested types, and nested types carry an
    /// empty Namespace field in ECMA metadata. Reading it directly would leave every
    /// iterator and async method unattributed, so the namespace is taken from the
    /// outermost declaring type instead.
    /// </summary>
    private static string ResolveNamespace(MetadataReader metadata, TypeDefinition type)
    {
        TypeDefinition current = type;

        while (true)
        {
            TypeDefinitionHandle declaring = current.GetDeclaringType();
            if (declaring.IsNil)
                return metadata.GetString(current.Namespace);

            current = metadata.GetTypeDefinition(declaring);
        }
    }

    private static bool UsesFileSystem(MetadataReader metadata, MethodBodyBlock body)
    {
        byte[] il = body.GetILBytes() ?? [];

        for (int i = 0; i + 4 < il.Length; i++)
        {
            // 0x28 call, 0x6F callvirt — the token follows as a 4-byte little-endian value.
            if (il[i] is not (0x28 or 0x6F))
                continue;

            int token = BitConverter.ToInt32(il, i + 1);
            EntityHandle handle = MetadataTokens.EntityHandle(token);

            if (handle.Kind != HandleKind.MemberReference)
                continue;

            MemberReference reference = metadata.GetMemberReference((MemberReferenceHandle)handle);
            if (reference.Parent.Kind != HandleKind.TypeReference)
                continue;

            TypeReference declaring = metadata.GetTypeReference((TypeReferenceHandle)reference.Parent);
            string typeName = metadata.GetString(declaring.Name);

            if (typeName is "File" or "Directory" or "FileInfo" or "DirectoryInfo")
                return true;
        }

        return false;
    }
}
```

- [ ] **Step 2: Прогнать тест**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~SourceIsolationTests"`
Expected: `Passed! - Failed: 0, Passed: 1`

Если тест падает, он назовёт нарушителя. Правильная реакция — перенести обращение к ФС за `ISourceProvider`, а не расширять список исключений.

**Обязательная проверка на ложноотрицательность.** Тест обязан ловить реальное нарушение, а не просто всегда проходить. Временно добавь в любой класс вне `Sources` строку `File.Exists("probe")`, убедись, что тест падает и называет этот метод, затем убери. Без этой проверки нельзя утверждать, что ограничение действительно защищено: анализатор, который молча ничего не находит, выглядит точно так же, как анализатор, который работает.

Отдельно проверь, что тест **не** считает нарушителями `EnumerateAsync` и `ReadLinesAsync` самого `FileSystemSourceProvider`. Их машины состояний — вложенные типы, а у вложенных типов поле `Namespace` в метаданных пустое; именно для этого добавлен `ResolveNamespace`.

- [ ] **Step 3: Коммит**

```bash
git add -A
git commit -m "test: enforce filesystem access only through ISourceProvider"
```

---

## Task 14: Интеграционный тест качества поиска

Единственный тест, измеряющий пользу инструмента. Остальные проверяют механику.

**Files:**
- Test: `tests/CodeIndex.Core.Tests/Integration/SearchQualityTests.cs`

- [ ] **Step 1: Написать тест на синтетическом проекте**

```csharp
using CodeIndex.Core;
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Search;
using CodeIndex.Core.Storage;
using CodeIndex.Core.Tests.Embedding;
using CodeIndex.Core.Tests.Sources;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeIndex.Core.Tests.Integration;

public sealed class SearchQualityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ci-quality-" + Guid.NewGuid().ToString("N"));

    private static readonly Dictionary<string, string> Project = new()
    {
        ["Client/XrplClient.cs"] = """
            namespace Xrpl.Client;

            public class XrplClient
            {
                /// <summary>Retrieves account root information.</summary>
                public string AccountInfo(string address) => address;
            }
            """,
        ["Models/TrustSetFlags.cs"] = """
            namespace Xrpl.Models;

            public class TrustSetFlags
            {
                public const int SetNoRipple = 131072;
            }
            """,
        ["Ledger/OfferBook.cs"] = """
            namespace Xrpl.Ledger;

            public class OfferBook
            {
                public void Cancel(int sequence) { }
            }
            """,
    };

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    public static TheoryData<string, string> GoldenQueries() => new()
    {
        { "TrustSetFlags", "Xrpl.Models.TrustSetFlags" },
        { "AccountInfo", "Xrpl.Client.XrplClient.AccountInfo" },
        { "SetNoRipple", "Xrpl.Models.TrustSetFlags" },
        { "OfferBook", "Xrpl.Ledger.OfferBook" },
        { "Cancel", "Xrpl.Ledger.OfferBook.Cancel" },
    };

    [Theory]
    [MemberData(nameof(GoldenQueries))]
    public async Task Search_PutsTheExpectedSymbolInTopThree(string query, string expectedSymbol)
    {
        CodeIndexService service = CreateService();

        IReadOnlyList<SearchHit> hits = await service.SearchAsync(
            query, limit: 3, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.Contains(hits, h => h.Chunk.Symbol.StartsWith(expectedSymbol, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_SurvivesAFullEditCycle()
    {
        InMemorySourceProvider source = new(new Dictionary<string, string>(Project));
        CodeIndexService service = CreateService(source);

        await service.RefreshAsync(TestContext.Current.CancellationToken);

        source.Set("Ledger/OfferBook.cs", """
            namespace Xrpl.Ledger;

            public class OfferBook
            {
                public void CancelOffer(int sequence) { }
            }
            """);

        IReadOnlyList<SearchHit> hits = await service.SearchAsync(
            "CancelOffer", limit: 3, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.Contains(hits, h => h.Chunk.Symbol == "Xrpl.Ledger.OfferBook.CancelOffer");
    }

    private CodeIndexService CreateService(InMemorySourceProvider? provider = null)
    {
        InMemorySourceProvider source = provider ?? new InMemorySourceProvider(new Dictionary<string, string>(Project));
        StubEmbeddingClient embedder = new();

        IndexBuilder builder = new(
            source,
            new ChunkerPipeline(new RoslynChunker(), new FallbackChunker()),
            embedder,
            new IndexStore(_dir),
            Options.Create(new CodeIndexOptions { ProjectId = "quality", CacheDirectory = _dir }));

        return new CodeIndexService(builder, source, embedder);
    }
}
```

- [ ] **Step 2: Прогнать и убедиться, что все проходят**

Run: `dotnet test tests/CodeIndex.Core.Tests --filter "FullyQualifiedName~SearchQualityTests"`
Expected: `Passed! - Failed: 0, Passed: 6`

Тест использует `StubEmbeddingClient`, поэтому проверяет символьную ветку и механику обновления, а не качество эмбеддингов. Качество семантики проверяется вручную в задаче 15 — автоматизировать его на детерминированной заглушке невозможно.

- [ ] **Step 3: Коммит**

```bash
git add -A
git commit -m "test: add golden-query search quality suite"
```

---

## Task 15: Боевой прогон и подключение

**Files:**
- Create: `README.md`
- Modify: `<indexed-project>\CLAUDE.md`

- [ ] **Step 1: Установить Ollama и модель**

```bash
winget install Ollama.Ollama
```

Затем в отдельном окне:

```bash
ollama pull qwen3-embedding:4b
```

Проверить, что сервис отвечает:

```bash
curl http://localhost:11434/api/tags
```

Expected: JSON со списком моделей, включающим `qwen3-embedding:4b`

- [ ] **Step 2: Проверить фактическую размерность модели**

```bash
curl -s http://localhost:11434/api/embed -d "{\"model\":\"qwen3-embedding:4b\",\"input\":\"test\"}"
```

Посчитать длину массива в ответе. Если родная размерность меньше 1024, уменьшить `Embedding:Dimensions` в `appsettings.json` до фактической — усечение вверх невозможно.

- [ ] **Step 3: Построить индекс `<indexed-project>`**

```bash
dotnet run --project src/CodeIndex.Server -- --build-only
```

Expected: строка вида `Indexed 719 files into ~N chunks in MM:SS`. Ожидаемое время — 4–8 минут, ожидаемый размер `vectors.bin` — около 35 МБ при 9000 чанков.

- [ ] **Step 4: Замерить фактические характеристики**

```bash
dotnet run --project src/CodeIndex.Server -- --status
```

Записать в README фактические значения: число чанков, время сборки, размер кэша, время холодного и тёплого поиска. Если расхождение с оценками спеки больше чем вдвое — разобраться до перехода дальше.

- [ ] **Step 5: Зарегистрировать сервер**

```bash
claude mcp add code-index -- dotnet run --project <repo>/src/CodeIndex.Server
```

- [ ] **Step 6: Проверить вручную пять запросов**

Задать через инструмент `code_search` и убедиться, что нужный член попадает в топ-3:

1. `where are trust lines validated`
2. `TrustSetFlags`
3. `how is a payment transaction signed`
4. `account_lines request model`
5. `AMM deposit`

Запросы 1, 3 и 5 проверяют именно семантику — они не содержат точных имён символов, поэтому символьная ветка на них бесполезна. Если они не работают, а 2 и 4 работают, значит проблема в эмбеддингах, а не в остальном конвейере.

- [ ] **Step 7: Прописать правило в CLAUDE.md индексируемого проекта**

Добавить в `<indexed-project>\CLAUDE.md`:

```markdown
## Поиск по коду SDK

Для поиска места реализации использовать инструмент `code_search` MCP-сервера `code-index`, а не `Grep`. Он возвращает несколько релевантных членов с сигнатурами и номерами строк вместо всех текстовых совпадений.

`Grep` остаётся уместным только для точного поиска строковых литералов и для файлов, не являющихся `.cs`.
```

Без этого пункта инструмент не будет использоваться: `Grep` — поведение по умолчанию.

- [ ] **Step 8: Написать README**

`README.md` должен содержать: назначение, требования (Ollama, модель, .NET 10), команду регистрации, описание четырёх инструментов, фактические замеры из шага 4 и раздел про перенос кэша на вторую машину.

- [ ] **Step 9: Коммит**

```bash
git add -A
git commit -m "docs: add README with measured characteristics and setup steps"
```

---

## Проверка по критериям приёмки спеки

После задачи 15 пройти по списку из раздела 17 спеки и отметить каждый пункт:

| # | Критерий | Чем проверяется |
|---|---|---|
| 1 | Индекс строится одной командой | Задача 15, шаг 3 |
| 2 | Эталонные запросы дают нужный член в топ-3 | Задача 14 (механика) + задача 15, шаг 6 (семантика) |
| 3 | Повторный поиск быстрее секунды | Задача 15, шаг 4 |
| 4 | Изменение одного файла переиндексирует только его | `RefreshAsync_ReEmbedsOnlyTheChangedFile` |
| 5 | Смена ветки без изменения содержимого не вызывает переиндексацию | `FileFingerprintTests.Matches_IsTrueWhenContentHashIsUnchanged` |
| 6 | Остановленный Ollama не ломает инструмент | `SearchAsync_DegradesToSymbolBranchWhenEmbeddingsAreUnavailable` |
| 7 | Кэш с другой машины принимается без переиндексации | Проверить вручную: скопировать кэш, запустить `code_index_status`, убедиться, что `built_at_utc` не изменился |
| 8 | Обращения к ФС только через `ISourceProvider` | `SourceIsolationTests` |
| 9 | Все тесты проходят | `dotnet test` |

Пункт 7 не покрыт автотестом — он требует двух машин. Проверяется вручную один раз при первом переносе.

