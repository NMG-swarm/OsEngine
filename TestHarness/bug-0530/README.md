# bug-0537 — юнит-харнесс маппинга анкетных полей ISS (MoexIssDataServer)

ТЗ-ред.2 (карточка `Bugs/bug-0537.md`, sync bug-0530 / BUG-0529). Харнесс написан
ДО кода фикса: секция A RED (маппер не реализован), секция B RED по смыслу
(синтетика без анкетных полей). После фикса на `fix/bug-0530` — все GREEN.

## Запуск

```
.\run.ps1                                    # база: bin\Debug клона FixPipeline
.\run.ps1 -Lib <bin fresh-сборки>             # после фикса
```

Выход: строки PASS/FAIL/INFO, `SUMMARY green/red`, `result.json` в bin-папке
харнесса, exit 0 только при 0 FAIL. OsEngine.dll не запускается — только
reflection-прогрузка типа из dll, сеть не трогается.

## Seam-контракт для сессии фикса (ОБЯЗАТЕЛЬНО, харнесс ищет по форме)

В `MoexDataServerRealization` (файл `MoexIssDataServer.cs`) появляется метод маппинга строки `securities`:

- сигнатура ровно: `(JArray columns, JArray row, string classParam) -> Security`
  (`JArray` = Newtonsoft.Json.Linq, `classParam` — элемент `classes` вида
  `stock#shares#TQBR#Акции`, `split('#')[2]` = доска);
- static или instance (instance — вызывается на неинициализированном объекте,
  полей экземпляра метод не трогает); public или private;
- имя произвольное (при нескольких совпадениях харнесс предпочитает имя с «Map»).

Семантика (ТЗ-ред.2): Exchange="MOEX"; State TQBR STATUS=="A"→Activ иначе Close,
RFUD LASTTRADEDATE>=сегодня→Activ иначе Close, индексы/CETS Activ; PriceStep
TQBR/RFUD←MINSTEP, индексы/CETS-фолбэк=1; F1: если step не из MINSTEP —
Decimals=0 принудительно; TQBR/RFUD Decimals←DECIMALS (null→по step), присваивать
ПОСЛЕ PriceStep; F2: RFUD PriceStepCost = STEPPRICE>0 ? STEPPRICE : MINSTEP,
иначе = PriceStep; Lot: TQBR LOTSIZE (null/0→1), иначе 1; VolumeStep=1;
SecurityType по доске: TQBR Stock, SECTYPE ETF/J→Fund (F4), RFUD Futures,
RTSI/SNDX Index, CETS CurrencyPair; null-safe: отсутствующая колонка (ColIndex -1)
→ дефолт, бросать запрещено, допустим возврат null только при невалидном SECID.
Дефолты при отсутствии колонок зафиксированы кейсами A17/A18/A20
(step=1, Decimals=0, TQBR/RFUD State=Close, Lot=1, тип по доске).

F3 (секция B) правит `CreateFuturesSection`/`GetFuturesForOneYearOncePer*`:
словарь база→шаблон по первому вхождению RFUD-контракта, копирование
PriceStep/PriceStepCost/Decimals/Lot/VolumeStep/SecurityType/Exchange в синтетику,
idEnding из NameId шаблона базы, явный фолбэк `allFutures[0]`, State=Close безусловно.
Фолбэк `allFutures[0]` из CreateFuturesSection недостижим — покрывается статически
на этапе ревью фикса (кейс S1), не юнитом.
F4: комментарий у ветки SECTYPE=="J"→Fund о работе только при наличии колонки
(проверяется статически при ревью, юнит A04 покрывает мёртвую ветку).

## Кейсы

