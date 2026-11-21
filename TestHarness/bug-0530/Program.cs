// bug-0537 (BUG-0529, sync bug-0530) — юнит-харнесс маппинга анкетных полей ISS
// в MoexIssDataServer по ТЗ-ред.2 (F1–F5). Пишется ДО кода фикса: до фикса
// секция A ожидаемо RED (seam-маппер отсутствует), секция B RED по смыслу
// (синтетика истёкших без анкетных полей). После фикса — все GREEN.
//
// Seam-контракт для фикса (см. README.md): статический (или инстансный) метод
// в MoexIssDataServer вида  (JArray columns, JArray row, string classParam) -> Security,
// поиск по форме сигнатуры, имя не фиксировано.
//
// Аргументы: [путь к bin\Debug клона OsEngine] (по умолчанию — клон FixPipeline).
// Выход: строки PASS/FAIL/INFO, summary, result.json; exit 0 только при 0 FAIL.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bug0537Harness
{
    internal static class Program
    {
        private const string DefaultLib =
            @"C:\Users\Admin\Desktop\NMG\Agents\Deployments\FixPipeline\App\OsEngine\project\OsEngine\bin\Debug";

        private static Type _secType;
        private static Type _srvType;
        private static Type _jarrType;
        private static Type _jvalType;
        private static Type _jobjType;
        private static Type _stateEnum;
        private static Type _secTypeEnum;
        private static Assembly _asm;
        private static readonly List<string[]> CaseLog = new List<string[]>();
        private static int _green;
        private static int _red;

        private static int Main(string[] args)
        {
            string lib = args.Length > 0 ? args[0]
                : Environment.GetEnvironmentVariable("OSENGINE_LIB") ?? DefaultLib;

            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string name = new AssemblyName(e.Name).Name;
                string path = Path.Combine(lib, name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };

            string dllPath = Path.Combine(lib, "OsEngine.dll");
            Console.WriteLine("# OsEngine.dll: " + dllPath);
            if (!File.Exists(dllPath))
            {
                Console.WriteLine("FATAL: OsEngine.dll not found in lib dir");
                return 2;
            }
            Console.WriteLine("# sha256: " + Sha256(dllPath));

            Assembly asm = Assembly.LoadFrom(dllPath);
            _asm = asm;
            _secType = asm.GetType("OsEngine.Entity.Security", true);
            _srvType = asm.GetType("OsEngine.Market.Servers.MOEX.MoexDataServerRealization", true);
            AssemblyName ns = asm.GetReferencedAssemblies()
                .First(a => a.Name.StartsWith("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase));
            Assembly jsonAsm = Assembly.Load(ns.FullName);
            _jarrType = jsonAsm.GetType("Newtonsoft.Json.Linq.JArray", true);
            _jvalType = jsonAsm.GetType("Newtonsoft.Json.Linq.JValue", true);
            _jobjType = jsonAsm.GetType("Newtonsoft.Json.Linq.JObject", true);
            _stateEnum = asm.GetType("OsEngine.Entity.SecurityStateType", true);
            _secTypeEnum = asm.GetType("OsEngine.Entity.SecurityType", true);

            try
            {
                RunSectionA();
            }
            catch (Exception e)
            {
                Fail("A-EX", "Section A unexpected crash", e.ToString());
            }

            try
            {
                RunSectionB();
            }
            catch (Exception e)
            {
                Fail("B-EX", "Section B unexpected crash", e.ToString());
            }

            try
            {
                RunSectionU();
            }
            catch (Exception e)
            {
                Fail("U-EX", "Section U unexpected crash", e.ToString());
            }

            Console.WriteLine();
            Console.WriteLine("SUMMARY green=" + _green + " red=" + _red);
            WriteResult();
            return _red == 0 ? 0 : 1;
        }

        // ---------------- Section A: маппинг строки securities по columns ----------------

        private static void RunSectionA()
        {
            MethodInfo mapper = FindMapper();
            if (mapper == null)
            {
                // RED-база до фикса: маппер ещё не написан.
                string[] ids = { "A01", "A02", "A03", "A04", "A05", "A06", "A07", "A08", "A09",
                    "A10a", "A10b", "A12", "A13", "A14", "A15", "A16", "A17", "A18", "A19", "A20" };
                foreach (string id in ids)
                    Fail(id, "mapper seam (JArray columns, JArray row, string class) -> Security",
                        "not found in MoexIssDataServer (pre-fix baseline)");
                return;
            }
            Console.WriteLine("# mapper seam: " + Describe(mapper));

            string tqbr = "stock#shares#TQBR#Акции";
            string rfud = "currency#futures#RFUD#Фьючерсы";
            string rtsi = "stock#index#RTSI#Индексы";
            string sndx = "stock#index#SNDX#Индексы";
            string cets = "currency#currency#CETS#Валюта";

            // ---- A01: TQBR SBER, полный набор колонок (live-позиции из Разбор) ----
            string[] tqCols = TqbrColumns();
            object[] tqRow = TqbrRow("SBER", "Сбербанк", "A", "1", "2", "0.01", "CSFP");
            object sec = Invoke(mapper, tqCols, tqRow, tqbr);
            CheckBoard(sec, "A01", "TQBR SBER", "MOEX", "Activ", 0.01m, "2", 1m, 0.01m, "Stock");
            SoftNames(sec, "A01n", "SBER", "Сбербанк", tqbr, "Акции#TQBR");

            // ---- A02/A03/A04: SecurityType по SECTYPE@TQBR (F4) ----
            object[] etfRow = TqbrRow("EQTY", "ETF-фонд", "A", "1", "2", "0.01", "ETF");
            CheckType(Invoke(mapper, tqCols, etfRow, tqbr), "A02", "TQBR SECTYPE=ETF -> Fund", "Fund");
            object[] jRow = TqbrRow("JKR", "Дженжер", "A", "1", "2", "0.01", "J");
            CheckType(Invoke(mapper, tqCols, jRow, tqbr), "A03", "TQBR SECTYPE=J -> Fund (F4)", "Fund");

            string[] tqNoSecType = TqbrColumns();
            tqNoSecType[24] = "SOMECODE"; // колонки SECTYPE нет -> ветка Fund мертва, допустимо
            object[] plainRow = TqbrRow("LKOH", "Лукойл", "A", "1", "2", "0.01", "CSFP");
            plainRow[24] = null;
            CheckType(Invoke(mapper, tqNoSecType, plainRow, tqbr), "A04",
                "TQBR без колонки SECTYPE -> Stock (F4 dead-branch ok)", "Stock");

            // ---- A05: State по STATUS ----
            object[] closedRow = TqbrRow("SBRF", "Сбер (delist)", "F", "1", "2", "0.01", "CSFP");
            CheckState(Invoke(mapper, tqCols, closedRow, tqbr), "A05", "TQBR STATUS!=A -> Close", "Close");

            // ---- A06: Lot по LOTSIZE, null/0 -> 1 ----
            object[] lotRow = TqbrRow("GAZP", "Газпром", "A", "0", "2", "0.01", "CSFP");
            CheckLot(Invoke(mapper, tqCols, lotRow, tqbr), "A06", "TQBR LOTSIZE=0 -> Lot=1", "1");

            // ---- A07: DECIMALS=null -> по шагу (0.0001 -> 4) ----
            object[] decNull = TqbrRow("TMSKS", "ТМК", "A", "1", null, "0.0001", "CSFP");
            CheckDecimals(Invoke(mapper, tqCols, decNull, tqbr), "A07",
                "TQBR DECIMALS=null -> по step 0.0001", "4");

            // ---- A08: RFUD BRZ6 live-сэмпл + F2 ----
            string[] rfCols = RfudColumns();
            object[] brz = RfudRow("BRZ6", "Нефть Brent", "2", "0.01",
                DateTime.Now.Year + 1 + "-12-31", "BR", "10", "8.22281");
            object brzSec = Invoke(mapper, rfCols, brz, rfud);
            CheckBoard(brzSec, "A08", "RFUD BRZ6 (live-сэмпл, F2)", "MOEX", "Activ", 0.01m, "2", 1m, 8.22281m, "Futures");
            SoftNames(brzSec, "A08n", "BRZ6", "Нефть Brent", rfud, "Фьючерсы#RFUD");

            // ---- A09: F2 RFUD STEPPRICE=null -> PriceStepCost=MINSTEP ----
            object[] brNoStep = RfudRow("BRZ6", "Нефть Brent", "2", "0.01",
                DateTime.Now.Year + 1 + "-12-31", "BR", "10", null);
            CheckPriceStepCost(Invoke(mapper, rfCols, brNoStep, rfud), "A09",
                "RFUD STEPPRICE=null -> PriceStepCost=MINSTEP", "0.01");

            // ---- A10: RFUD State по LASTTRADEDATE (граница «>= сегодня») ----
            object[] expired = RfudRow("BRQ6", "Нефть Brent", "2", "0.01", "2020-01-01", "BR", "10", "8.22281");
            CheckState(Invoke(mapper, rfCols, expired, rfud), "A10a",
                "RFUD LASTTRADEDATE вчера -> Close", "Close");
            object[] today = RfudRow("BRZ6", "Нефть Brent", "2", "0.01",
                DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "BR", "10", "8.22281");
            CheckState(Invoke(mapper, rfCols, today, rfud), "A10b",
                "RFUD LASTTRADEDATE == сегодня -> Activ", "Activ");

            // ---- A12/A13: индексы RTSI/SNDX + F1 ----
            string[] idxCols = { "SECID", "BOARDID", "NAME", "LEVEL", "DECIMALS", "SHORTNAME",
                "PREVCLOSE", "UPDATED", "TTIME" }; // RTSI 9 колонок, MINSTEP нет
            object[] rtsiRow = new object[idxCols.Length];
            rtsiRow[0] = "RTSI"; rtsiRow[2] = "Индекс МосБиржи"; rtsiRow[4] = "3"; rtsiRow[5] = "ММВБ";
            object idxSec = Invoke(mapper, idxCols, rtsiRow, rtsi);
            CheckBoard(idxSec, "A12", "RTSI: step-фолбэк=1, F1 Decimals=0 при DECIMALS=3",
                "MOEX", "Activ", 1m, "0", 1m, 1m, "Index");
            CheckNameFullFrom(idxSec, "A12n", "Индекс МосБиржи", "A12");

            string[] sndxCols = (string[])idxCols.Clone();
            sndxCols[5] = "XXX"; // SHORTNAME нет вовсе — NameFull всё равно из NAME
            object[] sndxRow = new object[sndxCols.Length];
            sndxRow[0] = "SNDX"; sndxRow[2] = "Санкции.Индекс"; sndxRow[4] = "2";
            CheckBoard(Invoke(mapper, sndxCols, sndxRow, sndx), "A13",
                "SNDX: step-фолбэк=1, F1 Decimals=0, NameFull из NAME",
                "MOEX", "Activ", 1m, "0", 1m, 1m, "Index");

            // ---- A14/A15/A15b: CETS — фактические columns из дампа Смотрителя (tr246/runs#350,
            // лог сервера 22:32:08, DataCount 200). F5 закрыта: MINSTEP в CETS ЕСТЬ (0.0001/0.0025),
            // DECIMALS=4, LOTSIZE=100000/1000/1, STATUS="A", LASTTRADEDATE/STEPPRICE в CETS нет. ----
            string[] cetsCols = CetsColumns();
            object[] cetsRow0 = { "AEDRUBTODTOM", "CETS", "AED_TODTOM", 100000, "2026-11-19", 4, 1,
                "CURR", 0.0001, "2026-11-18", "AED_TODTOM - SWAP AED/RUB", null, "A", "AED",
                null, null, "RUB", "AED_TODTOM - SWAP AED/RUB", 1 };
            CheckBoard(Invoke(mapper, cetsCols, cetsRow0, cets), "A14",
                "CETS fact row0: step=MINSTEP 0.0001, DECIMALS=4 (F1 живая), Lot=100000, cost=step",
                "MOEX", "Activ", 0.0001m, "4", 100000m, 0.0001m, "CurrencyPair");

            object[] cetsRow1 = { "AEDRUB_SPT", "CETS", "AEDRUB_SPT", 1000, "2026-11-23", 4, 1,
                "CURR", 0.0025, "2026-11-18", "AED/RUB_SPT - AED/RUB", null, "A", "AED",
                null, null, "RUB", "AED/RUB_SPT - AED/RUB", 1 };
            CheckBoard(Invoke(mapper, cetsCols, cetsRow1, cets), "A15",
                "CETS fact row1: step=0.0025, dec=4, Lot=1000, F2-фолбэк cost=step (STEPPRICE нет)",
                "MOEX", "Activ", 0.0025m, "4", 1000m, 0.0025m, "CurrencyPair");

            object[] cetsRow2 = { "AEDRUB_TMS", "CETS", "AEDRUB_TMS", 1, "2026-11-20", 4, 1,
                "CURR", 0.0025, "2026-11-18", "AED/RUB_TMS - AED/RUB", null, "A", "AED",
                null, null, "RUB", "AED/RUB_TMS - SWAP AED/RUB", 1 };
            CheckBoard(Invoke(mapper, cetsCols, cetsRow2, cets), "A15b",
                "CETS fact row2: Lot=1 по LOTSIZE, step=0.0025, dec=4",
                "MOEX", "Activ", 0.0025m, "4", 1m, 0.0025m, "CurrencyPair");

            // ---- A21: CETS без колонок — null-safe дефолты (семейство A17/A18) ----
            CheckBoard(Invoke(mapper, new string[0], new object[] { "USDRUB_TOM" }, cets), "A21",
                "CETS columns=[] -> дефолты Activ/CurrencyPair без MINSTEP",
                "MOEX", "Activ", 1m, "0", 1m, 1m, "CurrencyPair");

            // ---- A22/A23 (tr270): Error 16 — DECIMALS ISS не согласован со шагом.
            // A22 CNYRUB_TMS verbatim из дампа: dec=6 при step=0.0001 (4 знака) -> кламп в 4.
            object[] cnyRow = (object[])cetsRow0.Clone();
            cnyRow[0] = "CNYRUB_TMS"; cnyRow[3] = 1; cnyRow[5] = 6; cnyRow[8] = 0.0001;
            object cnySec = Invoke(mapper, cetsCols, cnyRow, cets);
            CheckBoard(cnySec, "A22",
                "CETS CNYRUB_TMS dec=6 step=0.0001 -> Decimals=4 (кламп tr270)",
                "MOEX", "Activ", 0.0001m, "4", 1m, 0.0001m, "CurrencyPair");
            CheckV1Eq(cnySec, "A22v1", "A22");
            // A23 — тот же класс (39 бумаг: HKDRUB_TOM/PLDRUB_TOM/SLVRUB_*), шаг 0.0025:
            object[] cnyRow2 = (object[])cetsRow0.Clone();
            cnyRow2[0] = "HKDRUB_TOM"; cnyRow2[3] = 1; cnyRow2[5] = 6; cnyRow2[8] = 0.0025;
            object hkdSec = Invoke(mapper, cetsCols, cnyRow2, cets);
            CheckBoard(hkdSec, "A23",
                "CETS class-кейс dec=6 step=0.0025 -> Decimals=4 (кламп)",
                "MOEX", "Activ", 0.0025m, "4", 1m, 0.0025m, "CurrencyPair");
            // A24 — guard от наивного min-клампа: dec=2 < знаков(0.0001)=4. V1 требует
            // точного равенства -> Decimals=4 (колонка не применяется, значение сеттера).
            object[] cnyRow3 = (object[])cetsRow0.Clone();
            cnyRow3[0] = "SLVRUB_FX"; cnyRow3[3] = 1; cnyRow3[5] = 2; cnyRow3[8] = 0.0001;
            object lvSec = Invoke(mapper, cetsCols, cnyRow3, cets);
            CheckBoard(lvSec, "A24",
                "CETS dec=2 игнорируется: Decimals=4 (unconditional, ред.3b) — не min-кламп",
                "MOEX", "Activ", 0.0001m, "4", 1m, 0.0001m, "CurrencyPair");
            // V1 Error16-реплика на согласованных фактурах не регресснет:
            CheckV1Eq(Invoke(mapper, cetsCols, cetsRow0, cets), "A14v1", "A14");
            CheckV1Eq(lvSec, "A24v1", "A24");

            // ---- A16: независимость от позиций (переставленные анкетные колонки) ----
            int[] perm = { 0, 1, 2, 3, 9, 5, 14, 7, 8, 24, 10, 11, 12, 13, 4, 15, 16, 17, 18, 6,
                19, 20, 21, 22, 23, 25, 26 };
            string[] shuffled = new string[tqCols.Length];
            object[] shuffledRow = new object[tqCols.Length];
            for (int i = 0; i < perm.Length; i++)
            {
                shuffled[i] = tqCols[perm[i]];
                shuffledRow[i] = tqRow[perm[i]];
            }
            CheckBoard(Invoke(mapper, shuffled, shuffledRow, tqbr), "A16",
                "TQBR: те же значения при перестановке columns (позиции не используются)",
                "MOEX", "Activ", 0.01m, "2", 1m, 0.01m, "Stock");

            // ---- A17: только неизвестные колонки (ColIndex=-1 -> дефолты) ----
            string[] junkCols = { "FOO", "BAR" };
            object[] junkRow = { "XXX", "YYY" };
            object junkSec = Invoke(mapper, junkCols, junkRow, tqbr);
            CheckBoard(junkSec, "A17", "TQBR columns без анкетных полей -> дефолты без падения",
                "MOEX", "Close", 1m, "0", 1m, 1m, "Stock");

            // ---- A18: пустые columns ----
            object emptySec = Invoke(mapper, new string[0], new object[] { "SBER" }, tqbr);
            CheckBoard(emptySec, "A18", "TQBR columns=[] -> дефолты без падения",
                "MOEX", "Close", 1m, "0", 1m, 1m, "Stock");

            // ---- A19: SECID=null — без падения (Security или null, по ТЗ null-safe) ----
            object[] nullIdRow = TqbrRow(null, null, "A", "1", "2", "0.01", "CSFP");
            try
            {
                object nullSec = Invoke(mapper, tqCols, nullIdRow, tqbr);
                Pass("A19", "TQBR SECID=null -> без исключения", nullSec == null ? "null" : "Security");
            }
            catch (TargetInvocationException e)
            {
                Fail("A19", "TQBR SECID=null -> без исключения", e.InnerException.ToString());
            }

            // ---- A20: RFUD без анкетных колонок -> дефолты (step=1, Close, Futures) ----
            object rfJunk = Invoke(mapper, junkCols, junkRow, rfud);
            CheckBoard(rfJunk, "A20", "RFUD columns без анкетных полей -> дефолты без падения",
                "MOEX", "Close", 1m, "0", 1m, 1m, "Futures");
        }

        // ---------------- Section B: F3 — синтетика истёкших (CreateFuturesSection) ----------------

        private static void RunSectionB()
        {
            MethodInfo create = _srvType.GetMethod("CreateFuturesSection",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (create == null)
            {
                Fail("B0", "CreateFuturesSection", "method not found");
                return;
            }
            object inst = RuntimeHelpers.GetUninitializedObject(_srvType);

            Type listType = typeof(List<>).MakeGenericType(_secType);
            IList input = (IList)Activator.CreateInstance(listType);

            // allFutures[0] = SiZ6 с чужим классом-суффиксом — базовый «первый» фьючерс
            input.Add(MakeSec("SiZ6", "SiZ6#C#D#RFUD#Y", "Фьючерсы#RFUD",
                "0.1", "0.1", "1", "1", "Activ", "Futures", "MOEX"));
            // первое вхождение BR — шаблон по F3: step=0.5 (BRZ6 позже — НЕ шаблон)
            input.Add(MakeSec("BRQ6", "BRQ6#A#B#RFUD#X", "Фьючерсы#RFUD",
                "0.5", "5.0", "1", "1", "Activ", "Futures", "MOEX"));
            input.Add(MakeSec("BRZ6", "BRZ6#A#B#RFUD#X", "Фьючерсы#RFUD",
                "0.01", "8.22281", "2", "1", "Activ", "Futures", "MOEX"));

            IList result;
            try
            {
                object ret = create.Invoke(inst, new object[] { input });
                result = (IList)(ret ?? input);
            }
            catch (TargetInvocationException e)
            {
                Fail("B0", "CreateFuturesSection invoke", e.InnerException.ToString());
                return;
            }

            List<object> synth = result.Cast<object>()
                .Where(s => Str(s, "NameClass") == "Фьючерсы истёкшие#RFUD").ToList();
            List<object> brSynth = synth.Where(s => Str(s, "Name").StartsWith("BR")).ToList();
            List<object> siSynth = synth.Where(s => Str(s, "Name").StartsWith("Si")).ToList();

            if (synth.Count == 0 || brSynth.Count == 0 || siSynth.Count == 0)
            {
                Fail("B1", "синтетика создана (BR+Si, 8 лет)",
                    $"synth={synth.Count} BR={brSynth.Count} Si={siSynth.Count}");
                return;
            }
            Pass("B1", "синтетика создана (sanity, независима от фикса)",
                $"synth={synth.Count}");

            object badState = synth.FirstOrDefault(s => Str(s, "State") != "Close");
            if (badState == null) Pass("B2", "F3: State=Close синтетике безусловно", "all Close");
            else Fail("B2", "F3: State=Close синтетике безусловно",
                $"{Str(badState, "Name")}: State={Str(badState, "State")}");

            CheckSynthTemplate(brSynth, "B3", "BR (месячные, шаблон BRQ6: first-occurrence)",
                "0.5", "5.0", "1", "Futures", "MOEX");
            CheckSynthTemplate(siSynth, "B4", "Si (квартальные, шаблон SiZ6)",
                "0.1", "0.1", "1", "Futures", "MOEX");

            bool brIdOk = brSynth.All(s => Str(s, "NameId").Contains("A#B#RFUD"));
            bool siIdOk = siSynth.All(s => Str(s, "NameId").Contains("C#D#RFUD"));
            if (brIdOk && siIdOk)
                Pass("B5", "F3: idEnding из NameId шаблона базы", "ok");
            else
                Fail("B5", "F3: idEnding из NameId шаблона базы",
                    $"BR->'{Str(brSynth[0], "NameId")}' Si->'{Str(siSynth[0], "NameId")}'");

            object brzAfter = result.Cast<object>().First(s => Str(s, "Name") == "BRZ6");
            CheckPriceStep(brzAfter, "B6", "шаблон-источник BRZ6 не изуродован", "0.01");
        }

        // ---------------- helpers ----------------

        private static MethodInfo FindMapper()
        {
            return _srvType
                .GetMethods(BindingFlags.Static | BindingFlags.Instance |
                            BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.DeclaringType == _srvType)
                .Where(m => m.ReturnType == _secType)
                .Where(m =>
                {
                    ParameterInfo[] ps = m.GetParameters();
                    return ps.Length == 3
                        && ps[0].ParameterType.IsAssignableFrom(_jarrType)
                        && ps[1].ParameterType.IsAssignableFrom(_jarrType)
                        && ps[2].ParameterType == typeof(string);
                })
                .OrderBy(m => m.Name.IndexOf("Map", StringComparison.OrdinalIgnoreCase) < 0 ? 1 : 0)
                .FirstOrDefault();
        }

        private static object Invoke(MethodInfo mapper, string[] columns, object[] row, string classParam)
        {
            object target = mapper.IsStatic ? null : RuntimeHelpers.GetUninitializedObject(_srvType);
            return mapper.Invoke(target, new object[] { ToArray(columns), ToArray(row), classParam });
        }

        // JArray из raw-значений: каждое значение через JValue(object) (null -> JTokenType.Null)
        private static object ToArray(IEnumerable items)
        {
            object arr = Activator.CreateInstance(_jarrType, Type.EmptyTypes);
            MethodInfo add = _jarrType.GetMethods()
                .First(m => m.Name == "Add" && m.GetParameters().Length == 1);
            ConstructorInfo valCtor = _jvalType.GetConstructor(new[] { typeof(object) });
            foreach (object item in items)
            {
                object token = valCtor.Invoke(new object[] { item });
                if (add.GetParameters()[0].ParameterType.IsInstanceOfType(token))
                    add.Invoke(arr, new[] { token });
                else
                    add.Invoke(arr, new[] { item });
            }
            return arr;
        }

        private static object MakeSec(string name, string nameId, string nameClass,
            string step, string cost, string dec, string lot, string state, string secType, string exchange)
        {
            object sec = Activator.CreateInstance(_secType);
            Set(sec, "Name", name);
            Set(sec, "NameId", nameId);
            Set(sec, "NameClass", nameClass);
            Set(sec, "NameFull", name);
            Set(sec, "Exchange", exchange);
            _secType.GetProperty("PriceStep").SetValue(sec, decimal.Parse(step, CultureInfo.InvariantCulture));
            _secType.GetProperty("Decimals").SetValue(sec, int.Parse(dec, CultureInfo.InvariantCulture));
            Set(sec, "PriceStepCost", decimal.Parse(cost, CultureInfo.InvariantCulture));
            Set(sec, "Lot", decimal.Parse(lot, CultureInfo.InvariantCulture));
            Set(sec, "VolumeStep", 1m);
            Set(sec, "State", Enum.Parse(_stateEnum, state));
            Set(sec, "SecurityType", Enum.Parse(_secTypeEnum, secType));
            return sec;
        }

        private static void CheckSynthTemplate(List<object> synth, string id, string desc,
            string step, string cost, string dec, string secType, string exchange)
        {
            object bad = synth.FirstOrDefault(s =>
                Str(s, "PriceStep") != step || Str(s, "PriceStepCost") != cost ||
                Str(s, "Decimals") != dec || Str(s, "SecurityType") != secType ||
                Str(s, "Exchange") != exchange || Str(s, "Lot") != "1" || Str(s, "VolumeStep") != "1");
            if (bad == null) { Pass(id, desc, "все синтетики = шаблон базы"); return; }
            Fail(id, desc,
                $"{Str(bad, "Name")}: step={Str(bad, "PriceStep")} cost={Str(bad, "PriceStepCost")} " +
                $"dec={Str(bad, "Decimals")} type={Str(bad, "SecurityType")} ex={Str(bad, "Exchange") ?? "null"} " +
                $"lot={Str(bad, "Lot")} vol={Str(bad, "VolumeStep")}");
        }

        private static void CheckBoard(object sec, string id, string desc,
            string exchange, string state, decimal step, string dec, decimal lot, decimal cost, string secType)
        {
            if (sec == null) { Fail(id, desc, "mapper вернул null"); return; }
            var errs = new List<string>();
            Cmp(errs, "Exchange", Str(sec, "Exchange") ?? "null", exchange);
            Cmp(errs, "State", Str(sec, "State"), state);
            Cmp(errs, "PriceStep", Str(sec, "PriceStep"), step.ToString(CultureInfo.InvariantCulture));
            Cmp(errs, "Decimals", Str(sec, "Decimals"), dec);
            Cmp(errs, "Lot", Str(sec, "Lot"), lot.ToString(CultureInfo.InvariantCulture));
            Cmp(errs, "PriceStepCost", Str(sec, "PriceStepCost"), cost.ToString(CultureInfo.InvariantCulture));
            Cmp(errs, "SecurityType", Str(sec, "SecurityType"), secType);
            Cmp(errs, "VolumeStep", Str(sec, "VolumeStep"), "1");
            Cmp(errs, "MinTradeAmount", Str(sec, "MinTradeAmount"), "1");
            if (errs.Count == 0) { Pass(id, desc, "ok"); return; }
            Fail(id, desc, string.Join("; ", errs));
        }

        private static void Cmp(List<string> errs, string field, string actual, string expected)
        {
            if (actual != expected) errs.Add($"{field}={actual} (ожидался {expected})");
        }

        private static void CheckType(object sec, string id, string desc, string expected)
        { CheckField(sec, id, desc, expected, s => Str(s, "SecurityType")); }

        private static void CheckState(object sec, string id, string desc, string expected)
        { CheckField(sec, id, desc, expected, s => Str(s, "State")); }

        private static void CheckLot(object sec, string id, string desc, string expected)
        { CheckField(sec, id, desc, expected, s => Str(s, "Lot")); }

        private static void CheckPriceStep(object sec, string id, string desc, string expected)
        { CheckField(sec, id, desc, expected, s => Str(s, "PriceStep")); }

        private static void CheckPriceStepCost(object sec, string id, string desc, string expected)
        { CheckField(sec, id, desc, expected, s => Str(s, "PriceStepCost")); }

        private static void CheckDecimals(object sec, string id, string desc, string expected)
        { CheckField(sec, id, desc, expected, s => Str(s, "Decimals")); }

        private static void CheckField(object sec, string id, string desc, string expected,
            Func<object, string> get)
        {
            if (sec == null) { Fail(id, desc, "mapper вернул null"); return; }
            string actual = get(sec);
            if (actual == expected) Pass(id, desc, actual);
            else Fail(id, desc, $"{actual} (ожидался {expected})");
        }

        // Name* уже реализованы в GetSecurities; на уровне маппера — soft-проверка:
        // если маппер их заполняет — сверяем, если null — INFO (остались в GetSecurities).
        private static void SoftNames(object sec, string id, string name, string nameFull,
            string classParam, string expectClass)
        {
            if (sec == null) return;
            if (Str(sec, "Name") == null)
            {
                Info(id, "Name* заполняет GetSecurities (маппер их не трогает)");
                return;
            }
            var errs = new List<string>();
            Cmp(errs, "Name", Str(sec, "Name"), name);
            if (Str(sec, "NameFull") != null) Cmp(errs, "NameFull", Str(sec, "NameFull"), nameFull);
            if (Str(sec, "NameId") != null) Cmp(errs, "NameId", Str(sec, "NameId"), name + "#" + classParam);
            if (Str(sec, "NameClass") != null) Cmp(errs, "NameClass", Str(sec, "NameClass"), expectClass);
            if (errs.Count == 0) Pass(id, "Name/NameId/NameClass/NameFull (soft)", "ok");
            else Fail(id, "Name/NameId/NameClass/NameFull (soft)", string.Join("; ", errs));
        }

        // NameFull индекса — из колонки NAME, даже если маппер возвращает NameFull
        private static void CheckNameFullFrom(object sec, string id, string expect, string parent)
        {
            if (sec == null) return;
            string actual = Str(sec, "NameFull");
            if (actual == null) return; // NameFull не на уровне маппера
            if (actual == expect) Pass(id, "индекс: NameFull из NAME (soft)", actual);
            else Fail(id, "индекс: NameFull из NAME (soft)", actual);
        }

        // ---------------- fixtures ----------------

        // TQBR 27 колонок: заякоренные позиции из Разбор (SECID@0, BOARDID@1, SHORTNAME@2,
        // LOTSIZE@4, STATUS@6, DECIMALS@8, MINSTEP@14, SECTYPE@24); филлеры — только для размера.
        private static string[] TqbrColumns()
        {
            string[] cols = new string[27];
            for (int i = 0; i < cols.Length; i++) cols[i] = "X" + i.ToString("00");
            cols[0] = "SECID"; cols[1] = "BOARDID"; cols[2] = "SHORTNAME";
            cols[4] = "LOTSIZE"; cols[6] = "STATUS"; cols[8] = "DECIMALS";
            cols[14] = "MINSTEP"; cols[24] = "SECTYPE";
            return cols;
        }

        private static object[] TqbrRow(string secid, string shortName, string status,
            string lotSize, string decimals, string minStep, string secType)
        {
            object[] row = new object[27];
            row[0] = secid; row[1] = "TQBR"; row[2] = shortName;
            row[4] = lotSize; row[6] = status; row[8] = decimals;
            row[14] = minStep; row[24] = secType;
            return row;
        }

        // RFUD 28 колонок: SECID@0, SHORTNAME@2, DECIMALS@5, MINSTEP@6, LASTTRADEDATE@7,
        // SECTYPE@9, LOTVOLUME@13, STEPPRICE@17 (live-сэмпл BRZ6).
        private static string[] RfudColumns()
        {
            string[] cols = new string[28];
            for (int i = 0; i < cols.Length; i++) cols[i] = "Y" + i.ToString("00");
            cols[0] = "SECID"; cols[2] = "SHORTNAME"; cols[5] = "DECIMALS";
            cols[6] = "MINSTEP"; cols[7] = "LASTTRADEDATE"; cols[9] = "SECTYPE";
            cols[13] = "LOTVOLUME"; cols[17] = "STEPPRICE";
            return cols;
        }

        private static object[] RfudRow(string secid, string shortName, string decimals,
            string minStep, string lastTradeDate, string secType, string lotVolume, string stepPrice)
        {
            object[] row = new object[28];
            row[0] = secid; row[2] = shortName; row[5] = decimals;
            row[6] = minStep; row[7] = lastTradeDate; row[9] = secType;
            row[13] = lotVolume; row[17] = stepPrice;
            return row;
        }

        // ---------------- Section U: дельта ОдТС plan-20261121-014 ----------------
        // U-01=R-08 (безусловный Decimals при шаге из колонки), U-03 кросс-пин
        // StepFractionDigits vs БОЕВОЙ IsCompairDecimalsAndStep, U-13 сырой JSON
        // (Date-токены, экспонента MINSTEP), U-14 State по Date-токенам,
        // U-15 переприём NumberStyles, U-16 round-trip GetSaveStr/LoadFromString,
        // S-01 боевой Var_1_Securities.Check на корпусе анкет.

        private static void RunSectionU()
        {
            MethodInfo mapper = FindMapper();
            if (mapper == null)
            {
                foreach (string id in new[] { "U-01", "U-03", "U-13", "U-14a", "U-14b", "U-14c",
                    "U-15a", "U-15b", "U-15c", "U-16", "S-01a", "S-01b", "S-01c" })
                    Fail(id, "Section U: mapper seam not found (pre-fix baseline)", "-");
                return;
            }

            // ---- U-01 (R-08): MINSTEP="1.5" БЕЗ колонки DECIMALS. Сеттер на шаге >=1
            // даёт Decimals=0, а оракул V1 требует 1 -> до unconditional-правки RED.
            string[] uCols = { "SECID", "BOARDID", "MINSTEP" };
            object[] uRow = { "TSTBRD", "CETS", "1.5" };
            object uSec = Invoke(mapper, uCols, uRow, cetsClass);
            CheckBoard(uSec, "U-01",
                "R-08: MINSTEP=1.5 без DECIMALS -> Decimals=1 (unconditional)",
                "MOEX", "Activ", 1.5m, "1", 1m, 1.5m, "CurrencyPair");
            CheckV1Eq(uSec, "U-01v1", "U-01");

            // ---- U-03: кросс-пин приватного StepFractionDigits против БОЕВОГО
            // Var_1_Securities.IsCompairDecimalsAndStep (не реплики) на сетке шагов.
            CrossPinOracle();

            // ---- U-13: сырой JSON через JObject.Parse (DateParseHandling по умолчанию:
            // ISO-строки становятся DateTime-токенами), экспонента 1E-05 в MINSTEP
            // строкой и числом (JValue(double).ToString -> "1E-05").
            string rawJson =
                "{\"table\":{\"securities\":{"
                + "\"columns\":[\"SECID\",\"BOARDID\",\"SHORTNAME\",\"LOTSIZE\",\"SETTLEDATE\",\"DECIMALS\",\"MINSTEP\",\"PREVDATE\",\"STATUS\"],"
                + "\"data\":[[\"TESTRUB_TOM\",\"CETS\",\"TEST/RUB\",1000,\"2026-11-20\",5,\"1E-05\",\"2026-11-19\",\"A\"],"
                + "[\"TESTNUM_TOM\",\"CETS\",\"TESTNUM/RUB\",1000,\"2026-11-20\",5,1e-05,\"2026-11-19\",\"A\"]]}}}";
            object root = JParse(rawJson);
            object secBlock = JGet(root, "table") != null ? JGet(JGet(root, "table"), "securities") : null;
            object rawCols = JGet(secBlock, "columns");
            object data = JGet(secBlock, "data");
            object rawRow0 = JIndex(data, 0);
            object rawRow1 = JIndex(data, 1);
            object u13a = InvokeArr(mapper, rawCols, rawRow0, cetsClass);
            CheckBoard(u13a, "U-13a",
                "raw JSON: MINSTEP \"1E-05\" -> step=0.00001, Decimals=5 (экспонента принята)",
                "MOEX", "Activ", 0.00001m, "5", 1000m, 0.00001m, "CurrencyPair");
            CheckV1Eq(u13a, "U-13av1", "U-13a");
            object u13b = InvokeArr(mapper, rawCols, rawRow1, cetsClass);
            CheckBoard(u13b, "U-13b",
                "raw JSON: MINSTEP числом 1e-05 (JValue double -> \"1E-05\") -> step=0.00001",
                "MOEX", "Activ", 0.00001m, "5", 1000m, 0.00001m, "CurrencyPair");

            // ---- U-14: State RFUD по LASTTRADEDATE сквозь Date-токены (default parse)
            // и DD.MM-амбигуити; таймзоны: локальная полночь, сравнение с DateTime.Today.
            string rfudCols = "{\"table\":{\"securities\":{\"columns\":[\"SECID\",\"BOARDID\",\"DECIMALS\",\"MINSTEP\",\"LASTTRADEDATE\",\"SECTYPE\",\"STEPPRICE\"],";
            object futureSec = MapRaw(rfudCols + "\"data\":[[\"BRZ7\",\"RFUD\",2,0.01,\"2027-05-31\",\"BR\",8.5]]}}}");
            CheckState(futureSec, "U-14a", "RFUD Date-токен 2027-05-31 -> Activ (без сдвига зоной)", "Activ");
            object pastSec = MapRaw(rfudCols + "\"data\":[[\"BRQ5\",\"RFUD\",2,0.01,\"2020-06-15\",\"BR\",8.5]]}}}");
            CheckState(pastSec, "U-14b", "RFUD Date-токен 2020-06-15 -> Close", "Close");
            object ddSec = MapRaw(rfudCols + "\"data\":[[\"BRXX\",\"RFUD\",2,0.01,\"31.12.2027\",\"BR\",8.5]]}}}");
            CheckState(ddSec, "U-14c",
                "RFUD DD.MM-амбигуити \"31.12.2027\" -> Close (консервативно; ISS отдаёт ISO — факт дампа)", "Close");

            // ---- U-15: переприём NumberStyles.Float (whitespace/.5/групповой разделитель)
            object[] wsRow = { "WSSEC", "CETS", " 0.0025 " };
            CheckBoard(Invoke(mapper, uCols, wsRow, cetsClass), "U-15a",
                "MINSTEP \" 0.0025 \" (whitespace) -> принят, dec=4",
                "MOEX", "Activ", 0.0025m, "4", 1m, 0.0025m, "CurrencyPair");
            object[] dotRow = { "DOTSEC", "CETS", ".5" };
            CheckBoard(Invoke(mapper, uCols, dotRow, cetsClass), "U-15b",
                "MINSTEP \".5\" -> принят (Float), dec=1",
                "MOEX", "Activ", 0.5m, "1", 1m, 0.5m, "CurrencyPair");
            object[] grpRow = { "GRPSEC", "CETS", "1,000.5" };
            CheckBoard(Invoke(mapper, uCols, grpRow, cetsClass), "U-15c",
                "MINSTEP \"1,000.5\" (групповой разделитель) -> reject -> фолбэк step=1",
                "MOEX", "Activ", 1m, "0", 1m, 1m, "CurrencyPair");

            // ---- U-16: round-trip GetSaveStr -> LoadFromString на анкетной SBER-фичуре
            RoundTrip(mapper);

            // ---- S-01: боевой Var_1_Securities.Check на корпусе анкет
            S01Live(mapper);
        }

        private static readonly string cetsClass = "currency#currency#CETS#Валюта";
        private static readonly string rfudClass = "currency#futures#RFUD#Фьючерсы";

        // сырой JSON -> секьюрити RFUD из первой строки data
        private static object MapRaw(string json)
        {
            object root = JParse(json);
            object secBlock = JGet(JGet(root, "table"), "securities");
            return InvokeArr(FindMapper(), JGet(secBlock, "columns"), JIndex(JGet(secBlock, "data"), 0), rfudClass);
        }

        private static object JParse(string json)
        {
            return _jobjType.GetMethod("Parse", new[] { typeof(string) }).Invoke(null, new object[] { json });
        }

        private static object JGet(object token, string key)
        {
            return token.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, token, new object[] { key });
        }

        private static object JIndex(object token, int i)
        {
            return token.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, token, new object[] { i });
        }

        private static object InvokeArr(MethodInfo mapper, object colsArr, object rowArr, string classParam)
        {
            object target = mapper.IsStatic ? null : RuntimeHelpers.GetUninitializedObject(_srvType);
            return mapper.Invoke(target, new object[] { colsArr, rowArr, classParam });
        }

        private static void CrossPinOracle()
        {
            Type var1 = FindType("Var_1_Securities");
            MethodInfo oracle = var1.GetMethod("IsCompairDecimalsAndStep",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo helper = _srvType.GetMethod("StepFractionDigits",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (oracle == null || helper == null)
            {
                Fail("U-03", "кросс-пин: не найдены IsCompairDecimalsAndStep/StepFractionDigits", "-");
                return;
            }
            object tester = RuntimeHelpers.GetUninitializedObject(var1);
            decimal[] grid = { 0.0001m, 0.0025m, 0.01m, 0.5m, 1.5m, 1m, 10m, 0.0100m,
                8.22281m, 0.00001m, 100.001m };
            int bad = 0;
            string detail = "";
            foreach (decimal step in grid)
            {
                int helperVal = (int)helper.Invoke(null, new object[] { step });
                // оракул: единственное dec, при котором IsCompair true (для целого шага — все dec)
                bool anyFraction = false;
                int oracleVal = -1;
                bool allTrue = true;
                for (int n = 0; n <= 20; n++)
                {
                    bool ok = (bool)oracle.Invoke(tester, new object[] { n, step });
                    if (ok) { anyFraction = true; if (oracleVal < 0) oracleVal = n; }
                    else allTrue = false;
                }
                int expect = !anyFraction ? 0 : oracleVal; // целой дроби у оракула нет -> helper 0
                if (allTrue) expect = 0;
                if (helperVal != expect)
                {
                    bad++;
                    detail += " step=" + step.ToString(CultureInfo.InvariantCulture)
                        + " helper=" + helperVal + " oracle=" + expect + ";";
                }
            }
            if (bad == 0) Pass("U-03", "кросс-пин StepFractionDigits vs боевой IsCompairDecimalsAndStep",
                grid.Length + " шагов совпали");
            else Fail("U-03", "кросс-пин StepFractionDigits vs боевой IsCompairDecimalsAndStep", detail);
        }

        private static void RoundTrip(MethodInfo mapper)
        {
            object sec = Invoke(mapper, TqbrColumns(), TqbrRow("RTBR", "Рандом-трип", "A", "1", "2", "0.01", "CSFP"),
                "stock#shares#TQBR#Акции");
            // GetSecurities заполняет Name* вне маппера; Exchange в save-формат не входит
            // (не персистится by design) — сравниваем только персистимые поля
            Set(sec, "Name", "RTBR");
            Set(sec, "NameId", "RTBR");
            Set(sec, "NameClass", "Акции#TQBR");
            Set(sec, "NameFull", "RTBR");
            string save = (string)_secType.GetMethod("GetSaveStr").Invoke(sec, null);
            object back = Activator.CreateInstance(_secType);
            _secType.GetMethod("LoadFromString").Invoke(back, new object[] { save });
            string[] props = { "PriceStep", "Decimals", "Lot", "PriceStepCost", "VolumeStep",
                "MinTradeAmount", "State", "SecurityType", "Name", "NameId", "NameClass" };
            string diff = "";
            foreach (string p in props)
            {
                if (Str(sec, p) != Str(back, p)) diff += " " + p + ": " + Str(sec, p) + "!=" + Str(back, p);
            }
            if (diff.Length == 0) Pass("U-16", "round-trip GetSaveStr/LoadFromString анкеты SBER-профиля", "10 полей");
            else Fail("U-16", "round-trip GetSaveStr/LoadFromString", diff);
        }

        private static void S01Live(MethodInfo mapper)
        {
            Type var1 = FindType("Var_1_Securities");
            MethodInfo check = var1.GetMethod("Check", BindingFlags.Instance | BindingFlags.NonPublic);
            Type serverTypeEnum = FindType("ServerType");
            object serverType = Enum.Parse(serverTypeEnum,
                    Enum.GetNames(serverTypeEnum).Contains("MoexDataServer") ? "MoexDataServer"
                        : Enum.GetNames(serverTypeEnum)[0]);
            object tester = RuntimeHelpers.GetUninitializedObject(var1);
            FieldInfo errorsField = null;
            for (Type t = var1; t != null && errorsField == null; t = t.BaseType)
                errorsField = t.GetField("_errors",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (check == null || errorsField == null)
            {
                Fail("S-01a", "S-01: не найдены Check/_errors (боевой V1 недоступен)", "-");
                return;
            }

            // S-01a: краснота базы на боевом Check. Сырой Security (дефолты полей:
            // Exchange null, State UnKnown, type None, step/cost/lot 0, _decimals=-1,
            // VolumeStep 0, MinTradeAmount 1) — только Name* дозаполняем (у setter-а
            // Decimals guard >=0, а setter PriceStep при 0 ставит _decimals=0 —
            // трогать их нельзя, иначе Error 14 не воспроизвести).
            errorsField.SetValue(tester, new List<string>());
            object blank = Activator.CreateInstance(_secType);
            Set(blank, "Name", "TST");
            Set(blank, "NameId", "TST");
            Set(blank, "NameClass", "TST#X");
            Set(blank, "NameFull", "TST");
            check.Invoke(tester, new object[] { blank, serverType });
            List<string> baseErrors = (List<string>)errorsField.GetValue(tester);
            SortedSet<int> codes = ErrorCodes(baseErrors);
            bool redOk = SetEquals(codes, new SortedSet<int> { 6, 9, 10, 11, 12, 13, 14, 17 });
            if (redOk) Pass("S-01a", "краснота базы на боевом Var_1 Check = {6,9,10,11,12,13,14,17}",
                baseErrors.Count + " сообщений");
            else Fail("S-01a", "краснота базы на боевом Var_1 Check", CodesStr(codes));

            // S-01b: positive control — dec=6/step=0.0001 МИМО маппера -> Error 16
            errorsField.SetValue(tester, new List<string>());
            object ctrl = MakeSec("PCT", "PCT", "PCT#X", "0.0001", "0.0001", "6", "1", "Activ", "CurrencyPair", "MOEX");
            check.Invoke(tester, new object[] { ctrl, serverType });
            bool has16 = ErrorCodes((List<string>)errorsField.GetValue(tester)).Contains(16);
            if (has16) Pass("S-01b", "positive control: боевой V1 бьёт Error 16 на dec=6/step=0.0001 (мимо маппера)", "-");
            else Fail("S-01b", "positive control Error 16 не сработал", CodesStr(ErrorCodes((List<string>)errorsField.GetValue(tester))));

            // S-01c: корпус маппированных анкет через боевой Check -> чисто
            string[] corpusDesc = { "TQBR SBER", "CETS row0", "RFUD BRZ6", "CNYRUB_TMS(clamp)", "RTSI idx", "U-01 step1.5" };
            string corpusErr = "";
            int corpusIdx = 0;
            foreach (object[] item in new object[][] {
                new object[] { TqbrColumns(), TqbrRow("SBER", "Сбербанк", "A", "1", "2", "0.01", "CSFP"), "stock#shares#TQBR#Акции" },
                new object[] { CetsColumns(), new object[] { "AEDRUBTODTOM", "CETS", "AED_TODTOM", 100000, "2026-11-19", 4, 1,
                    "CURR", 0.0001, "2026-11-18", "AED_TODTOM", null, "A", "AED", null, null, "RUB", "AED_TODTOM", 1 }, cetsClass },
                new object[] { RfudColumns(), RfudRow("BRZ6", "Нефть Brent", "2", "0.01",
                    DateTime.Now.Year + 1 + "-12-31", "BR", "10", "8.22281"), rfudClass },
                new object[] { CetsColumns(), new object[] { "CNYRUB_TMS", "CETS", "CNY/RUB_TMS", 1, "2026-11-19", 6, 1,
                    "CURR", 0.0001, "2026-11-18", "CNY/RUB", null, "A", "CNY", null, null, "RUB", "CNY/RUB", 1 }, cetsClass },
                new object[] { new string[] { "SECID", "BOARDID", "NAME", "LEVEL", "DECIMALS", "SHORTNAME", "PREVCLOSE", "UPDATED", "TTIME" },
                    new object[] { "RTSI", "IDX", "Индекс РТС", null, "3", "RTSI", null, null, null },
                    "stock#index#RTSI#Индексы" },
                new object[] { new string[] { "SECID", "BOARDID", "MINSTEP" }, new object[] { "TSTBRD", "CETS", "1.5" }, cetsClass },
            })
            {
                object mSec = Invoke(mapper, (string[])item[0], (object[])item[1], (string)item[2]);
                // GetSecurities заполняет Name*-поля вне маппера — дозаполняем как он
                Set(mSec, "Name", Str(mSec, "NameId") ?? "X");
                if (string.IsNullOrEmpty(Str(mSec, "NameId"))) Set(mSec, "NameId", "X");
                if (string.IsNullOrEmpty(Str(mSec, "NameClass"))) Set(mSec, "NameClass", "X#X");
                if (string.IsNullOrEmpty(Str(mSec, "NameFull"))) Set(mSec, "NameFull", Str(mSec, "Name"));
                errorsField.SetValue(tester, new List<string>());
                check.Invoke(tester, new object[] { mSec, serverType });
                SortedSet<int> c = ErrorCodes((List<string>)errorsField.GetValue(tester));
                if (c.Count != 0) corpusErr += " [" + corpusDesc[corpusIdx] + "]" + CodesStr(c);
                corpusIdx++;
            }
            if (corpusErr.Length == 0) Pass("S-01c", "боевой Var_1 Check на 6 маппированных анкетах — Errors пусто", "-");
            else Fail("S-01c", "боевой Var_1 Check вернул коды на корпусе", corpusErr);
        }

        private static SortedSet<int> ErrorCodes(List<string> errors)
        {
            SortedSet<int> codes = new SortedSet<int>();
            foreach (string e in errors)
            {
                System.Text.RegularExpressions.Match m =
                    System.Text.RegularExpressions.Regex.Match(e, @"Error\s+(\d+)");
                if (m.Success) codes.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            }
            return codes;
        }

        private static string CodesStr(SortedSet<int> codes)
        {
            return "{" + string.Join(",", codes.Select(c => c.ToString(CultureInfo.InvariantCulture))) + "}";
        }

        private static bool SetEquals(SortedSet<int> a, SortedSet<int> b)
        {
            if (a.Count != b.Count) return false;
            using (IEnumerator<int> ea = a.GetEnumerator(), eb = b.GetEnumerator())
            {
                while (ea.MoveNext() && eb.MoveNext()) if (ea.Current != eb.Current) return false;
            }
            return true;
        }

        private static Type FindType(string name)
        {
            Type[] types;
            try { types = _asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            return types.First(t => t.Name == name);
        }

        // CETS 19 колонок — фактический порядок из дампа runs#350 (tr246, F5 закрыта).
        private static string[] CetsColumns()
        {
            return new[] { "SECID", "BOARDID", "SHORTNAME", "LOTSIZE", "SETTLEDATE", "DECIMALS",
                "FACEVALUE", "MARKETCODE", "MINSTEP", "PREVDATE", "SECNAME", "REMARKS", "STATUS",
                "FACEUNIT", "PREVPRICE", "PREVWAPRICE", "CURRENCYID", "LATNAME", "LOTDIVIDER" };
        }

        // Реплика проверки V1 Error 16 (Var_1_Securities.IsCompairDecimalsAndStep):
        // точное равенство Decimals и числа дробных знаков PriceStep без хвостовых
        // нулей. Срабатывание в tr270 (Error 16 на CNYRUB_TMS dec=6 step=0.0001).
        private static void CheckV1Eq(object sec, string id, string srcId)
        {
            decimal step = decimal.Parse(Str(sec, "PriceStep"), CultureInfo.InvariantCulture);
            int dec = int.Parse(Str(sec, "Decimals"), CultureInfo.InvariantCulture);
            string s = step.ToString(CultureInfo.InvariantCulture).TrimEnd('0');
            string[] parts = s.Split('.');
            bool ok = parts.Length < 2 || dec == parts[1].Length;
            if (ok) Pass(id, "реплика V1 Error16 для " + srcId, "dec=" + dec + " step=" + s);
            else Fail(id, "реплика V1 Error16 для " + srcId, "dec=" + dec + " step=" + s);
        }

        // ---------------- infra ----------------

        private static string Str(object obj, string field)
        {
            Type t = obj.GetType();
            MemberInfo m = (MemberInfo)t.GetField(field) ?? (MemberInfo)t.GetProperty(field);
            if (m == null) throw new InvalidOperationException("no member " + field);
            object value = m is FieldInfo f ? f.GetValue(obj)
                : m is PropertyInfo p ? p.GetValue(obj) : null;
            // инвариантный формат: сверка «0.01» не должна зависеть от региона машины
            return value is IFormattable num ? num.ToString(null, CultureInfo.InvariantCulture)
                : value?.ToString();
        }

        private static void Set(object obj, string field, object value)
        {
            Type t = obj.GetType();
            FieldInfo f = t.GetField(field);
            if (f != null) { f.SetValue(obj, value); return; }
            PropertyInfo p = t.GetProperty(field);
            if (p == null) throw new InvalidOperationException("no member " + field);
            p.SetValue(obj, value);
        }

        private static void Pass(string id, string desc, string detail)
        {
            _green++;
            CaseLog.Add(new[] { id, "PASS", desc + " | " + detail });
            Console.WriteLine("PASS " + id + ": " + desc + (detail == null ? "" : " | " + detail));
        }

        private static void Fail(string id, string desc, string detail)
        {
            _red++;
            CaseLog.Add(new[] { id, "FAIL", desc + " | " + detail });
            Console.WriteLine("FAIL " + id + ": " + desc + " | " + detail);
        }

        private static void Info(string id, string detail)
        {
            Console.WriteLine("INFO " + id + ": " + detail);
        }

        private static string Describe(MethodInfo m)
        {
            return (m.IsStatic ? "static " : "") + m.Name + "(" +
                string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")";
        }

        private static string Sha256(string path)
        {
            using (FileStream fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return Convert.ToHexString(SHA256.HashData(fs));
            }
        }

        private static void WriteResult()
        {
            var result = new
            {
                bug = "bug-0537",
                sync = "bug-0530",
                date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                green = _green,
                red = _red,
                exit = _red == 0 ? 0 : 1,
                cases = CaseLog,
            };
            string path = Path.Combine(AppContext.BaseDirectory, "result.json");
            File.WriteAllText(path, JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                new UTF8Encoding(true));
            Console.WriteLine("result.json: " + path);
        }
    }
}
