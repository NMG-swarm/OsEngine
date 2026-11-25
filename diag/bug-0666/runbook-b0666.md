# runbook-b0666 — инструментирование тихой смерти движка (tr362)

Пакет: `test/0666-instr` (fork, без PR), база `ef063250a` (2302..2305 включены).
Дифф: `project/OsEngine/Logging/FirstChanceProbe.cs` (+167) и одна строка вызова в `App.xaml.cs`.
Зонд включается **только** переменной `OSENGINE_DIAG_FIRSTCHANCE=1`; без неё код инертен
(self-check `Artifacts/Tests/bug-0666/run-check.ps1` → `RESULT=PASS`). В PR ветка не попадает.

Раскладка пакета (рядом со снапом; снап после freeze не мутируем):

```
Artifacts\Stands\bug-0666\runbook-b0666.md
Artifacts\Stands\bug-0666\run-b0666.ps1            <- обёртка: DEATH+exit code, WER-свидетель, сэмплер, сбор CrashStart
Artifacts\Stands\bug-0666\launch-detached.ps1      <- запуск вне job-объекта вызова (§3.0, ОБЯЗАТЕЛЕН для живых фаз)
Artifacts\Stands\bug-0666\p1\OsEngine.exe          <- эталоны смерти (лежат В КОРНЕ p1, не bin\Release\...)
Artifacts\Stands\bug-0666\p1\P1.csproj             <- пересборка: dotnet build P1.csproj -c Release -o .
Artifacts\Tests\bug-0666\run-check.ps1             <- self-check зонда
Снап: Shared\OsEngineTests\bug-0666-diag-01 (680 файлов)
Результаты и дампы: C:\OsEngineDumps\b0666\  (том один, C:, ~486 ГБ свободно)
```