| id | покрывает | ожидание до фикса |
|----|-----------|--------------------|
| A01–A10b | TQBR/RFUD маппинг, State, Lot, DECIMALS-null, F2 (BRZ6 live-сэмпл, STEPPRICE=null, граница LASTTRADEDATE) | RED (seam нет) |
| A12/A13 | RTSI/SNDX, F1 (step=1, Decimals=0 при DECIMALS=3/2), NameFull из NAME | RED (seam нет) |
| A22/A23 | tr270 Error 16: CNYRUB_TMS dec=6/step=0.0001 и класс-кейс dec=6/step=0.0025 -> Decimals=4 (колонка не участвует, ред.3b) | RED до фикса -> GREEN |
| A24 + A14v1/A22v1/A24v1 | guard от min-клампа (dec<знаков -> всё равно знаки шага) и реплика V1 IsCompairDecimalsAndStep (точное равенство) | RED до дофикса -> GREEN |
| U-01(+v1) | R-08: MINSTEP=1.5 БЕЗ DECIMALS -> Decimals=1 (unconditional; сеттер дал бы 0, V1 требует 1) | RED на -02 -> GREEN |
| U-03 | кросс-пин StepFractionDigits vs БОЕВОЙ IsCompairDecimalsAndStep, 11 шагов | GREEN (пин) |
| U-13a/b | сырой JSON (JObject.Parse, Date-токены): MINSTEP "1E-05" строкой и числом -> 0.00001/dec 5 | RED на -02 -> GREEN |
| U-14a/b/c | RFUD State по Date-токенам (+/−), DD.MM-амбигуити -> Close (ISS=ISO) | GREEN (пин) |
| U-15a/b/c | NumberStyles.Float: whitespace/.5 приняты, групповой разделитель -> фолбэк | GREEN (пин) |
| U-16 | round-trip GetSaveStr/LoadFromString анкеты | GREEN (пин) |
| S-01a/b/c | боевой Var_1 Check: база={6,9,10,11,12,13,14,17}, control Error16 мимо маппера, 6 анкет корпуса чисто | S-01c RED на -02 (member U-01) -> GREEN |
| A14/A15/A15b | CETS — фактические columns из дампа runs#350 | GREEN |
| A21 | CETS без колонок — null-safe дефолты (семейство A17/A18) | GREEN после фикса |
| A16 | независимость от позиций: те же значения при перестановке columns | RED (seam нет) |
| A17/A18/A20 | неизвестные/пустые columns — дефолты без падения; A19 SECID=null без исключения | RED (seam нет) |
| A01n/A08n/A12n | Name/NameId/NameClass/NameFull — soft: INFO, если маппер их не заполняет | INFO |
| B1 | CreateFuturesSection создаёт синтетику BR+Si (sanity) | GREEN |
| B2 | F3 State=Close безусловно | RED (UnKnown) |
| B3/B4 | F3 копирование анкеты шаблона базы (BR — first-occurrence BRQ6, не allFutures[0] SiZ6) | RED (0/None) |
| B5 | F3 idEnding из NameId шаблона (BR→«A#B#RFUD», Si→«C#D#RFUD») | RED (везде allFutures[0]) |
| B6 | шаблон-источник BRZ6 не мутируется | GREEN |

## Гейды для прогона после фикса (по книге знаний)

- Прогон GREEN имеет силу только против СВЕЖЕЙ сборки: в клоне `dotnet build
  project\OsEngine\OsEngine.csproj --no-incremental`, проверить `$LASTEXITCODE`,
  sha собранной `OsEngine.dll` != `git rev-parse HEAD:project/OsEngine/bin/Debug/OsEngine.dll`
  (вендорная база), затем `-Lib` на папку собранного вывода.
- sha dll пропечатан в шапке вывода харнесса и сверяется с коммитом ветки.
- A14/A15/A15b финалены по фактическим columns CETS из дампа Смотрителя (`runs#350`,
  F5 закрыта): MINSTEP есть (0.0001/0.0025), DECIMALS=4, LOTSIZE=100000/1000/1,
  LASTTRADEDATE/STEPPRICE в CETS нет; «CETS без MINSTEP» живёт только как A21 null-safe.
