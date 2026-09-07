# LogRotateWin

Порт утилиты **logrotate 3.22.0** из Ubuntu (исходные тексты лежат в папке
`logrotate-3.22.0`) под Windows на платформе .NET (проект `LogRotateWin`).

## Об утилите

`logrotate` автоматизирует ротацию, сжатие, удаление и отправку по почте
файлов журналов. Порт сохраняет совместимость конфигурационных файлов и
поведение оригинальной версии в той мере, в какой это возможно под Windows.

Основные особенности порта:

- ротация по времени (hourly/daily/weekly/monthly/yearly), по размеру
  (`size`/`minsize`/`maxsize`) и принудительно (`-f`, `--force`);
- сжатие выполняется встроенными средствами .NET (GZip) по умолчанию либо
  внешней программой через директивы `compresscmd`/`compressoptions`;
- отправка журналов по почте выполняется так же, как в Linux: вызывается
  `mail -s <subject> <address>`, содержимое журнала (при необходимости
  распакованное) подаётся в stdin команды; команду можно переопределить
  флагом `-m`/`--mail` или директивой `mailcmd`;
- директива `create` реально применяет к создаваемым файлам права доступа
  (DACL) и владельца из ключевой строки `create <mode> <owner> <group>`;
- структура state-файла совместима с оригиналом.

Проект включает две группы тестов:

- `LogRotateWin.LegacyTests` — порт оригинальных shell-тестов logrotate;
- `Plecos.LogRotateWin.Tests` — интеграционные и unit-тесты поведения, 
    импортированные из проекта [plecos.logrotatewin](https://github.com/plecos/logrotatewin)

## Новые директивы конфигурационного файла

После портирования к стандартному набору директив logrotate 3.22.0 были
добавлены следующие директивы (в оригинальной версии 3.22.0 отсутствуют):

### `uncompressoptions <options...>`

Аргументы командной строки для программы распаковки (`uncompresscmd`) —
аналог `compressoptions` для `compresscmd`.

По умолчанию распаковка сжатого журнала перед отправкой по почте
выполняется встроенными средствами .NET. Если задан `uncompresscmd` и
`uncompressoptions`, порт запускает внешнюю программу распаковки с указанными
аргументами, подавая сжатый файл в её stdin, а stdout программы перенаправляет
в команду отправки почты.

Пример:

```
"c:\logs\app.log" {
    rotate 7
    compress

    compresscmd   c:\Program Files\7-Zip\7z.exe
    compressoptions a dummy.xz -si -so
    compressext   .xz

    uncompresscmd   c:\Program Files\7-Zip\7z.exe
    uncompressoptions x -si -txz -so

    mail yar229@home.loc
    mailfirst
}
```

Обратите внимание: формат, задаваемый для распаковки, должен совпадать с
форматом, в котором файлы были сжаты (в примере файлы сжимаются в XZ,
поэтому для распаковки нужен `-txz`).

## Сборка и запуск

```
dotnet build LogRotateWin\LogRotateWin.csproj
dotnet run --project LogRotateWin -- --help
```

## Тесты

```
dotnet test LogRotateWin.LegacyTests
dotnet test Plecos.LogRotateWin.Tests
```