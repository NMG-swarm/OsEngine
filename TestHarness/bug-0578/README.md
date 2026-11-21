# bug-0578 — юнит-харнесс маппинга steps (BUG-0577, BybitData)

Листинг харнесса для зачёта покрытия ОдТС (plan-20261121-018). Тесты T1-T6 + K1-K7.
Канонический рантайм прогона — у агента-владельца (Фиксер-Младший); в PR фикса `fix/bug-0578` не входит.

## Шов

Reflection на `BybitDataServerRealization.ConvertSecurities` + поле `_securities` + spy на `LogMessageEvent`.
Синтетические raw-дампы формы `instruments-info` V5 (по форме tr302), без сети, без ключей.
Оракулы: поля `Security` (PriceStep/PriceStepCost/VolumeStep) + подстроки логов. NEVER печатные счётчики
FIX-0578 (:325/:327) — они проверяются на АДЕКВАТНОСТЬ (реальный visited, real step-counts), но решения тестов от них не зависят.

## Запуск

```powershell
# из корня checkout (ветка test/bug-0578) — fix-режим на checkout fix/bug-0578:
dotnet build TestHarness\bug-0578\bug-0578.Tests.csproj -c Release -p:EngineProject=<worktree-fix>\project\OsEngine\OsEngine.csproj
& TestHarness\bug-0578\bin\Release\net10.0-windows\bug0578-tests.exe        # ждём exit 0, «RESULT ALL: PASS»

# base-режим (A/B) на checkout origin/master (b853f080b):
dotnet build ... -p:EngineProject=<worktree-master>\project\OsEngine\OsEngine.csproj
& ...\bug0578-tests.exe base                                                 # ждём exit 0 (K2b/K3b/T5b)
```

Инкрементал не доверять: чистить obj/bin харнесса и obj\Release движка, `--no-incremental /nodeReuse:false`.

## Ожидания

fix-режим: exit 0; base-режим: exit 0. Выходы: 0 PASS, 1 FAIL, 3 unhandled, 4 watchdog.

- K1: матрица TryParseStep (инвариантный парсер; ru-RU не ломает; `1e-999` -> false; 27-значная дробь не бросает).
- K1b: «1,5»->false (запятая не инвариантный разделитель), «1e999»->false (overflow), «+0.01»->true (LeadingSign),
  «  0.01  »->true (Leading/TrailingWhite у NumberStyles.Float).
- K2: A/B off-by-one — base: `list.Count==1` даёт 0 бумаг, сентинел режет последнюю; fix: последняя включительно.
- K3: A/B type-mismatch вложенного фильтра. FACT (иное поведение, чем в ТЗ, честно): `priceFilter:[]` и `:123`
  бросают JsonSerializationException на десериализации (в приложении — catch GetSecuritiesByCategory:223,
  ровно одна Error :225, категория пропущена, соседние целы :178-181); `priceFilter:""` бросок НЕ даёт —
  Newtonsoft отдаёт null, guard скипает бумагу с логой. Ни один вариант не грузит бумагу с нулями.
  base: все три грузятся (полей DTO нет).
- K4: value-дискриминант фолбэка: SYMA без qtyStep -> basePrecision 0.007; SYMB qtyStep=0.01 при bp=0.007 -> 0.01;
  SYMC qtyStep='' -> скип, лога содержит `qtyStep='' basePrecision='0.007'`.
- K5: 2 битых Trading -> ровно 2 skip-логи; Settling/Delisted без логой; «TRADING»/«Trading» проходят ToLower-гейт.
- K6: 5x tickSize="0" -> added=0 skipped=5, skip-логи после цикла (до counters-строки), Error=0.
  FACT-наследие: AServer.cs:1429-1432 переспрашивает раз в 1с при пустом наборе (PR-note).
- K7: spy-подписчик бросает на первой skip-логе -> ровно одна Error «convert error», добавленные сохранены, наружу чисто.

## PR-note наследие (в дифф не входит)

NRE status null :237; limit=1000 без nextPageCursor; `_securities` не чистится между рестартами;
`ToLower()` -> `ToLowerInvariant` (:242, tr-TR «I»->«ı»); DecimalsVolume паритет с BybitServer (:974/:979);
inverse PriceStepCost=tickSize семантика не верифицирована; added==0 && trading>0 -> требовать Error.
