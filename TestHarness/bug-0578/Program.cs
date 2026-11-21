// BUG-0577 / bug-0578 — юнит-тесты маппинга (ТЗ, review-20261121-014; K1-K7 по ТЗ ОдТС plan-20261121-018).
// Локальный рантайм: в клон OsEngine и PR не попадают (копия харнесса — в ветке test/bug-0578 форка, только как листинг).
// Режим по умолчанию (fix): T1-T6 + K1-K7 на checkout fix/bug-0578 — ожидаем exit 0.
// Режим "base" (аргумент base): A/B-факты на origin/master — K2 off-by-one, K3 type-mismatch, T5-off-by-one.
// K1 матрица форматов TryParseStep + принудительный ru-RU + «1e-999»->скип + >=15-значная дробь не бросает.
// K2 off-by-one сентинел A/B (фикстура list.Count==1). K3 type-mismatch вложенного фильтра A/B:
//    fix — бросок на десериализации (в приложении -> ровно одна Error «Securities request error» :225,
//    catch покатегорный :191-227), бумага не грузится нулями; base — грузит (полей нет, мусор игнорируется).
// K4 value-дискриминант фолбэка: SYMA без qtyStep -> basePrecision; SYMB qtyStep=0.01 при bp=0.007 -> 0.01;
//    SYMC qtyStep="" -> скип, лога содержит qtyStep='' basePrecision='0.007'.
// K5 конъюнктивный skip-vs-status: 2 битых Trading -> skip-строк ровно 2; Settling/Delisted без логой;
//    "TRADING"/"Trading" валидные проходят гейт ToLower и добавляются.
// K6 массовый скип: N=5 tickSize="0" -> added=0 skipped=5, логои скипа ДО строки counters, Error=0.
// K7 бросок spy-подписчика на первой skip-логе: одна Error «convert error», ранее добавленные сохранены, наружу не бросает.
// Выход: 0 = PASS, 1 = FAIL, 3 = unhandled, 4 = watchdog.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using OsEngine.Entity;
using OsEngine.Logging;
using OsEngine.Market.Servers.BybitData;
using OsEngine.Market.Servers.BybitData.Entity;

namespace Bug0578Tests
{
    internal static class Program
    {
        static int _checks;
        static int _fails;
        static readonly List<string> FailLines = new List<string>();
        static bool _baseMode;

        static void Check(string id, string name, bool ok, string detail = "")
        {
            _checks++;
            if (!ok) { _fails++; FailLines.Add("FAILED: " + id + " " + name + (detail.Length > 0 ? " | " + detail : "")); }
            Console.WriteLine("CHECK [" + id + "] " + name + " " + (ok ? "PASS" : "FAIL") + (detail.Length > 0 ? " | " + detail : ""));
        }

        static void Fact(string id, string text)
        {
            Console.WriteLine("FACT [" + id + "] " + text);
        }

        // ---- синтетические raw-дампы instruments-info (форма tr302) ----------------

        const string PF_NULL = "\"priceFilter\":null";
        const string LF_NULL = "\"lotSizeFilter\":null";

        static string PF(string tickSize) { return "\"priceFilter\":{\"tickSize\":\"" + tickSize + "\",\"displayDecimalPlace\":\"2\"}"; }
        static string QTY(string q) { return "\"qtyStep\":\"" + q + "\""; }
        static string BP(string q) { return "\"basePrecision\":\"" + q + "\""; }
        static string LF(params string[] inner) { return "\"lotSizeFilter\":{" + string.Join(",", inner) + "}"; }

        // item по форме flat-Symbol V5 + вложенные фильтры; «отсутствует» = ключ не пишется вовсе
        static string Item(string symbol, string status, string contract, string quote, string pfJson, string lfJson)
        {
            return "{\"symbol\":\"" + symbol + "\",\"contractType\":\"" + contract + "\",\"status\":\"" + status +
                "\",\"baseCoin\":\"TST\",\"quoteCoin\":\"" + quote + "\",\"launchTime\":\"1500000000000\",\"deliveryTime\":\"0\"," +
                "\"priceScale\":\"2\",\"settleCoin\":\"USDT\"," + pfJson + "," + lfJson + "}";
        }

        static string Raw(string category, params string[] items)
        {
            return "{\"retCode\":\"0\",\"retMsg\":\"OK\",\"time\":\"1763000000000\",\"result\":{\"category\":\"" + category +
                "\",\"nextPageCursor\":\"\",\"list\":[" + string.Join(",", items) + "]}}";
        }

