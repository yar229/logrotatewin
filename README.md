# [LogRotateWin](https://github.com/yar229/logrotatewin)

Порт утилиты [logrotate](https://launchpad.net/ubuntu/+source/logrotate) v.3.22.0 из Ubuntu на платформу .NET 10 под Windows.
[Ubuntu manpages: logrotate](https://manpages.ubuntu.com/manpages/stonking/man8/logrotate.8.html)

## Об утилите

`logrotate` автоматизирует ротацию, сжатие, удаление и отправку по почте
файлов журналов. Порт сохраняет совместимость конфигурационных файлов и
поведение оригинальной версии в той мере, в какой это возможно под Windows.

Основные особенности порта:

- сжатие выполняется встроенными средствами .NET (GZip) по умолчанию либо
  внешней программой через директивы `compresscmd`/`compressoptions`;
- отправка журналов по почте выполняется так же, как в Linux: вызывается
  `mail -s <subject> <address>`, содержимое журнала (при необходимости
  распакованное) подаётся в stdin команды; команду можно переопределить
  флагом `-m`/`--mail` или директивой `mailscript`;
- директива `create` реально применяет к создаваемым файлам права доступа
  (DACL) и владельца из ключевой строки `create <mode> <owner> <group>`;
- директива `su uid gid` полностью игнорируется: файлы создаются с правами
  доступа по умолчанию Windows;

Проект включает три группы тестов:

- `LogRotateWin.LegacyTests` — порт оригинальных shell-тестов logrotate;
- `Plecos.LogRotateWin.Tests` — интеграционные и unit-тесты поведения, импортированные из проекта [plecos.logrotatewin](https://github.com/plecos/logrotatewin)
- `PostCsConvertation.Tests` — тесты, сделанные в процессе портирования и тестирование нового функционала

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
в команду отправки почты (если не указана `mailscript`)

Пример (с использованием архиватора [7-Zip](https://www.7-zip.org/)):

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

Формат, задаваемый для распаковки, должен совпадать с
форматом, в котором файлы были сжаты (в примере файлы сжимаются в XZ,
поэтому для распаковки нужен `-txz`).

### `mailscript`

Переопределяет команду отправки почты, задавая её в виде скрипта 
(блок между `mailscript` и `endscript`). Скрипт
запускается через `cmd.exe`, как и остальные скрипты директив.
В отличие от команды `–-mail <cmd>`, указанной в комадной строке, не ожидает
данные из stdin и не пишет в stdout.

В значении скрипта доступны параметры:

- `%1` (`%LOGROTATE_LOG%`) — путь к журналу, который отправляется по почте;
- `%2` (`%LOGROTATE_MAILTO%`) — адрес получателя из директивы `mail`.

Пример (с использованием почтовой утилиты [blat](http://www.blat.net)):

```
"c:\logs\app.log" {
    rotate 3
    mail sender@example.com

    mailscript
        echo mailing file %1 as attachment to address %2
        e:\Tools\blat\blat.exe -to %2 -s "Log rotated %1" -body "File %1 attached" -attach "%1" -p logrotate
    endscript
}
```

### Параметры в скриптах `prerotate`, `postrotate`, `preremove`

В скриптах директив `prerotate`, `postrotate` и `preremove` также можно
использовать параметры:

- `%1` (`%LOGROTATE_LOG%`) — путь к ротируемому журналу;
- `%2` (`%LOGROTATE_LOGROTATED%`) — путь к ротированному файлу.

Обратите внимание: для скриптов, выполняемых до того, как определено имя
ротированного файла (`prerotate`, `preremove`), параметр `%2`
(`%LOGROTATE_LOGROTATED%`) обычно пуст — полное значение он получает в
`postrotate`.

## Сборка и запуск

```
dotnet build LogRotateWin\LogRotateWin.csproj
dotnet run --project LogRotateWin -- --help
```

## Тесты

```
dotnet test LogRotateWin.LegacyTests
dotnet test Plecos.LogRotateWin.Tests
dotnet test PostCsConvertation.Tests 
```