Перед прогоном скопируй `run-b0666.ps1` и папку `p1\` в каталог снапа.

## 0. Подготовка (админ, один раз)

```
reg add "HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\OsEngine.exe" /v DumpType   /t REG_DWORD        /d 3 /f
reg add "HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\OsEngine.exe" /v DumpFolder /t REG_EXPAND_SZ    /d "C:\OsEngineDumps\b0666" /f
reg add "HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\OsEngine.exe" /v DumpCount  /t REG_DWORD        /d 8 /f
reg add "HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting" /v DontShowUI /t REG_DWORD /d 1 /f
mkdir C:\OsEngineDumps\b0666
```

`DumpType=3` = Full (не 2 — там Heap). **Дампу не верим**: под реальным OOM он не материализуется
(P1 подтвердил: `failfast-oom` → дампа нет с двух заходов). Свидетели — см. таблицу §4.

## 1. Порядок P1 → P5 (отдельным прогоном, до живого репро)

Все три режима гоняются **обёрткой**: она и есть измеряемый прибор.

```
cd <снап>
powershell -File run-b0666.ps1 -Exe .\p1\OsEngine.exe -WorkDir .\p1 -OutDir C:\OsEngineDumps\b0666\p1 -IntervalSec 2 -Arguments @('exit0')
powershell -File run-b0666.ps1 -Exe .\p1\OsEngine.exe -WorkDir .\p1 -OutDir C:\OsEngineDumps\b0666\p1 -IntervalSec 2 -Arguments @('kill')
powershell -File run-b0666.ps1 -Exe .\p1\OsEngine.exe -WorkDir .\p1 -OutDir C:\OsEngineDumps\b0666\p1 -IntervalSec 2 -Arguments @('failfast-oom')
```

### P1 — эталонные подписи смерти (RUN_FACTS Смотрителя 25.11, хост Смотрителя)

| режим | DEATH exitcode | WER 1000/1001 + Report.wer | дамп LocalDumps | вывод |
|---|---|---|---|---|
| `exit0` | `0x00000000` | нет | нет | чистый выход |
| `kill` (`Process.Kill` = TerminateProcess) | `0xFFFFFFFF` | нет | нет | **внешнее снятие** |
| `failfast-oom` (необработанный OOM фонового треда) | `0xE0434352` | **ЕСТЬ** (1000 `0xe0434352` KERNELBASE, 1001 CLR20r3, Report.wer ~2.7 КБ) | **НЕТ** (2 захода) | **a′-семейство** |

Главный дискриминатор P1: **FailFast всегда оставляет WER 1000/1001, даже когда дамп не состоялся.**
Дамп — не свидетель. Консоль `Out of memory.` при `failfast-oom` — норма.

Перепроверено Фиксером 25.11 17:04 на этом же хосте, обёрткой из пакета: `exit0` → `0x00000000` +
строка `WER_NONE`; `kill` → `0xFFFFFFFF` + `WER_NONE`; `failfast-oom` → `0xE0434352` + две строки
`WER` (1000 `OsEngine.exe`/`KERNELBASE.dll`/`0xe0434352`, 1001 `CLR20r3`). Дампов в
`C:\OsEngineDumps\b0666` — **0**, `Report.wer` — ровно на два FailFast-прогона. Обёртка теперь сама
пишет `WER`/`WER_NONE` в CSV (`Get-WinEvent` по окну прогона), руками события не смотреть.

### P2 — пин ETL (только first-chance)

```
logman create trace b2 -p "Microsoft-Windows-DotNETRuntime" 0xFFFFFFFFFFFFFFFF 4 -o C:\OsEngineDumps\b0666\b2.etl -f bincirc -max 2048 -ets
... gоняешь failfast-oom ...
logman stop b2 -ets
tracerpt C:\OsEngineDumps\b0666\b2.etl -o C:\OsEngineDumps\b0666\etl -of XML   # ищем OutOfMemoryException
```

Kernel-Process ETL из пакета **исключён**: на хосте Смотрителя `b2k.etl` 8 КБ, `ProcessStop` = 0 событий
(канал не пишется в такой конфигурации). Свидетель кода выхода — обёртка (`DEATH,exitcode`), она
отработала на всех трёх эталонах. Отдельный Kernel-канал поднимать не будем: задача решается без него.
Если `OutOfMemoryException` в ETL нет — ETL не свидетель, живём на probe+обёртке, скажи мне.

### P3 — пин сборки (ловушка вендорного bin)

`snapshot-info.txt` снапа: `dll_sha256` = `7bae56823f7b14868808b4607319de1d8d3f640941e8d1fb392a2dc9fb4cb70f`,
`base_gate: fresh`, `branch: test/0666-instr`, `commit: 5ecb1c40c…`. Проверь на своей копии:

```
powershell -Command "(Get-FileHash .\OsEngine.dll -Algorithm SHA256).Hash"
powershell -Command "$b=[IO.File]::ReadAllBytes('.\OsEngine.dll'); [Text.Encoding]::GetEncoding(28591).GetString($b).IndexOf('D'+[char]0+'I'+[char]0+'A'+[char]0+'G'+[char]0)"
```

Не −1 = в снапе наш зонд. Поиск **байтовый**: UTF-16-литералы лежат не по чётному смещению, обычный
`Encoding.Unicode.GetString` + `IndexOf` даёт ложный «не найдено» (на этом обожглись с маркером −05).

### P4 — пин зонда в живом движке (без биржи)

Запусти снап обёрткой, дай подняться. В `<каталог OsEngine.exe>\Engine\CrashStart.log`:
`DIAG PROBE ATTACHED`, далее `DIAG HB...` раз в 30 с с `WS`/`GC`. Обёртка читает порт MCP из **первой
строки** `Engine\McpSettings.txt` (вторая строка — API-ключ, её не читаем и не логируем) — в CSV должно
быть `mcp=ok`, не `mcp=na`.

### P5 — пин обёртки (промерено 25.11 16:54–16:55 на снапе, 90 с)

В `Engine\CrashStart.log`: `DIAG PROBE ATTACHED`, `DIAG HB` каждые 30 с, на остановке
`BeginGracefulShutdown programmatic=True`. Teardown снял по PID через 5 с → в CSV
`DEATH ... exitcode=0xFFFFFFFF`, `WER_NONE`, `COLLECTED ...CrashStart.log`, `collected=CrashStart.log`.
**Важно:** graceful-стоп, прерванный внешним снятием, даёт **тот же код, что килл**. Развязка — по
маркерам (таблица §4, строка 4). `DIAG PROCESS_EXIT` появляется только если Shutdown доходит до конца
сам; в этом P5 он не появился, потому что процесс дождали снятием. Порт MCP обёртка читает из первой
строки `Engine\McpSettings.txt` (промерено: `6510`), до правки было `mcp=na`.

## 2. Убийца: что уже найдено (25.11, read-only + живой опыт)

### 2.0 Киллер харнесса — воспроизведён

Обычный `Process.Start`-потомок, оставленный сиротой (именно так делает tr362-запуск
`launch-engine.ps1`), **снимается вместе с деревом shell-вызова через ~2 с после его конца**:
heartbeat-потомок дописал биты `17:03:30.964 / 32.978 / 34.987` и замолк; процесс, созданный
`Win32_Process.Create` (его поднимает WmiPrvSE, вне дерева вызова), дожил до своего конца.
Семантика = TerminateProcess: без WER, без CrashStart, exit `0xFFFFFFFF` — ровно подпись tr362.
Поэтому:

- живой прогон (обёртка и движок под ней) запускать **только** `launch-detached.ps1` (§3.0),
  иначе «тихая смерть» будет самоубийством харнесса;
- версия «посмертный таймаут shell-вызова» для tr362 всё ещё не сходится: `launch-engine.ps1` выходит
  сразу после `Process.Start`, вызов закончился ~13:48:04, а смерть №1 — ~13:54:55 (uptime 6.9 мин),
  смерть №2 — uptime ~99 с. Один таймаут под оба не ложится, третья сессия тем же скриптом жила 2 ч.
  Нужен журнал вызовов той сессии (что висело в 13:54:5x и 13:57:5x, был ли прогон `background=true`).

### 2.1 Свидетели, которых раньше не было

- **RestartManager**: 22 сессии Rm в 13:57:21–13:57:46 (старт+стоп каждую секунду) — вспышка
  Installer-стиля принудительного закрытия «держателей файлов» прямо перед верхом смерти №2
  (13:57:57.492); под смертью №1 (13:54:55) Rm-сессий нет вообще.
- **http.sys** (`System`, HttpService 111/113) даёт точные старты MCP-линзера: 13:48:04.878 (`*:6510`,
  сессия 1), 13:55:03.021 и 13:55:06.371 (`*:6500` — чужой терминал перезапустился через 8 с после
  смерти №1), 13:56:18.232 (сессия 2), 13:59:11.128 (сессия 3). Снятий линзера http.sys не пишет.
- WER 1000/1001 за окно смертей — **0** (перепроверено Фиксером по Application), в тот же день на
  FailFast-эталонах — 4 пары: журнал живой, свидетель работает.

### 2.2 Киллеры по пути/имени в наших харнессах

Архив tr362 (§4) — TerminateProcess-семейство, а не краш OsEngine. Киллеры по пути/имени живут
в наших же харнессах (префикс `Connectors\` — папка роли-тестера):

- `Connectors\System\Temp\teardown_0587.ps1:37` — `EXTRA KILL`: `Stop-Process`/`Kill` по **любому**
  `OsEngine`, у которого `ExecutablePath -like '...\Shared\OsEngineTests\*'` — то есть по любому тестовому
  снапу, включая каталог tr362 −05;
- `Connectors\mp\cleanup_diag0471.ps1:8`, `Connectors\System\mp\killbuild.ps1:1-2` — `Stop-Process -Force`
  по `Path -like "<чужой снап>*"`;
- `Connectors\System\tr137\teardown.ps1`, `tmp\teardown_0496.ps1`, `tmp\bfut_disconnect_kill.ps1` —
  снятие по PID/имени;
- дисциплина есть (`Connectors\System\run\guard\guard_launch.ps1`: `teardown_policy = kill only PIDs in
  pid_tree; no image-name taskkill`), но перечисленные скрипты её не соблюдают.

Прогон фазы 0 (без ключей и ордеров, ~10 мин): тот же Connect-only трафик, но сборка лежит **вне**
`Shared\OsEngineTests` и exe переименован (например `C:\b0666\OsEngineX.exe`; для WER добавь тот же
LocalDumps-ключ на `OsEngineX.exe`). Выживет при тех же действиях → убийца привязан к пути/имени,
дефект не OsEngine. Умрёт так же → киллер не именной, тогда 1A/1B.

## 3. Живой репро (только после отмашки Прораба/владельца)

### 3.0 Запуск — только через `launch-detached.ps1`

Обёртка и движок под ней — дети shell-вызова; после конца вызова харнесс снимает дерево
(TerminateProcess, см. §2.0). Правильно:

```
powershell -File launch-detached.ps1 -WorkDir <снап> -Command 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File <снап>\run-b0666.ps1 -Exe <снап>\OsEngine.exe -WorkDir <снап> -OutDir C:\OsEngineDumps\b0666\<test-id> -IntervalSec 5 -MarkFile C:\OsEngineDumps\b0666\marks.txt'
```

Вызов вернёт `detached pid=…` сразу; результат забираешь из `summary-*.txt` / `sampler-*.csv` в OutDir.
Прямой запуск обёртки вforeground-вызове допустим только на короткие P1/P4-пины.

Один заход = `run-b0666.ps1 -Exe .\OsEngine.exe -WorkDir . -Proxy <host:port>
-OutDir C:\OsEngineDumps\b0666\<test-id> -IntervalSec 5 -MarkFile C:\OsEngineDumps\b0666\marks.txt`.
Обёртка ставит `OSENGINE_DIAG_FIRSTCHANCE=1`, пишет `MARK`, `SAMPLE`, `DEATH` с кодом, `WER`/`WER_NONE`,
`MCP_MISS3`, и забирает `Engine\CrashStart.log`.

- **1A, Connect-only.** TInvest market data, токен даёт Мастер Коннекторов. 5 циклов: connect → дождаться
  ~4540 бумаг → держать 60 с → disconnect → 20 с. Ордеров, портфелей, истории нет.
- **1B, шторм connect/disconnect.** 20 циклов connect→securities→disconnect, пауза 15 с. Триггер (б) tr362.
- **1C, обрыв туннеля.** tr362: `PIPE-END IOException` в туннеле через ~1 с после Cancel. Прогони 1B дважды:
  с `-Proxy <живой cut-tunnel>` и с `-Proxy <заведомо мёртвый порт>`. Смерть только с живым прокси → обрыв
  ни при чём (причинность в tr362 могла быть обратной: умер движок → сокет оборвался).

## 4. Вердикт — четыре свидетеля (первое совпадение)

| # | Наблюдение | Вывод |
|---|---|---|
| 1 | `DIAG FIRST-CHANCE OOM` в CrashStart или `OutOfMemoryException` в ETL до `DEATH` | **a′**, кто держит память — по дампу/EWS |
| 2 | `exitcode=0xE0434352` **или** WER `Application Error 1000` + `1001 CLR20r3` (+`Report.wer`) в окне | **a′-семейство** (FailFast: OOM/AV/stack overflow). Дампу не верить |
| 3 | `exitcode=0xFFFFFFFF` **и** `DIAG PROCESS_EXIT` нет **и** `BeginGracefulShutdown`/`TerminateNow caller=` нет **и** WER нет | **c′**, внешнее снятие (TerminateProcess) |
| 4 | `exitcode=0xFFFFFFFF` **и** есть `BeginGracefulShutdown programmatic=…` / `TerminateNow caller=…` (сторож 12 с) | **штатный teardown**, не баг |
| 5 | `exitcode=0x00000000` + `DIAG PROCESS_EXIT` | чистый выход через Shutdown |
| 6 | `ws_mb`/`committed_mb` монотонно по циклам (>15% за цикл) или commit → limit | **leak-ветка**, отдельный дефект |
| 7 | `DEATH` нет, heartbeat оборвался | мертва обёртка вместе с движком — дефект харнесса |

Ре-вердикт по архиву tr362 −05: строки 1, 2 и 4 выключены (CrashStart.log в каталоге exe отсутствует —
все каналы выхода движка пишут туда: `MainWindow.xaml.cs:329` `TerminateNow caller=`, `:365`
`BeginGracefulShutdown programmatic=`, `:476`/`:514` OOM-маркеры), WER-событий за окно нет ⇒ остаётся
**строка 3: внешнее снятие (TerminateProcess)**. OsEngine по этим двум смертям не виноват: ни одна
внутрипроцессная ветка смерти не могла уйти без следа в CrashStart + WER.
Ведущая гипотеза убийцы — харнесс: §2.0 (дерево shell-вызова снимается после конца вызова; воспроизведено)
+ §2.2 (teardown-скрипты со снятием по пути/имени). Поэтому гонять 1A/1B/1C как «репро краша OsEngine»
бессмысленно: сначала **фаза 0** (§2.2) — те же действия, но сборка вне `Shared\OsEngineTests`, exe
переименован и запущен через `launch-detached.ps1`. Выжила → закрывать как внешнюю причину и править
харнесс; умрёт так же → тогда 1A/1B на живом трафике.