        // хвостовой filler: на master off-by-one режет ПОСЛЕДНИЙ item, проверяемая бумага должна посещаться циклом
        // => во все списки, кроме T5/K2 (они сами про off-by-one), добавляем Settled-заглушку последней
        static string Pad() { return Item("PADFILLUSDT", "Settled", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))); }

        // ---- harness: realization + reflection на ConvertSecurities/_securities ----

        static readonly MethodInfo MConvert = typeof(BybitDataServerRealization)
            .GetMethod("ConvertSecurities", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo FSec = typeof(BybitDataServerRealization)
            .GetField("_securities", BindingFlags.Instance | BindingFlags.NonPublic);
        // Security: свойство PriceStep имеет сеттер, который САМ выводит _decimals из шага (Security.cs:68-92).
        // Явная запись Decimals/DecimalsVolume в фиксе — вне скоупа (ОдБ). Проверяем контракт сущности:
        // бэккинг Decimals = вывод сеттера PriceStep (0.1 -> 1 знак), DecimalsVolume (поле) = 0.
        static readonly FieldInfo FDecimalsBacking = typeof(Security)
            .GetField("_decimals", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo MTryParse = typeof(BybitDataServerRealization)
            .GetMethod("TryParseStep", BindingFlags.Static | BindingFlags.NonPublic);

        class Harness : IDisposable
        {
            public readonly BybitDataServerRealization R;
            public readonly List<(string Msg, LogMessageType Type)> Logs = new List<(string, LogMessageType)>();
            readonly object _logGate = new object();

            public Harness()
            {
                if (MConvert == null || FSec == null)
                    throw new InvalidOperationException("harness members not found (ConvertSecurities/_securities API moved?)");
                R = new BybitDataServerRealization();
                R.LogMessageEvent += (m, t) => { lock (_logGate) { Logs.Add((m, t)); } };
            }

            // null = броска не было; иначе InnerException из-под TargetInvocationException
            public Exception Convert(string rawJson, string category)
            {
                object resp = JsonConvert.DeserializeObject<ByBitDataResponse<ListSymbols>>(rawJson);
                try { MConvert.Invoke(R, new object[] { resp, category }); return null; }
                catch (TargetInvocationException tie) { return tie.InnerException; }
                catch (Exception e) { return e; }
            }

            public List<Security> Secs() { return (List<Security>)FSec.GetValue(R); }
            public Security ByFull(string nameFull) { return Secs().FirstOrDefault(s => s.NameFull == nameFull); }
            public bool Logged(string needle) { lock (_logGate) { return Logs.Any(l => l.Msg.Contains(needle, StringComparison.OrdinalIgnoreCase)); } }
            public int Count() { return Secs().Count; }
            public int CountLogged(string prefix) { lock (_logGate) { return Logs.Count(l => l.Msg.StartsWith(prefix, StringComparison.Ordinal)); } }
            public int IndexLogged(string needle) { lock (_logGate) { return Logs.FindIndex(l => l.Msg.Contains(needle, StringComparison.OrdinalIgnoreCase)); } }
            public int ErrorCount() { lock (_logGate) { return Logs.Count(l => l.Type == LogMessageType.Error); } }

            public void Dispose()
            {
                try { R.Dispose(); } catch (Exception e) { Console.WriteLine("CHAR dispose " + e.GetType().Name); }
            }
        }

        static string D(decimal v) { return v.ToString(CultureInfo.InvariantCulture); }
        static string F(Security s, string what)
        {
            if (s == null) return what + ": бумага не в списке";
            return what + ": PriceStep=" + D(s.PriceStep) + " PriceStepCost=" + D(s.PriceStepCost) + " VolumeStep=" + D(s.VolumeStep);
        }

        // ---- T1: futures-маппинг (linear/inverse) -----------------------------------

        static void RunT1()
        {
            Console.WriteLine("== T1: futures — tickSize->PriceStep/PriceStepCost, qtyStep->VolumeStep ==");
            using (var h = new Harness())
            {
                string lin = Raw("linear", Item("T1LINUSDT", "Trading", "PERPETUAL", "USDT",
                    PF("0.1"), LF(QTY("0.1"), BP("9"), "\"minOrderQty\":\"0.001\"")), Pad());
                Exception th1 = h.Convert(lin, "linear");
                Check("T1a", "linear без броска", th1 == null, th1 == null ? "" : th1.GetType().Name + ": " + th1.Message);
                Security s1 = h.ByFull("T1LINUSDT");
                Check("T1a", "linear PriceStep=tickSize=0.1", s1 != null && s1.PriceStep == 0.1m, F(s1, "PriceStep"));
                Check("T1a", "linear PriceStepCost=PriceStep", s1 != null && s1.PriceStepCost == s1.PriceStep && s1.PriceStepCost == 0.1m, F(s1, "PriceStepCost"));
                Check("T1a", "linear VolumeStep=qtyStep=0.1 (не basePrecision)", s1 != null && s1.VolumeStep == 0.1m, F(s1, "VolumeStep"));

                string inv = Raw("inverse", Item("T1INVUSD", "Trading", "INVERSE_PERPETUAL", "USD",
                    PF("0.5"), LF(QTY("1"), "\"minOrderQty\":\"1\"")), Pad());
                Exception th2 = h.Convert(inv, "inverse");
                Check("T1b", "inverse без броска", th2 == null, th2 == null ? "" : th2.GetType().Name + ": " + th2.Message);
                Security s2 = h.ByFull("T1INVUSD");
                Check("T1b", "inverse PriceStep=0.5", s2 != null && s2.PriceStep == 0.5m, F(s2, "PriceStep"));
                Check("T1b", "inverse PriceStepCost=PriceStep", s2 != null && s2.PriceStepCost == 0.5m, F(s2, "PriceStepCost"));
                Check("T1b", "inverse VolumeStep=qtyStep=1", s2 != null && s2.VolumeStep == 1m, F(s2, "VolumeStep"));
            }
        }

        // ---- T2: spot-фолбэк basePrecision ------------------------------------------

        static void RunT2()
        {
            Console.WriteLine("== T2: spot — qtyStep отсутствует -> basePrecision->VolumeStep; '' -> guard-скип ==");
            using (var h = new Harness())
            {
                string btc = Raw("spot", Item("BTCUSDT", "Trading", "Spot", "USDT",
                    PF("0.01"), LF(BP("0.000001"), "\"marketMinRate\":\"0.1\"")), Pad());
                Exception th1 = h.Convert(btc, "spot");
                Check("T2a", "spot без броска", th1 == null, th1 == null ? "" : th1.GetType().Name + ": " + th1.Message);
                Security s1 = h.ByFull("BTCUSDT");
                Check("T2a", "spot VolumeStep=basePrecision=0.000001", s1 != null && s1.VolumeStep == 0.000001m, F(s1, "VolumeStep"));
                Check("T2a", "spot PriceStep=tickSize=0.01 (+Cost)", s1 != null && s1.PriceStep == 0.01m && s1.PriceStepCost == 0.01m, F(s1, "PriceStep"));

                string clr = Raw("spot", Item("CLEARUSDT", "Trading", "Spot", "USDT",
                    PF("0.001"), LF(BP("0.1"))), Pad());
                h.Convert(clr, "spot");
                Security s2 = h.ByFull("CLEARUSDT");
                Check("T2b", "spot CLEAR VolumeStep=basePrecision=0.1", s2 != null && s2.VolumeStep == 0.1m, F(s2, "VolumeStep"));

                string emp = Raw("spot", Item("EMPTYQUSDT", "Trading", "Spot", "USDT",
                    PF("0.001"), LF(QTY(""), BP("0.1"))), Pad());
                h.Convert(emp, "spot");
                Security s3 = h.ByFull("EMPTYQUSDT");
                Check("T2c", "qtyStep='' -> бумага скипнута (не 0 и не фолбэк)", s3 == null, F(s3, "empty-qty"));
                Check("T2c", "qtyStep='' -> логая имени бумаги", h.Logged("EMPTYQUSDT"), "логов=" + h.Logs.Count);
            }
        }

        // ---- T3 (M1): null всего вложенного объекта ----------------------------------

        static void RunT3()
        {
            Console.WriteLine("== T3 (M1): priceFilter/lotSizeFilter=null -> скип с логой, категория не обрезана ==");
            using (var h = new Harness())
            {
                string raw = Raw("linear",
                    Item("M1APFUSDT", "Trading", "PERPETUAL", "USDT", PF_NULL, LF(QTY("0.1"))),
                    Item("M1BUSDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))),
                    Item("M1CLFUSDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF_NULL),
                    Item("M1DUSDT", "Trading", "PERPETUAL", "USDT", PF("0.05"), LF(QTY("0.5"))),
                    Pad());
                Exception th = h.Convert(raw, "linear");
                Check("T3", "без броска наружу", th == null, th == null ? "" : th.GetType().Name + ": " + th.Message);
                Check("T3", "priceFilter=null -> M1APFUSDT скипнута", h.ByFull("M1APFUSDT") == null, F(h.ByFull("M1APFUSDT"), "M1A"));
                Check("T3", "lotSizeFilter=null -> M1CLFUSDT скипнута", h.ByFull("M1CLFUSDT") == null, F(h.ByFull("M1CLFUSDT"), "M1C"));
                Check("T3", "логая имени M1APFUSDT", h.Logged("M1APFUSDT"), "логов=" + h.Logs.Count);
                Check("T3", "логая имени M1CLFUSDT", h.Logged("M1CLFUSDT"), "логов=" + h.Logs.Count);
                Check("T3", "после скипов категория не обрезана: M1B и M1D на месте", h.ByFull("M1BUSDT") != null && h.ByFull("M1DUSDT") != null, "count=" + h.Count());
                Check("T3", "M1D валидна: PriceStep=0.05 VolumeStep=0.5", h.ByFull("M1DUSDT") != null
                    && h.ByFull("M1DUSDT").PriceStep == 0.05m && h.ByFull("M1DUSDT").VolumeStep == 0.5m, F(h.ByFull("M1DUSDT"), "M1D"));
                Check("T3", "catch цикла не сработал (нет «convert error»)", !h.Logged("convert error"), "error-строк=" + h.ErrorCount());
                Check("T3", "список ровно 2 (скипнутые не добавлены нулями)", h.Count() == 2, "count=" + h.Count());
            }
        }

        // ---- T4 (M4): guard ниже status-гейта ----------------------------------------

        static void RunT4()
        {
            Console.WriteLine("== T4 (M4): не-Trading отсекается гейтом раньше guard — ни в список, ни в логи скипа ==");
            using (var h = new Harness())
            {
                string raw = Raw("linear",
                    Item("ZZSETTLED9USDT", "Settled", "PERPETUAL", "USDT", PF_NULL, LF_NULL),
                    Item("ZZPRELAUNCH9", "PreLaunch", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))),
                    Item("ZZTRADING9USDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))),
                    Pad());
                Exception th = h.Convert(raw, "linear");
                Check("T4", "без броска", th == null, th == null ? "" : th.GetType().Name);
                Check("T4", "Settled с null-фильтрами не в списке", h.ByFull("ZZSETTLED9USDT") == null, "count=" + h.Count());
                Check("T4", "PreLaunch не в списке", h.ByFull("ZZPRELAUNCH9") == null, "count=" + h.Count());
                Check("T4", "Settled не порождает логой (гейт выше guard)", !h.Logged("ZZSETTLED9USDT"), "логов=" + h.Logs.Count);
                Check("T4", "Trading-бумага категории добавлена", h.ByFull("ZZTRADING9USDT") != null, "count=" + h.Count());
            }
        }

        // ---- T5: off-by-one ----------------------------------------------------------

        static void RunT5()
        {
            Console.WriteLine("== T5: цикл i<Count — последняя Trading-бумага категории добавляется ==");
            using (var h = new Harness())
            {
                string raw = Raw("linear",
                    Item("OB1USDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))),
                    Item("OB2USDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))),
                    Item("OB3LASTUSDT", "Trading", "PERPETUAL", "USDT", PF("0.2"), LF(QTY("0.2"))));
                Exception th = h.Convert(raw, "linear");
                Check("T5", "без броска", th == null, th == null ? "" : th.GetType().Name);
                Check("T5", "visited==Count: добавлено 3 из 3", h.Count() == 3, "count=" + h.Count());
                Check("T5", "последняя OB3LASTUSDT в списке", h.ByFull("OB3LASTUSDT") != null, "count=" + h.Count());
                Check("T5", "OB3 валидна: PriceStep=0.2 VolumeStep=0.2", h.ByFull("OB3LASTUSDT") != null
                    && h.ByFull("OB3LASTUSDT").PriceStep == 0.2m && h.ByFull("OB3LASTUSDT").VolumeStep == 0.2m, F(h.ByFull("OB3LASTUSDT"), "OB3"));
            }
        }

        // ---- T6: обратная совместимость ----------------------------------------------

        static void RunT6()
        {
            Console.WriteLine("== T6 BC: степы из API вместо 0; Decimals/DecimalsVolume не тронуты; Lot/State/Exchange/NameClass как в master ==");
            using (var h = new Harness())
            {
                string raw = Raw("linear", Item("BC1USDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))), Pad());
                Exception th = h.Convert(raw, "linear");
                Check("T6", "без броска", th == null, th == null ? "" : th.GetType().Name);
                Security s = h.ByFull("BC1USDT");
                Check("T6", "были 0 -> PriceStep/PriceStepCost/VolumeStep из API", s != null
                    && s.PriceStep == 0.1m && s.PriceStepCost == 0.1m && s.VolumeStep == 0.1m, F(s, "BC1"));
                int decBacking = s == null || FDecimalsBacking == null ? -1 : (int)FDecimalsBacking.GetValue(s);
                Check("T6", "Decimals/DecimalsVolume явным кодом не пишутся: DecimalsVolume(поле)=0, _decimals=1 — авто-вывод сеттера PriceStep (0.1 -> 1 знак, Security.cs:71-92)", s != null
                    && decBacking == 1 && s.DecimalsVolume == 0 && s.Decimals == decBacking,
                    "decimals_backing=" + decBacking + " getter=" + (s == null ? -1 : s.Decimals) + " DecimalsVolume=" + (s == null ? -1 : s.DecimalsVolume));
                Check("T6", "Lot=1, State=Activ, Exchange=Bybit, NameClass=PERPETUAL, Name=*.P", s != null
                    && s.Lot == 1m && s.State == SecurityStateType.Activ && s.Exchange == OsEngine.Market.ServerType.Bybit.ToString()
                    && s.NameClass == "PERPETUAL" && s.Name == "BC1USDT.P",
                    "Lot=" + (s == null ? 0 : D(s.Lot)) + " Name=" + (s == null ? "-" : s.Name) + " NameClass=" + (s == null ? "-" : s.NameClass));
            }
        }

        // ---- K1: матрица форматов TryParseStep --------------------------------------

        static (bool ok, decimal val) TryParseStep(string value)
        {
            if (MTryParse == null) throw new InvalidOperationException("TryParseStep not found (fix not applied?)");
            object[] a = new object[] { value, 0m };
            bool ok = (bool)MTryParse.Invoke(null, a);
            return (ok, (decimal)a[1]);
        }

        static void RunK1()
        {
            Console.WriteLine("== K1: матрица форматов TryParseStep + ru-RU + 1e-999 + 15+ знаков ==");
            var prev = CultureInfo.CurrentCulture;
            try
            {
                var matrix = new (string v, bool expect, string why)[]
                {
                    ("0.01", true, "обычный дробный"),
                    ("1", true, "целый"),
                    ("1E-5", true, "экспонента E"),
                    ("", false, "пустая строка"),
                    (null, false, "null"),
                    ("0", false, "нулевой шаг"),
                    ("-0.1", false, "отрицательный"),
                    ("abc", false, "мусор"),
                    ("1e-999", false, "underflow экспоненты -> скип"),
                    ("0.123456789012345678901234567", true, "27 знаков -> не бросает"),
                };
                foreach (var c in matrix)
                {
                    bool threw = false; string tname = ""; decimal val = 0m; bool ok = false;
                    try { (ok, val) = TryParseStep(c.v); }
                    catch (Exception e) { threw = true; tname = e.GetType().Name; }
                    Check("K1", "TryParseStep(" + (c.v ?? "<null>") + ") " + c.why + " -> " + c.expect,
                        !threw && ok == c.expect, threw ? "бросок " + tname : "ok=" + ok + " val=" + D(val));
                }

                // принудительный ru-RU: разделитель-точка должна читаться (InvariantCulture в TryParseStep)
                CultureInfo ci = new CultureInfo("ru-RU");
                CultureInfo.CurrentUICulture = ci;
                CultureInfo.CurrentCulture = ci;
                var (okRu, valRu) = TryParseStep("0.0005");
                Check("K1r", "ru-RU culture: «0.0005» -> 0.0005 (инвариантный парсер)", okRu && valRu == 0.0005m, "ok=" + okRu + " val=" + D(valRu));

                using (var h = new Harness())
                {
                    string raw = Raw("linear", Item("K1RUUSDT", "Trading", "PERPETUAL", "USDT", PF("0.0005"), LF(QTY("0.0005"))), Pad());
                    Exception th = h.Convert(raw, "linear");
                    Security s = h.ByFull("K1RUUSDT");
                    Check("K1r", "ru-RU culture сквоз Convert: PaperPriceStep=0.0005", th == null && s != null && s.PriceStep == 0.0005m && s.VolumeStep == 0.0005m, F(s, "K1RU"));
                }

                // 1e-999 сквоз Convert: скип с логой имени
                using (var h2 = new Harness())
                {
                    string raw = Raw("linear", Item("K1E999USDT", "Trading", "PERPETUAL", "USDT", PF("1e-999"), LF(QTY("0.1"))), Pad());
                    Exception th = h2.Convert(raw, "linear");
                    Check("K1e", "tickSize=1e-999 -> скип с логой, без броска", th == null && h2.ByFull("K1E999USDT") == null && h2.Logged("K1E999USDT"), F(h2.ByFull("K1E999USDT"), "K1E999"));
                }
            }
            finally { CultureInfo.CurrentCulture = prev; }
        }

        // ---- K2: off-by-one сентинел A/B ---------------------------------------------

        static void RunK2fix()
        {
            Console.WriteLine("== K2 (fix): list.Count==1 -> 1 бумага; сентинел: последняя валидная включена ==");
            using (var h = new Harness())
            {
                string solo = Raw("linear", Item("K2SOLOUSDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))));
                Exception th = h.Convert(solo, "linear");
                Check("K2a", "Count==1: добавлена единственная (последняя) бумага", th == null && h.Count() == 1 && h.ByFull("K2SOLOUSDT") != null, "count=" + h.Count());

                string sent = Raw("linear",
                    Item("K2SENTAUSDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))),
                    Item("K2SENTBUSDT", "Trading", "PERPETUAL", "USDT", PF("0.3"), LF(QTY("0.3"))));
                h.Convert(sent, "linear");
                Security b = h.ByFull("K2SENTBUSDT");
                Check("K2a", "сентинел: A и B добавлены, B (последняя) валидна PriceStep=0.3", h.Count() == 3 && b != null && b.PriceStep == 0.3m, "count=" + h.Count() + " " + F(b, "B"));
            }
        }

        static void RunK2base()
        {
            Console.WriteLine("== K2 (base): off-by-one — Count==1 даёт 0 бумаг; сентинел режет последнюю ==");
            using (var h = new Harness())
            {
                string solo = Raw("linear", Item("K2SOLOUSDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))));
                Exception th = h.Convert(solo, "linear");
                Check("K2b", "base Count==1 -> 0 добавлено (последняя съедена)", th == null && h.Count() == 0, "count=" + h.Count());

                string sent = Raw("linear",
                    Item("K2SENTAUSDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))),
                    Item("K2SENTBUSDT", "Trading", "PERPETUAL", "USDT", PF("0.3"), LF(QTY("0.3"))));
                h.Convert(sent, "linear");
                Check("K2b", "base сентинел: A есть, B (последняя) отсутствует", h.Count() == 1 && h.ByFull("K2SENTAUSDT") != null && h.ByFull("K2SENTBUSDT") == null, "count=" + h.Count());
            }
        }

        // ---- K3: type-mismatch вложенного фильтра A/B -------------------------------

        static void RunK3fix()
        {
            Console.WriteLine("== K3 (fix): priceFilter=[]/\"\"/123 — бросок на десериализации, бумага не грузится нулями; сосед цел ==");
            string[] bads = { "\"priceFilter\":[]", "\"priceFilter\":\"\"", "\"priceFilter\":123" };
            foreach (string bad in bads)
            {
                using (var h = new Harness())
                {
                    string raw = Raw("linear", Item("K3BADUSDT", "Trading", "PERPETUAL", "USDT", bad, LF(QTY("0.1"))), Pad());
                    string outcome; bool loaded;
                    try
                    {
                        Exception th = h.Convert(raw, "linear");
                        loaded = h.ByFull("K3BADUSDT") != null;
                        outcome = th == null ? (loaded ? "SILENT-LOAD" : "NO-THROW-SKIP") : "THROW:" + th.GetType().Name;
                    }
                    catch (Exception e)
                    {
                        // бросок из JsonConvert.DeserializeObject — в приложении это catch GetSecuritiesByCategory:223
                        // -> ровно одна Error «Securities request error» (:225), категория пропущена, соседние категории целы (:178-181)
                        outcome = "THROW:" + e.GetType().Name;
                        loaded = false;
                    }
                    Fact("K3f", bad + " -> " + outcome);
                    Check("K3f", "type-mismatch " + bad + ": не молчаливая загрузка с нулями", !loaded && outcome != "SILENT-LOAD", outcome);
                }
            }
            using (var h2 = new Harness())
            {
                string good = Raw("linear", Item("K3GOODUSDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))), Pad());
                Exception th = h2.Convert(good, "linear");
                Check("K3f", "соседняя категория после сбоя грузится целой", th == null && h2.ByFull("K3GOODUSDT") != null, F(h2.ByFull("K3GOODUSDT"), "K3GOOD"));
            }
        }

        static void RunK3base()
        {
            Console.WriteLine("== K3 (base): вложенный мусор игнорируется (полей DTO нет) — бумага грузится ==");
            string[] bads = { "\"priceFilter\":[]", "\"priceFilter\":\"\"", "\"priceFilter\":123" };
            foreach (string bad in bads)
            {
                using (var h = new Harness())
                {
                    string raw = Raw("linear", Item("K3BADUSDT", "Trading", "PERPETUAL", "USDT", bad, LF(QTY("0.1"))), Pad());
                    Exception th = null; bool loaded = false;
                    try { th = h.Convert(raw, "linear"); loaded = h.ByFull("K3BADUSDT") != null; }
                    catch (Exception e) { th = e; }
                    Fact("K3b", bad + " -> base: throw=" + (th?.GetType().Name ?? "none") + " loaded=" + loaded + " count=" + h.Count());
                    Check("K3b", "base грузит " + bad + " (mismatch не виден base-DTO)", th == null && loaded, th?.GetType().Name ?? "");
                }
            }
        }

        // ---- K4: value-дискриминант фолбэка ------------------------------------------

        static void RunK4()
        {
            Console.WriteLine("== K4: SYMA без qtyStep->bp 0.007; SYMB qtyStep=0.01 при bp=0.007 -> 0.01; SYMC qtyStep='' -> скип с логой ==");
            using (var h = new Harness())
            {
                string raw = Raw("spot",
                    Item("SYMAUSDT", "Trading", "Spot", "USDT", PF("0.01"), LF(BP("0.007"))),
                    Item("SYMBUSDT", "Trading", "Spot", "USDT", PF("0.01"), LF(QTY("0.01"), BP("0.007"))),
                    Item("SYMCUSDT", "Trading", "Spot", "USDT", PF("0.01"), LF(QTY(""), BP("0.007"))),
                    Pad());
                Exception th = h.Convert(raw, "spot");
                Check("K4", "без броска", th == null, th?.GetType().Name ?? "");
                Security a = h.ByFull("SYMAUSDT");
                Security b = h.ByFull("SYMBUSDT");
                Security c = h.ByFull("SYMCUSDT");
                Check("K4a", "SYMA: qtyStep отсутствует -> VolumeStep=basePrecision=0.007", a != null && a.VolumeStep == 0.007m, F(a, "SYMA"));
                Check("K4b", "SYMB: qtyStep=0.01 приоритетнее bp=0.007 (разные значения — дискриминант)", b != null && b.VolumeStep == 0.01m, F(b, "SYMB"));
                Check("K4c", "SYMC: qtyStep='' -> скип (не фолбэк)", c == null, F(c, "SYMC"));
                string need = "qtyStep='' basePrecision='0.007'";
                Check("K4c", "SYMC лога содержит \"" + need + "\"", h.Logged(need), "логов=" + h.Logs.Count);
            }
        }

        // ---- K5: конъюнктивный skip-vs-status ----------------------------------------

        static void RunK5()
        {
            Console.WriteLine("== K5: 2 битых Trading -> ровно 2 skip-логи; Settling/Delisted без логой; TRADING/Trading целы ==");
            using (var h = new Harness())
            {
                string raw = Raw("linear",
                    Item("K5BAD1USDT", "Trading", "PERPETUAL", "USDT", PF("junk"), LF(QTY("0.1"))),
                    Item("K5BAD2USDT", "Trading", "PERPETUAL", "USDT", PF_NULL, LF_NULL),
                    Item("K5SETTLINGUSDT", "Settling", "PERPETUAL", "USDT", PF_NULL, LF_NULL),
                    Item("K5DELISTEDUSDT", "Delisted", "PERPETUAL", "USDT", PF_NULL, LF_NULL),
                    Item("K5UPPERUSDT", "TRADING", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))),
                    Item("K5MIXED9USDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))),
                    Pad());
                Exception th = h.Convert(raw, "linear");
                Check("K5", "без броска", th == null, th?.GetType().Name ?? "");
                int skips = h.CountLogged("FIX-0578 skip ");
                Check("K5", "skip-строк ровно 2 (битые Trading), не-Trading без логой", skips == 2, "skips=" + skips + " логов=" + h.Logs.Count);
                Check("K5", "Settling/Delisted не упомянуты в логах", !h.Logged("K5SETTLINGUSDT") && !h.Logged("K5DELISTEDUSDT"), "логов=" + h.Logs.Count);
                Check("K5", "TRADING (регистр) и Trading проходят гейт ToLower и добавлены",
                    h.ByFull("K5UPPERUSDT") != null && h.ByFull("K5MIXED9USDT") != null, "count=" + h.Count());
                Fact("K5", "status-гейт ToLower() culture-dependent — tr-TR 'I'->'ı' риск зафиксирован как наследие (PR-note), тест на дефолтной культуре");
            }
        }

        // ---- K6: массовый скип без Error ---------------------------------------------

        static void RunK6()
        {
            Console.WriteLine("== K6: 5x tickSize=\"0\" -> added=0 skipped=5, логои после цикла, Error=0 ==");
            using (var h = new Harness())
            {
                string raw = Raw("linear",
                    Item("K6AUSDT", "Trading", "PERPETUAL", "USDT", PF("0"), LF(QTY("0.1"))),
                    Item("K6BUSDT", "Trading", "PERPETUAL", "USDT", PF("0"), LF(QTY("0.1"))),
                    Item("K6CUSDT", "Trading", "PERPETUAL", "USDT", PF("0"), LF(QTY("0.1"))),
                    Item("K6DUSDT", "Trading", "PERPETUAL", "USDT", PF("0"), LF(QTY("0.1"))),
                    Item("K6EUSDT", "Trading", "PERPETUAL", "USDT", PF("0"), LF(QTY("0.1"))));
                Exception th = h.Convert(raw, "linear");
                Check("K6", "без броска, added=0, skipped=5", th == null && h.Count() == 0
                    && h.Logged("added=0") && h.Logged("skipped=5"), F(null, "K6") + " count=" + h.Count());
                int skips = h.CountLogged("FIX-0578 skip ");
                int countersIdx = h.IndexLogged("FIX-0578 counters");
                int firstSkip = h.IndexLogged("FIX-0578 skip ");
                Check("K6", "5 skip-логий напечатаны ПОСЛЕ цикла (до строки counters)", skips == 5 && countersIdx > firstSkip && countersIdx > 0, "skips=" + skips + " countersIdx=" + countersIdx);
                Check("K6", "Error-строк нет", h.ErrorCount() == 0, "errors=" + h.ErrorCount());
                Check("K6", "steps-строка с real-нулЯми: nonzero=0 во всех трёх", h.Logged("priceStep_nonzero=0") && h.Logged("priceStepCost_nonzero=0") && h.Logged("volumeStep_nonzero=0"), "");
                Fact("K6", "наследие (PR-note): AServer.cs:1429-1432 при пустом наборе переспрашивает раз в 1с — шторм-риск при нулевом added без Error в самом коннекторе не рождается");
            }
        }

        // ---- K7: бросок подписчика логов ---------------------------------------------

        static void RunK7()
        {
            Console.WriteLine("== K7: spy-подписчик бросает на первой skip-логе -> одна Error, добавленные сохранены, наружу чисто ==");
            using (var h = new Harness())
            {
                bool fired = false;
                h.R.LogMessageEvent += (m, t) =>
                {
                    if (!fired && m.StartsWith("FIX-0578 skip ", StringComparison.Ordinal))
                    {
                        fired = true;
                        throw new InvalidOperationException("spy boom");
                    }
                };
                string raw = Raw("linear",
                    Item("K7V1USDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))),
                    Item("K7V2USDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))),
                    Item("K7BADUSDT", "Trading", "PERPETUAL", "USDT", PF("x"), LF(QTY("0.1"))),
                    Item("K7V3USDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))));
                Exception th = h.Convert(raw, "linear");
                Check("K7", "наружу ничего не бросает", th == null, th == null ? "" : th.GetType().Name + ": " + th.Message);
                Check("K7", "ровно одна Error-строка (catch ConvertSecurities)", h.ErrorCount() == 1, "errors=" + h.ErrorCount());
                Check("K7", "все 3 валидные добавлены ДО сбоя логой сохранены",
                    h.ByFull("K7V1USDT") != null && h.ByFull("K7V2USDT") != null && h.ByFull("K7V3USDT") != null && h.Count() == 3,
                    "count=" + h.Count());
            }
        }

        // ---- base-режим: зеркало T5 (off-by-one) --------------------------------------

        static void RunT5base()
        {
            Console.WriteLine("== T5 (base): off-by-one — последняя Trading-бумага съедается ==");
            using (var h = new Harness())
            {
                string raw = Raw("linear",
                    Item("OB1USDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))),
                    Item("OB2USDT", "Trading", "PERPETUAL", "USDT", PF("0.1"), LF(QTY("0.1"))),
                    Item("OB3LASTUSDT", "Trading", "PERPETUAL", "USDT", PF("0.2"), LF(QTY("0.2"))));
                Exception th = h.Convert(raw, "linear");
                Check("T5b", "base: добавлено 2 из 3, OB3LASTUSDT отсутствует", th == null && h.Count() == 2 && h.ByFull("OB3LASTUSDT") == null, "count=" + h.Count());
            }
        }

        // ---- plumbing -----------------------------------------------------------------

        static string Cut(string s, int n = 160)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        [STAThread]
        static void Main(string[] args)
        {
            _baseMode = args != null && args.Length > 0 && args[0] == "base";
            try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }
            AppDomain.CurrentDomain.UnhandledException += (s0, e) =>
            {
                Console.WriteLine("UNHANDLED: " + (e.ExceptionObject as Exception));
                Environment.Exit(3);
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Console.WriteLine("UNOBSERVED: " + e.Exception);
                Environment.Exit(3);
            };

            var watchdog = new Thread(() => { Thread.Sleep(TimeSpan.FromSeconds(240)); Console.WriteLine("WATCHDOG TIMEOUT"); Environment.Exit(4); });
            watchdog.IsBackground = true;
            watchdog.Start();

            Console.WriteLine("ENGINE_DLL_SHA=" + ShaOfLoadedEngine());
            Console.WriteLine("MODE=" + (_baseMode ? "base" : "fix"));

            try
            {
                if (_baseMode)
                {
                    RunK2base();
                    RunK3base();
                    RunT5base();
                }
                else
                {
                    RunT1();
                    RunT2();
                    RunT3();
                    RunT4();
                    RunT5();
                    RunT6();
                    RunK1();
                    RunK2fix();
                    RunK3fix();
                    RunK4();
                    RunK5();
                    RunK6();
                    RunK7();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("STAND CRASH: " + Cut(e.ToString(), 4000));
                Environment.Exit(1);
            }

            Console.WriteLine("RESULT ALL: " + (_fails == 0 ? "PASS" : "FAIL") + " " + (_checks - _fails) + "/" + _checks + " checks");
            foreach (string f in FailLines) Console.WriteLine(f);
            string redSet = string.Join(",", FailLines
                .Select(f => System.Text.RegularExpressions.Regex.Match(f, @"FAILED: (T\d+[a-z]?|K\d+[a-z]?)"))
                .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToArray());
            Console.WriteLine("RED_SET=" + redSet);
            Environment.Exit(_fails == 0 ? 0 : 1);
        }

        static string ShaOfLoadedEngine()
        {
            try
            {
                string path = System.Reflection.Assembly.GetAssembly(typeof(Security)).Location;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(System.IO.File.ReadAllBytes(path))).Replace("-", "").ToLower();
            }
            catch (Exception e) { return "n/a (" + e.GetType().Name + ")"; }
        }
    }
}
