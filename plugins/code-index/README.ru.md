> 🌐 **Язык**: [English](README.md) | **Русский**

# Плагин code-index

Локальный stdio MCP, который даёт Claude Code семантический поиск по одному или нескольким
C#-репозиториям. Запускает `CodeIndex.Server.dll` под **вашей** локальной средой выполнения
.NET 10; эмбеддинги считает **ваш** локальный Ollama (`qwen3-embedding:4b`) — сервер мейнтейнера
не участвует, никакой код не покидает вашу машину, кроме обращений к `localhost:11434`.

Четыре инструмента: `code_search`, `code_get_chunk`, `code_index_status`, `code_reindex`. Полное
описание параметров и рекомендации по использованию — во встроенном скилле `code-search`:
[`skills/code-search/SKILL.md`](skills/code-search/SKILL.md), либо просто попросите Claude
поискать по коду после установки плагина.

## Предварительные требования

В отличие от плагина, который обращается к публичному серверу, у этого есть реальные локальные
зависимости — лаунчер проверяет их все перед запуском сервера и точно сообщает, чего не хватает
(см. [Диагностика](#диагностика) ниже):

- **Среда выполнения .NET 10** в PATH (`dotnet --list-runtimes` показывает строку
  `Microsoft.NETCore.App 10.x`). Установка: <https://dotnet.microsoft.com/download/dotnet/10.0>.
- **Ollama**, запущенный локально (`ollama serve`).
- Скачанная модель **`qwen3-embedding:4b`** (`ollama pull qwen3-embedding:4b`, ~2.5 ГБ,
  разово).
- **Хотя бы один настроенный проект** — см. [Конфигурацию](#конфигурация) ниже. Здесь нет
  осмысленного значения по умолчанию: пути к репозиториям известны только вам.

## Установка

```text
/plugin marketplace add StaticBit-io/code-index-mcp
/plugin install code-index@code-index-mcp
```

(Либо для локальной копии репозитория: `/plugin marketplace add /path/to/code-index-mcp`.)

## Конфигурация

Создайте `~/.code-index-mcp/config.json` (Windows: `%USERPROFILE%\.code-index-mcp\config.json`)
с нужными проектами:

```json
{
  "CodeIndex": {
    "Projects": [
      { "Id": "myproject", "Root": "/path/to/MyProject" }
    ]
  }
}
```

Это та же форма, что и собственная конфигурация сервера `CodeIndex`/`Embedding` (см.
[README репозитория](../../README.md#manual-setup-build-from-source)) — `Id` — это ключ кэша, `Root` — абсолютный путь к
репозиторию, а необязательный список `Extensions` сужает или расширяет набор индексируемых
файлов для конкретного проекта. Добавьте больше записей, чтобы индексировать несколько
репозиториев одним сервером:

```json
{
  "CodeIndex": {
    "Projects": [
      { "Id": "myproject", "Root": "/path/to/MyProject" },
      { "Id": "otherproject", "Root": "/path/to/OtherProject", "Extensions": [".cs"] }
    ]
  },
  "Embedding": {
    "Endpoint": "http://localhost:11434",
    "Model": "qwen3-embedding:4b"
  }
}
```

`Embedding` необязателен — не указывайте его, чтобы использовать значения по умолчанию выше.

### Почему файл, а не переменные окружения

Если выражать несколько проектов только через переменные окружения, получится по одной
громоздкой индексированной переменной на каждое поле:

```bash
CODEINDEX_CodeIndex__Projects__0__Id=myproject
CODEINDEX_CodeIndex__Projects__0__Root=/path/to/MyProject
```

— а Claude Code не прокидывает произвольные переменные окружения хоста в подпроцесс плагина:
передаются только переменные, явно объявленные в `.mcp.json` плагина (см. `env` в
[`.mcp.json`](.mcp.json)). Динамический, неограниченный список вида «один проект — один индекс»
так объявить нельзя. Файл конфигурации, который лаунчер читает напрямую, такого ограничения не
имеет и остаётся единственным практичным способом настроить больше одного проекта. Заодно ваши
локальные пути к репозиториям никогда не попадают ни в `.mcp.json`, ни в переменные окружения,
ни куда-либо ещё, что хранит Claude Code.

### Приоритет

Три настройки **объявлены** в `.mcp.json` и могут быть переопределены обычным способом через
переменные окружения ОС/оболочки — `CODEINDEX_CONFIG_FILE` (указать другое расположение файла
конфигурации), `CODEINDEX_Embedding__Endpoint` и `CODEINDEX_Embedding__Model`. Для каждой
настройки переменная окружения, уже установленная на момент запуска сервера Claude Code,
побеждает то же самое значение из файла конфигурации; файл конфигурации заполняет то, что таким
образом ещё не установлено. На практике: используйте файл для `Projects` (той части, где
переменные окружения неудобны), а переменные окружения — только если нужно переопределить
endpoint или модель Ollama без правки файла.

Перезапустите Claude Code (или просто повторите поиск) после создания или изменения файла
конфигурации.

## Первый поиск после установки: это ожидаемо займёт минуты, а не секунды

При самом первом запуске `code_search` (или `code_reindex`) для проекта индекса ещё нет —
каждый файл нужно разбить на чанки и посчитать эмбеддинги с нуля. Для проекта в несколько сотен
файлов это **несколько минут**, а не зависание. `code_index_status` после завершения сообщает
показатели, относящиеся к прогрессу (число файлов/чанков, время последней сборки); последующие
поиски обновляются инкрементально и выполняются быстро (от долей секунды до нескольких секунд).
Если после первого поиска по только что настроенному проекту Claude выглядит «тихо» —
это строится индекс; дайте ему закончить, а не считайте, что что-то зависло.

## Проверка работы

```text
/mcp
```

Должно показать `code-index: connected, 4 tools`. Затем попробуйте:

```text
Найди в этом репозитории, где проверяется удаление trustline.
```

Агент выберет `mcp__plugin_code-index_code-index__code_search`.

## Диагностика

Лаунчер (`bin/server.js`) проверяет следующее до запуска сервера и вместо трассировки стека или
тихого зависания печатает в stderr одно из этих сообщений:

**Проект не настроен:**
```text
[code-index] No project is configured — there is nothing to search yet.

[code-index] Create <path>\.code-index-mcp\config.json with at least one project:

  {
    "CodeIndex": {
      "Projects": [
        { "Id": "myproject", "Root": "C:\\path\\to\\MyProject" }
      ]
    }
  }

[code-index] Then restart Claude Code so the server picks up the new configuration.
```

**Ollama не запущен:**
```text
[code-index] Cannot reach Ollama at http://localhost:11434.

[code-index] code-index-mcp needs Ollama running locally to compute embeddings.
[code-index] Start it with:

  ollama serve

[code-index] Then ask your question again.
```

**Модель не скачана:**
```text
[code-index] Ollama is running, but model 'qwen3-embedding:4b' is not pulled yet.

[code-index] Pull it (about 2.5 GB, one-time download):

  ollama pull qwen3-embedding:4b

[code-index] Then ask your question again.
```

**Отсутствие среды выполнения .NET 10** выдаёт похожее сообщение со ссылкой на
`dotnet --list-runtimes` и ссылкой для скачивания.

Сами сообщения лаунчер печатает на английском (это вывод инструмента, не документация) — здесь
они приведены как есть, чтобы вы могли узнать их в консоли.

Если сервер всё же запустился, но *позже* какой-то вызов эмбеддинга не удался (например, Ollama
остановили посреди сессии) — `code_search` не падает с ошибкой, а деградирует; см. раздел
«Поле `warning`» в скилле о том, что означает предупреждение об устаревшем индексе и почему
результаты всё равно можно использовать.

## Как загружается бинарник сервера

Репозиторий плагина **не** хранит собранный сервер — это ~14 МБ преимущественно бинарного
артефакта, который не дельта-сжимается в git, так что коммит этого файла на каждый релиз навсегда
увеличивал бы репозиторий. Вместо этого `bin/server.js` при первом использовании скачивает его из
[GitHub Release](https://github.com/StaticBit-io/code-index-mcp/releases) и кэширует локально:

1. Лаунчер читает поле `serverVersion` из собственного манифеста плагина (`.claude-plugin/plugin.json`
   — оно намеренно отделено от `version` самого плагина: релиз плагина, где меняются только скилл
   или документация, не должен заставлять заново скачивать 14 МБ сервера).
2. Если `~/.code-index-mcp/server/<version>/` уже содержит проверенную установку — сервер
   запускается сразу, **без какого-либо обращения к сети**.
3. Иначе лаунчер скачивает `code-index-server-<version>.tar.gz` из релиза с тегом
   `server-v<version>`, сверяет его SHA-256 с `bin/server.sha256` (закоммичен в этом репозитории —
   репозиторий и есть корень доверия, поэтому проверка никогда не зависит от того, что пришло по
   сети), распаковывает и только после этого запускает. Несовпадение контрольной суммы —
   безусловный отказ, файл не запускается.

При первой загрузке новой версии в stderr виден прогресс:

```text
[code-index] Server v0.2.0 not found in local cache — downloading from GitHub Releases (~14 MB, one-time)...
[code-index] Downloaded 14.1 MB / 14.1 MB (100%)
[code-index] Verifying checksum...
[code-index] Checksum OK — extracting...
[code-index] Server v0.2.0 installed at C:\Users\you\.code-index-mcp\server\0.2.0\
```

После этого каждый следующий запуск (любой проект, любая сессия) использует закэшированную
установку без сети — пока `serverVersion` не изменится.

**Локально собираете другую версию сервера?** Укажите `CODEINDEX_SERVER_DIR` на директорию с уже
опубликованной сборкой `CodeIndex.Server` (например, результат
`dotnet publish src/CodeIndex.Server -c Release -o some/dir` из копии [репозитория](../../)) — и
лаунчер запустит именно её, без скачивания и без проверки контрольной суммы. Это отладочная
лазейка для разработки, не то, что стоит выставлять для обычного использования.

### Если скачивание не удалось

**Сеть недоступна, в кэше ещё ничего нет:**
```text
[code-index] Server v0.2.0 is not installed yet, and GitHub could not be reached to download it.
[code-index] Network error: <underlying error>

[code-index] Check your internet connection and try again. If you are offline, download the release
[code-index] manually and extract it into the folder below:

  https://github.com/StaticBit-io/code-index-mcp/releases/download/server-v0.2.0/code-index-server-0.2.0.tar.gz

  C:\Users\you\.code-index-mcp\server\0.2.0\

[code-index] Then ask your question again — the launcher will find it there and skip the download.
```

**Несовпадение контрольной суммы (повреждённая загрузка или скомпрометированный ассет) — сервер
не запускается ни при каких условиях:**
```text
[code-index] Downloaded server v0.2.0 but its checksum does not match — refusing to run it.
[code-index]   expected: <64-char sha256>
[code-index]   actual:   <64-char sha256>

[code-index] This usually means a corrupted download or a compromised release asset. The file was
[code-index] not installed. Try again; if this keeps happening, please report it:

  https://github.com/StaticBit-io/code-index-mcp/issues
```

**Релиз для этой версии плагина не опубликован:**
```text
[code-index] No GitHub release found for server v0.2.0 (tag server-v0.2.0).

[code-index] This plugin build expects a matching server release that is not published — check
[code-index]   https://github.com/StaticBit-io/code-index-mcp/releases
[code-index] for available versions, or download it manually once published:

  https://github.com/StaticBit-io/code-index-mcp/releases/download/server-v0.2.0/code-index-server-0.2.0.tar.gz
```

**Приватный репозиторий, нет доступных учётных данных** (сегодня репозиторий приватный; тот же
код без изменений сработает и без токена, если репозиторий когда-нибудь станет публичным):
```text
[code-index] GitHub returned 401 while requesting the server v0.2.0 release.
[code-index] This repository is private and needs authentication to download release assets.

[code-index] Provide a token with 'repo' scope one of these ways:
[code-index]   - set CODEINDEX_GITHUB_TOKEN (or GH_TOKEN / GITHUB_TOKEN) in your environment, or
[code-index]   - authenticate the GitHub CLI (`gh auth login`) — the launcher borrows its token automatically

[code-index] Or download the asset manually with your browser and extract it into:

  C:\Users\you\.code-index-mcp\server\0.2.0\

  https://github.com/StaticBit-io/code-index-mcp/releases/download/server-v0.2.0/code-index-server-0.2.0.tar.gz
```

Сами сообщения лаунчер печатает на английском (это вывод инструмента, не документация) — здесь
они приведены как есть, чтобы вы могли узнать их в консоли.

### Параллельные установки

Если два окна Claude Code запускаются одновременно, каждое скачивает и распаковывает архив в
собственную уникально названную временную директорию внутри `~/.code-index-mcp/server/`, а затем
атомарно переименовывает её в финальный, именованный по версии путь. Кто закончил первым — тот и
победил; второй экземпляр обнаруживает уже готовую установку и просто использует её, вместо того
чтобы упасть с ошибкой или перезаписать файлы. Процесс, убитый посреди загрузки, оставляет мусор
только в своей собственной временной директории — никогда по пути, который проверяют другие
запущенные лаунчеры, — поэтому частично скачанный файл невозможно принять за исправный.

## Платформы

Опубликованная сборка — это **портируемая, зависящая от рантайма** публикация `CodeIndex.Server`:
во всём дереве зависимостей нет нативного/AOT-кода (чистый managed-код: Roslyn,
`System.Numerics.Tensors`, никакого SQLite или другого нативного interop), поэтому одна и та же
сборка запускается через `dotnet CodeIndex.Server.dll` на любой ОС с подходящей средой выполнения
.NET 10. Собирает её CI (`.github/workflows/release-server.yml`) на `ubuntu-latest` с
`-p:SatelliteResourceLanguages=en`, поэтому она не несёт отпечаток машины сборки, как это было бы
при сборке на собственной Windows-машине разработчика (в первую очередь — специфичные для локали
сателлитные сборки Roslyn).

## Приватность

- Ничего не покидает вашу машину, кроме исходящих HTTP-запросов к вашему собственному Ollama
  (`localhost:11434` по умолчанию), дисковых операций в корнях настроенных проектов и в кэше
  индекса на диске (`%LocalAppData%/code-index-mcp/<Id>` по умолчанию), а также — только при
  первом запуске конкретной версии плагина, и только пока её сборка сервера ещё не в кэше —
  исходящих HTTPS-запросов к `api.github.com` (метаданные релиза и, для маленьких ассетов, сама
  загрузка) и `objects.githubusercontent.com` (pre-signed storage-URL, на который `api.github.com`
  делает редирект для скачивания самого ассета; см. `downloadAssetBuffer` в `bin/server.js`) для
  скачивания этой сборки. Код проекта или поисковый запрос в этот запрос никогда не попадают; см.
  [Как загружается бинарник сервера](#как-загружается-бинарник-сервера).
- Процесс сервера живёт, пока Claude Code держит открытым stdio-канал — завершается вместе с
  клиентом.
