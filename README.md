# [LogRotateWin](https://github.com/yar229/logrotatewin)

Порт утилиты [logrotate](https://github.com/logrotate/logrotate) v.3.22.0 из Ubuntu на платформу .NET 10 под Windows.

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

- директива `su owner group` применяется при пересоздании лог-файла
  (`create`) и при создании каталога `createolddir`: создаваемому файлу/
  каталогу назначаются права доступа (DACL) и владелец/группа из
  `su`, если владелец не задан явно в `create`/`createolddir`
  (аналог `switch_user()` в оригинале; смена владельца возможна только
  при запуске от имени администратора); при указании пароля (директива
  `supasswd`) скрипты (`prerotate`, `postrotate`, `firstaction`,
  `lastaction`, `preremove`, `mailscript`) выполняются от имени
  пользователя, указанного в `su`;

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

### `supasswd <password>`

Пароль пользователя из директивы `su`. Когда пароль задан, скрипты
(`prerotate`, `postrotate`, `firstaction`, `lastaction`, `preremove`,
`mailscript`) выполняются от имени этого пользователя
(аналог `switch_user()` в оригинале): Windows-порт запускает их через
`CreateProcessWithLogonW` (флаг `LOGON_WITH_PROFILE`), так что использующая
процессов авторизация, сетевые доступы и т.п. принадлежат указанному
аккаунту, а не текущему.

Обратите внимание:

- пароль хранится в открытом виде в конфигурационном файле;
- запуск под другим пользователем требует, чтобы logrotate был запущен
  с правами администратора (нужна привилегия `SeImpersonatePrivilege`),
  а сам аккаунт должен иметь право «Allow log on locally», иначе 
  `CreateProcessWithLogonW` вернёт ошибку  и скрипты выполнятся от текущего аккаунта 
  (будет выведено   предупреждение);
- аккаунт должен быть локальным или доменным и существовать в системе.

```
"c:\logs\app.log" {
    rotate 7
    create 0644
    su appuser appgroup
    supasswd appuser_password

    postrotate
        type %1 >> %2
    endscript
}
```
Пример для ротации логов nginx, если он запущен как сервис под пользователем `nginx-user`:
```
"c:\logs\app.log" {
    ...
    ...
    su nginx-user Users
    supasswd nginx-user-password

    postrotate
        c:
		cd c:\Services\nginx 
		nginx.exe -s reopen
    endscript
}
```


Директива `supasswd` без `su` игнорируется (выводится предупреждение).

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
