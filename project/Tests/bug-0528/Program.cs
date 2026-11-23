// bug-0528 / BUG-0527: юнит-тесты U1-U12 + стенд S1 для фикса guard MarketDepth.Time в XTFutures UpdateDepth.
// Pure-reflection харнесс: OsEngine грузится из --lib (один билд харнесса прогоняется на base и fix несменяемо).
// ТЗ стендов: StandApprover/Plans/plan-20261123-001.md. В PR не попадает (носитель: ветка test/0528 + Artifacts).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;

internal static class Program
{
    static Assembly asm;
    static Type realType, mdType, logType, statusEnumType;
    static object srv;
    static MethodInfo miSnap, miUpd;
    static FieldInfo fLast, fAll, fInit, fSeq, fFifo;
    static PropertyInfo pStatus;
    static DateTime t0;

    static readonly List<(DateTime Time, string Sym)> emits = new List<(DateTime, string)>();
    static readonly List<(string Msg, string Kind)> logs = new List<(string, string)>();
    static int failures;
    static readonly List<string> report = new List<string>();

    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Console.WriteLine("UNHANDLED: " + e.ExceptionObject);
            try { File.AppendAllText("bug0528-crash.log", DateTime.Now + " " + e.ExceptionObject + "\n"); } catch { }
            Environment.Exit(91);
        };
        ThreadPool.GetMinThreads(out int wmin, out int amin);
        ThreadPool.SetMinThreads(Math.Max(wmin, 16), Math.Max(amin, 16));
        System.Threading.Timer watchdog = null;

        string lib = null, mode = "units", reportPath = null;
        string label = "run";
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--lib") lib = args[++i];
            if (args[i] == "--mode") mode = args[++i];
            if (args[i] == "--report") reportPath = args[++i];
            if (args[i] == "--label") label = args[++i];
        }
        if (lib == null) { Console.WriteLine("usage: --lib <dir> [--mode units|s1] [--report f] [--label x]"); return 2; }
        lib = Path.GetFullPath(lib);

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            string p = Path.Combine(lib, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
        asm = Assembly.LoadFrom(Path.Combine(lib, "OsEngine.dll"));

        mdType = asm.GetType("OsEngine.Entity.MarketDepth");
        logType = asm.GetType("OsEngine.LogMessageType");
        Type tm = asm.GetTypes().First(x => x.Name == "TimeManager");
        statusEnumType = asm.GetTypes().First(x => x.Name == "ServerConnectStatus");
        Type outer = asm.GetType("OsEngine.Market.Servers.XT.XTFutures.XTFuturesServer");
        realType = outer.GetNestedTypes().First(x => x.GetMethod("SnapshotDepth", BindingFlags.NonPublic | BindingFlags.Instance) != null);
        t0 = Tm(1600000000000L);

        watchdog = new System.Threading.Timer(_ =>
        {
            lock (report) { Flush(reportPath, label, "WATCHDOG timeout"); }
            Console.Out.Flush();
            Environment.Exit(9);
        }, null, mode == "s1" ? 30000 : 120000, Timeout.Infinite);

        int exitCode;
        if (mode == "s1") exitCode = RunS1();
        else exitCode = RunUnits();

        watchdog.Change(Timeout.Infinite, Timeout.Infinite);
        lock (report) Flush(reportPath, label, "exit=" + exitCode + " failures=" + failures);
        return exitCode;
    }

    static DateTime Tm(long ms) => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

    // ---------- сервер/рефлексия ----------

    static void NewServer()
    {
        emits.Clear();
        logs.Clear();
        srv = Activator.CreateInstance(realType);
        miSnap = realType.GetMethod("SnapshotDepth", BindingFlags.NonPublic | BindingFlags.Instance);
        miUpd = realType.GetMethod("UpdateDepth", BindingFlags.NonPublic | BindingFlags.Instance);
        fLast = realType.GetField("_lastTimeMd", BindingFlags.NonPublic | BindingFlags.Instance);
        fAll = realType.GetField("_allDepths", BindingFlags.NonPublic | BindingFlags.Instance);
        fInit = realType.GetField("_snapshotInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
        fSeq = realType.GetField("_lastSeqNum", BindingFlags.NonPublic | BindingFlags.Instance);
        fFifo = realType.GetField("FIFOListWebSocketPublicMessage", BindingFlags.NonPublic | BindingFlags.Instance);
        pStatus = realType.GetProperty("ServerStatus");
        HookMd(realType.GetEvent("MarketDepthEvent").EventHandlerType.GetGenericArguments()[0]);
        HookLog(realType.GetEvent("LogMessageEvent").EventHandlerType.GetGenericArguments()[1]);
    }

    // спьи точной типизацией: Action<T> через MakeGenericMethod (T = MarketDepth / LogMessageType из загруженного OsEngine)
    static void HookMd(Type mdT)
    {
        typeof(Program).GetMethod(nameof(AttachMd), BindingFlags.NonPublic | BindingFlags.Static)
            .MakeGenericMethod(mdT).Invoke(null, new object[] { srv });
    }

    static object GetMember(object o, string name)
    {
        Type t = o.GetType();
        object? v = t.GetProperty(name)?.GetValue(o);
        if (v == null) v = t.GetField(name)?.GetValue(o);
        return v!;
    }

    static void AttachMd<T>(object server)
    {
        Action<T> h = md =>
        {
            object o = md!;
            lock (emits) emits.Add(((DateTime)GetMember(o, "Time"), (string)GetMember(o, "SecurityNameCode")));
        };
        realType.GetEvent("MarketDepthEvent").AddEventHandler(server, h);
    }

    static void HookLog(Type logT)
    {
        typeof(Program).GetMethod(nameof(AttachLog), BindingFlags.NonPublic | BindingFlags.Static)
            .MakeGenericMethod(logT).Invoke(null, new object[] { srv });
    }

    static void AttachLog<T>(object server)
    {
        Action<string, T> h = (m, k) => { lock (logs) logs.Add((m, k?.ToString())); };
        realType.GetEvent("LogMessageEvent").AddEventHandler(server, h);
    }

    static void OnMd(object md)
    {
        lock (emits) emits.Add(((DateTime)mdType.GetProperty("Time").GetValue(md), (string)mdType.GetProperty("SecurityNameCode").GetValue(md)));
    }

    static void OnLog(string message, object kind)
    {
        lock (logs) logs.Add((message, kind?.ToString()));
    }

    static DateTime LastMd() => (DateTime)fLast.GetValue(srv);
    static bool Init() => (bool)fInit.GetValue(srv);
    static long Seq() => (long)fSeq.GetValue(srv);
    static int DepthsCount() => ((IList)fAll.GetValue(srv)).Count;
    static double TopBid()
    {
        IList list = (IList)fAll.GetValue(srv);
        if (list.Count == 0) return -1;
        IList bids = (IList)GetMember(list[0], "Bids");
        if (bids.Count == 0) return -2;
        return (double)GetMember(bids[0], "Bid");
    }

    static void Snap(string json) => miSnap.Invoke(srv, new object[] { json });
    static void Upd(string json) => miUpd.Invoke(srv, new object[] { json });

    static int ErrCount() => logs.Count(l => l.Msg != null && (l.Msg.StartsWith("UpdateDepth error") || l.Msg.StartsWith("SnapshotDepth error")));

    // ---------- JSON фикстуры (синтетика, ВСЕГДА непустые b+a по гейтам :1574/:1658) ----------

    const string B0 = "[[\"50000\",\"2\"],[\"49990\",\"3\"]]";
    const string A0 = "[[\"50010\",\"1.5\"],[\"50020\",\"4\"]]";

    static string SnapJson(string sym, long id, long? t, string topic = "depth", string b = null, string a = null, bool withT = true)
    {
        string tPart = withT ? "\"t\":\"" + t + "\"," : "";
        return "{\"topic\":\"" + topic + "\",\"data\":{\"id\":\"" + id + "\",\"s\":\"" + sym + "\"," + tPart
            + "\"b\":" + (b ?? B0) + ",\"a\":" + (a ?? A0) + "}}";
    }

    static string UpdJson(string sym, long fu, long? t, string topic = "depth_update", string b = null, string a = null, bool withT = true)
    {
        string tPart = withT ? "\"t\":\"" + t + "\"," : "";
        return "{\"topic\":\"" + topic + "\",\"data\":{\"s\":\"" + sym + "\"," + tPart + "\"fu\":\"" + fu + "\",\"b\":" + (b ?? "[[\"50000\",\"5\"]]") + ",\"a\":" + (a ?? "[[\"50010\",\"2\"]]") + "}}";
    }

    // Upd с сырой t-строкой (битые payload)
    static string UpdRawT(string sym, string fu, string tRaw, string b = null)
        => "{\"topic\":\"depth_update\",\"data\":{\"s\":\"" + sym + "\",\"t\":" + tRaw + ",\"fu\":\"" + fu + "\",\"b\":" + (b ?? "[[\"50000\",\"5\"]]") + ",\"a\":" + A0 + "}}";

    static int EmitCount() { lock (emits) return emits.Count; }
    static DateTime EmitAt(int i) { lock (emits) return emits[i].Time; }

    // ---------- ассерты ----------

    static void Check(string unit, string what, bool ok, string detail = "")
    {
        report.Add((ok ? "PASS " : "FAIL ") + unit + " | " + what + (detail != "" ? " | " + detail : ""));
        Console.WriteLine(report[report.Count - 1]);
        if (!ok) failures++;
    }

    static bool Monotonic()
    {
        lock (emits)
        {
            for (int i = 1; i < emits.Count; i++)
                if (emits[i].Time <= emits[i - 1].Time) return false;
            return true;
        }
    }

    // ---------- юниты ----------

    static int RunUnits()
    {
        // U1: ядро регрессии Error 13 (A/B). snapshot t=T0 + update того же t.
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Upd(UpdJson("btcusdt", 1001, 1600000000000));
        Check("U1", "2 emit", EmitCount() == 2, "emits=" + EmitCount());
        if (EmitCount() == 2)
            Check("U1", "2-й emit = T0+1tick", EmitAt(1) == EmitAt(0).AddTicks(1),
                "e1=" + EmitAt(0).ToString("o") + " e2=" + EmitAt(1).ToString("o") + " (база: равные)");
        Check("U1", "Error-отпечатков 0", ErrCount() == 0, "err=" + ErrCount());

        // U2: не-emit-продвижение _lastTimeMd + инвариант emit <= _lastTimeMd (A/B по last)
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        int eBefore = EmitCount();
        Upd(UpdJson("btcusdt", 1001, 1600000001000, b: "[[\"50000\",\"0\"],[\"49990\",\"0\"]]"));
        Check("U2", "обнуление книги: emit нет", EmitCount() == eBefore, "emits=" + EmitCount());
        Check("U2", "lastTimeMd продвинут в биржевое t (A/B: база=UtcNow)", LastMd() == Tm(1600000001000), "last=" + LastMd().ToString("o"));
        Upd(UpdJson("btcusdt", 1002, 1600000001000));
        Check("U2", "след. emit > пред. emit и <= lastTimeMd", EmitCount() == eBefore + 1 && EmitAt(1) == LastMd() && EmitAt(1) > EmitAt(0), "e=" + EmitAt(EmitCount() - 1).ToString("o"));
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000, a: "[]"));
        Check("U2", "снапшот a:[] — emit нет, last продвинут", EmitCount() == 0 && LastMd() == Tm(1600000000000), "emits=" + EmitCount() + " last=" + LastMd().ToString("o"));

        // U3: порядок diff->snapshot при равном t — снапшот клампится (+1tick). characterization (обе).
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Upd(UpdJson("btcusdt", 1001, 1600000002000));
        Snap(SnapJson("btcusdt", 1002, 1600000002000));
        Check("U3", "снапшот после diff с равным t = +1tick", EmitCount() == 3 && EmitAt(2) == EmitAt(1).AddTicks(1), "emits=" + EmitCount() + " e3=" + EmitAt(EmitCount() - 1).ToString("o"));

        // U4: resync + liveness
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Upd(UpdJson("btcusdt", 1001, 1600000000100));
        int e4 = EmitCount(); DateTime last4 = LastMd();
        Upd(UpdJson("btcusdt", 1005, 1600000000300));
        Check("U4", "пропуск seq: нет emit, last не двигается, init=false", EmitCount() == e4 && LastMd() == last4 && !Init(), "emits=" + EmitCount() + " init=" + Init());
        Upd(UpdJson("btcusdt", 1006, 1600000000400));
        Check("U4", "до нового снапшота молчание", EmitCount() == e4 && ErrCount() == 0);
        Snap(SnapJson("btcusdt", 2000, 1600000005000));
        Upd(UpdJson("btcusdt", 2001, 1600000005100));
        Check("U4", "liveness: после снапшота emit возобновились", EmitCount() == e4 + 2 && Monotonic(), "emits=" + EmitCount());

        // U5a: битый t="abc" — бросок ДО мутаций (A/B: база мутировала + UtcNow)
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Upd(UpdRawT("btcusdt", "1001", "\"abc\"", b: "[[\"50000\",\"7.25\"]]"));
        Check("U5", "t=abc: Error-отпечаток ровно 1", ErrCount() == 1, "err=" + ErrCount());
        Check("U5", "t=abc: emit нет", EmitCount() == 1, "emits=" + EmitCount());
        Check("U5", "t=abc: уровни не тронуты (A/B: база мутировала)", TopBid() == 2.0, "topBid=" + TopBid());
        Check("U5", "t=abc: lastTimeMd не тронут (A/B: база задрала до UtcNow)", LastMd() == t0, "last=" + LastMd().ToString("o"));
        Upd(UpdJson("btcusdt", 1001, 1600000000100));
        Check("U5", "residual: seq продвинут битым t → fu=1001 = тихий resync", EmitCount() == 1 && !Init(), "init=" + Init());
        Snap(SnapJson("btcusdt", 1002, 1600000001000));
        Upd(UpdJson("btcusdt", 1003, 1600000001100));
        Check("U5", "residual: годный fu=битый+1 после ре-снапа эмитит", EmitCount() == 3 && Monotonic(), "emits=" + EmitCount());

        // U5b: t=""
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Upd(UpdRawT("btcusdt", "1001", "\"\"", b: "[[\"50000\",\"7.25\"]]"));
        Check("U5", "t=\"\": Error 1, без emit/мутаций", ErrCount() == 1 && EmitCount() == 1 && TopBid() == 2.0 && LastMd() == t0, "err=" + ErrCount());

        // U5c: t поле отсутствует → 0 → 1970, свежий инстанс эмитит
        NewServer();
        Snap(SnapJson("btcusdt", 1000, null, withT: false));
        Check("U5", "снап t отсутствует → emit 1970-01-01", EmitCount() == 1 && EmitAt(0) == Tm(0), "emits=" + EmitCount() + " e=" + (EmitCount() > 0 ? EmitAt(0).ToString("o") : "-"));
        Upd(UpdJson("btcusdt", 1001, 1600000000000));
        Check("U5", "после 1970-эвента более поздний t клампится монотонно", EmitCount() == 2 && EmitAt(1) == Tm(1600000000000), "e2=" + (EmitCount() > 1 ? EmitAt(1).ToString("o") : "-"));

        // U5d: t="-1" (1969) после chain → stale → кламп +1tick (A/B: база эмитит регресс)
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Upd(UpdRawT("btcusdt", "1001", "\"-1\""));
        Check("U5", "t=-1: stale → emit=T0+1tick (A/B: база — регресс 1969)", EmitCount() == 2 && EmitAt(1) == t0.AddTicks(1), "e2=" + (EmitCount() > 1 ? EmitAt(1).ToString("o") : "-"));

        // U5e: overflow и валидный long вне DateTime — два кейса: Error 1, без мутаций
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Upd(UpdRawT("btcusdt", "1001", "\"99999999999999999999\"", b: "[[\"50000\",\"7.25\"]]"));
        Check("U5", "t>long: Error 1, без emit/мутаций", ErrCount() == 1 && EmitCount() == 1 && TopBid() == 2.0);
        Upd(UpdRawT("btcusdt", "1002", "\"253402300800000\"", b: "[[\"50000\",\"7.25\"]]"));
        Check("U5", "t валидный long вне DateTime (253402300800000 = MaxValue+1мс): Error 1, без emit/мутаций", ErrCount() == 2 && EmitCount() == 1 && TopBid() == 2.0, "err=" + ErrCount());

        // U5g: fu="abc" — бросок до гейта, seq не продвинут
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Upd("{\"topic\":\"depth_update\",\"data\":{\"s\":\"btcusdt\",\"t\":\"1600000000100\",\"fu\":\"abc\",\"b\":[[\"50000\",\"5\"]],\"a\":" + A0 + "}}");
        Check("U5", "fu=abc: Error 1, seq НЕ продвинут", ErrCount() == 1 && Seq() == 1000, "err=" + ErrCount() + " seq=" + Seq());
        Upd(UpdJson("btcusdt", 1001, 1600000000100));
        Check("U5", "fu=abc после — годный 1001 эмитит", EmitCount() == 2, "emits=" + EmitCount());

        // U5h: fu отсутствует → Convert(null)=0 → тихий resync БЕЗ Error (задокументировать опасность)
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Upd("{\"topic\":\"depth_update\",\"data\":{\"s\":\"btcusdt\",\"t\":\"1600000000100\",\"b\":[[\"50000\",\"5\"]],\"a\":" + A0 + "}}");
        Check("U5", "fu отсутствует → 0: ТИХИЙ resync без Error (опасность зафиксирована)", ErrCount() == 0 && !Init() && EmitCount() == 1, "err=" + ErrCount() + " init=" + Init());

        // U6: шторм 1000 равных t → точная арифметика T0+k*tick
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        for (int i = 1; i <= 1000; i++)
            Upd(UpdJson("btcusdt", 1000 + i, 1600000000000, b: "[[\"50000\",\"" + (2 + i % 5) + "\"]]"));
        bool exact = true; long firstBad = -1;
        lock (emits)
        {
            for (int i = 1; i <= 1000 && i < emits.Count; i++)
                if (emits[i].Time != t0.AddTicks(i)) { exact = false; firstBad = i; break; }
        }
        Check("U6", "1000 коллизий: emit==T0+k·tick точно (emits=" + EmitCount() + ", firstBad=" + firstBad + ")", exact && EmitCount() == 1001);
        Check("U6", "Error 0, дрейф < 1с", ErrCount() == 0 && EmitCount() == 1001 && (EmitAt(1000) - t0).TotalSeconds < 1, "drift=" + (EmitCount() == 1001 ? (EmitAt(1000) - t0).TotalMilliseconds + "ms" : "-"));

        // U7: отстающий сервер — эмит prev+1tick, дельта drift в отчёт (A/B: база — равные/regress)
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Upd(UpdJson("btcusdt", 1001, 1600000005000));
        bool lagOk = true;
        for (int i = 1; i <= 10; i++)
        {
            Upd(UpdJson("btcusdt", 1001 + i, 1600000000000));
            lock (emits) { if (emits.Count != 2 + i || emits[emits.Count - 1].Time != emits[emits.Count - 2].Time.AddTicks(1)) lagOk = false; }
        }
        Check("U7", "лаг-лента: каждый emit=prev+1tick (A/B: база RED)", lagOk && EmitCount() == 12, "emits=" + EmitCount());
        Check("U7", "monotonic + Error 0; drift зафиксирован", Monotonic() && ErrCount() == 0,
            "drift_emit_minus_t=" + (EmitCount() == 12 ? (EmitAt(11) - Tm(1600000000000)).TotalMilliseconds + "ms" : "-"));

        // U8a: апдейты до снапшота — тишина, last=MinValue
        NewServer();
        Upd(UpdJson("btcusdt", 5, 1600000000000));
        Upd(UpdJson("btcusdt", 6, 1600000000100));
        Check("U8", "апдейты до снапшота: no emit, last=MinValue, Error 0", EmitCount() == 0 && LastMd() == DateTime.MinValue && ErrCount() == 0);

        // U8b: resync-восстановление — снапшот с равным t клампится, liveness
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Upd(UpdJson("btcusdt", 1001, 1600000000100));
        int e8 = EmitCount();
        Upd(UpdJson("btcusdt", 9999, 1600000000200));
        Snap(SnapJson("btcusdt", 3000, 1600000000100));
        Check("U8", "после resync снапшот с равным t клампится, emit возобновлены", EmitCount() == e8 + 1 && EmitAt(e8) == t0.AddTicks(1000001) && Monotonic(), "emits=" + EmitCount());

        // U8c: снапшот id="abc" — бросок ДО инициализации флага
        NewServer();
        Snap("{\"topic\":\"depth\",\"data\":{\"id\":\"abc\",\"s\":\"btcusdt\",\"t\":\"1600000000000\",\"b\":" + B0 + ",\"a\":" + A0 + "}}");
        Check("U8", "снап id=abc: Error 1, init=false, Depths=0", ErrCount() == 1 && !Init() && DepthsCount() == 0, "err=" + ErrCount() + " init=" + Init());
        Upd(UpdJson("btcusdt", 1001, 1600000000100));
        Check("U8", "снап id=abc: апдейты игнор (нет emit)", EmitCount() == 0);

        // U8d: снапшот с битым t — флаги выставлены ДО парса, Depths не посеян, liveness-ловушка
        NewServer();
        Snap("{\"topic\":\"depth\",\"data\":{\"id\":\"1000\",\"s\":\"btcusdt\",\"t\":\"abc\",\"b\":" + B0 + ",\"a\":" + A0 + "}}");
        Check("U8", "снап t=abc: Error 1, init=true, Depths=0", ErrCount() == 1 && Init() && DepthsCount() == 0, "err=" + ErrCount());
        Upd(UpdJson("btcusdt", 1001, 1600000000100));
        Check("U8", "снап t=abc: апдейты тихо гибнут (depth==null), Error не растёт", EmitCount() == 0 && ErrCount() == 1, "err=" + ErrCount());
        Snap(SnapJson("btcusdt", 2000, 1600000005000));
        Check("U8", "liveness: годный снапшот оживляет поток", EmitCount() == 1);

        // U9: кросс-символьная коллизия по общему _lastTimeMd
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Snap(SnapJson("adausdt", 1000, 1600000000000));
        Snap(SnapJson("ethusdt", 1000, 1600000000000));
        Check("U9", "B/C при равном t: +1tick, эмит != сырой t символа", EmitCount() == 3 && EmitAt(1) == t0.AddTicks(1) && EmitAt(2) == t0.AddTicks(2) && emits[1].Sym == "adausdt", "e2=" + EmitAt(EmitCount() > 1 ? 1 : 0).ToString("o"));

        // U10: Time=биржевой t (sentinel, обе) + дискриминатор базы: |_lastTimeMd - T0| < 1 мин
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Upd(UpdJson("btcusdt", 1001, 1600000000050));
        Check("U10", "emit == GetDateTimeFromTimeStamp(t), не UtcNow (sentinel)", EmitCount() == 2 && EmitAt(0) == t0 && EmitAt(1) == Tm(1600000000050));
        Check("U10", "дискриминатор: |lastTimeMd - T0| < 1 мин (база: UtcNow-цепочка → RED)", Math.Abs((LastMd() - t0).TotalMinutes) < 1, "last=" + LastMd().ToString("o"));

        // U11: bucketing потребителя (CandleSeries :522 UpDateCandle(md.Time)) на клампованной ленте
        // shortcut: реплика минутного трейтинга CandleSeries (floor Time→минута);升级到 реальный CandleSeries если ОдТС потребует
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        List<long> raw = new List<long>();
        for (int i = 1; i <= 40; i++) { raw.Add(1600000000000 + i * 1000); Upd(UpdJson("btcusdt", 1000 + i, 1600000000000 + i * 1000)); }
        for (int i = 0; i < 30; i++) { raw.Add(1600000020000); Upd(UpdJson("btcusdt", 1041 + i, 1600000020000, b: "[[\"50000\",\"" + (2 + i % 3) + "\"]]")); }
        List<DateTime> RawBucket() => raw.Select(ms => FloorMin(Tm(ms))).ToList();
        List<DateTime> em; lock (emits) em = emits.Select(x => x.Time).ToList();
        List<DateTime> emBucket = em.Skip(1).Select(FloorMin).ToList();
        Check("U11", "bucketing клампов (+1tick) == сырой последовательности (минутные корзины)",
            emBucket.Count == raw.Count && emBucket.Zip(RawBucket(), (a, b) => a == b).All(x => x), "buckets=" + emBucket.Count);
        Check("U11", "время эмитов не убывает (свечной consumer-safe)", Monotonic() && ErrCount() == 0);

        // U12: отравление _lastTimeMd валидным БУДУЩИМ t — новый риск фикса, PR-note
        NewServer();
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Upd(UpdJson("btcusdt", 1001, 253402300799999));
        Upd(UpdJson("btcusdt", 1002, 1600000000000));
        Upd(UpdJson("btcusdt", 1003, 1600000060000));
        Check("U12", "будущий t проходит guard и отравляет ленту: монотонно, год==9999, Error 0 (A/B: база — регресс после)",
            EmitCount() == 4 && Monotonic() && EmitAt(3).Year == 9999 && ErrCount() == 0, "e4=" + EmitAt(EmitCount() - 1).ToString("o"));

        return failures;
    }

    static DateTime FloorMin(DateTime t) => new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, t.Kind);

    // ---------- S1: queue-driven стенд (worker-путь без сети) ----------

    static void Push(string raw)
    {
        object q = fFifo.GetValue(srv);
        q.GetType().GetMethod("Enqueue").Invoke(q, new object[] { raw });
    }

    static bool Poll(Func<bool> cond, int ms)
    {
        DateTime end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { if (cond()) return true; Thread.Sleep(20); }
        return cond();
    }

    static int RunS1()
    {
        DateTime start = DateTime.Now;
        NewServer();
        object connect = Enum.Parse(statusEnumType, "Connect");
        pStatus.SetValue(srv, connect);
        Check("S1", "ServerStatus=Connect установлен", connect.Equals(pStatus.GetValue(srv)));

        // (а) U1-пара: снапшот + дифф равного t
        Snap(SnapJson("btcusdt", 1000, 1600000000000));
        Push(UpdJson("btcusdt", 1001, 1600000000000));
        bool a = Poll(() => EmitCount() >= 2, 5000);
        Check("S1a", "worker выдал 2-й emit (liveness)", a, "emits=" + EmitCount());
        if (EmitCount() >= 2)
            Check("S1a", "строго монотонная пара (positive control: база даёт равную пару)", EmitAt(1) == EmitAt(0).AddTicks(1),
                "e1=" + EmitAt(0).ToString("o") + " e2=" + EmitAt(1).ToString("o"));

        // (б) шторм 300 равных t через очередь
        int baseN = EmitCount();
        for (int i = 1; i <= 300; i++) Push(UpdJson("btcusdt", 1001 + i, 1600000000000, b: "[[\"50000\",\"" + (2 + i % 4) + "\"]]"));
        bool bOk = Poll(() => EmitCount() >= baseN + 300, 8000);
        Check("S1b", "шторм 300 принят (emits=" + EmitCount() + ")", bOk && Monotonic(), "monotonic=" + Monotonic());

        // (в) lag-сервер: рывок вперёд, затем отстающие t
        Snap(SnapJson("btcusdt", 5000, 1600000030000));
        for (int i = 1; i <= 5; i++) Push(UpdJson("btcusdt", 5000 + i, 1600000000000));
        int afterC = baseN + 300 + 1;
        bool cOk = Poll(() => EmitCount() >= afterC + 5, 6000);
        lock (emits)
        {
            bool steps = true;
            for (int i = afterC; i < afterC + 5 && i < emits.Count; i++)
                if (emits[i].Time != emits[i - 1].Time.AddTicks(1)) steps = false;
            Check("S1c", "lag-лента: emit=prev+1tick (c-ok=" + cOk + ")", cOk && steps && Monotonic());
        }

        // (г) мусорный не-JSON фрейм → 1 Error читателя, очередь не потеряна (сон ридлера 2с)
        int errR0 = logs.Count(l => l.Msg != null && l.Msg.StartsWith("PublicMessageReader error"));
        Push("### not-json {{{");
        bool dErr = Poll(() => logs.Count(l => l.Msg != null && l.Msg.StartsWith("PublicMessageReader error")) == errR0 + 1, 5000);
        Push(SnapJson("btcusdt", 9000, 1600000060000));
        bool dLive = Poll(() => EmitCount() >= afterC + 5 + 1, 5000);
        Check("S1d", "мусор-фрейм: Error читателя==1, фреймы за ним не потеряны", dErr && dLive, "errReader=" + (logs.Count(l => l.Msg != null && l.Msg.StartsWith("PublicMessageReader error"))) + " emits=" + EmitCount());

        // (д) регистр топиков DEPTH_UPDATE / Depth (OrdinalIgnoreCase :1414/:1419)
        Push(UpdJson("btcusdt", 9001, 1600000061000, topic: "DEPTH_UPDATE"));
        bool e1 = Poll(() => EmitCount() >= afterC + 6 + 1, 4000);
        Push(SnapJson("btcusdt", 9500, 1600000062000, topic: "Depth"));
        bool e2 = Poll(() => EmitCount() >= afterC + 6 + 2, 4000);
        Check("S1d2", "регистр топиков: DEPTH_UPDATE и Depth маршрутизируются", e1 && e2, "emits=" + EmitCount());

        // (е) resync → снапшот → восстановление
        int beforeF = EmitCount();
        Push(UpdJson("btcusdt", 9999, 1600000070000));
        Thread.Sleep(300);
        Check("S1e", "resync: пропуск seq — эмиты стоят", EmitCount() <= beforeF + 0 + 1 && !Init() && Monotonic(), "emits=" + EmitCount() + " init=" + Init());
        Push(SnapJson("btcusdt", 10000, 1600000075000));
        Push(UpdJson("btcusdt", 10001, 1600000076000));
        bool fOk = Poll(() => EmitCount() >= beforeF + 2, 5000);
        Check("S1e", "liveness: после resync снапшот+дифф восстановили поток", fOk && Monotonic() && ErrCount() == 0, "emits=" + EmitCount() + " mdErr=" + ErrCount());

        double ep = (DateTime.Now - start).TotalSeconds;
        Check("S1", "эпизод < 20 c (trap ConnectionCheck :1189)", ep < 19.0, "episode=" + ep.ToString("0.0") + "s");
        pStatus.SetValue(srv, Enum.Parse(statusEnumType, "Disconnect"));
        Check("S1", "итоговая монотонность всех эмитов + Error-отпечатков MD==0 (" + EmitCount() + " эмитов)", Monotonic() && ErrCount() == 0, "emits=" + EmitCount());
        return failures;
    }

    static void Flush(string path, string label, string tail)
    {
        string text = "=== bug-0528 coverage run [" + label + "] ===\n" + string.Join("\n", report) + "\n" + tail + "\n";
        Console.WriteLine(tail);
        if (path != null) File.AppendAllText(path, text);
    }
}
