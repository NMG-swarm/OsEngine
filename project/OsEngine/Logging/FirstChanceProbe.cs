/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Diagnostics;
using System.Threading;

namespace OsEngine.Logging
{
    /// <summary>
    /// DIAG bug-0666, ветка test/0666-instr — в PR не попадает.
    /// След first-chance-исключений, последней не-OOM ошибки и heartbeat с памятью в CrashStart.log.
    /// Нужен потому, что под OOM WER-дамп не материализуется, а обработчики по политике BUG-0030 молчат
    /// и внешняя фореника остаётся без свидетелей. Включается только OSENGINE_DIAG_FIRSTCHANCE=1;
    /// без переменной класс не делает ничего.
    /// </summary>
    public static class FirstChanceProbe
    {
        /// <summary> Строжек OOM не больше: под OOM-штормом исключения сыплются непрерывно </summary>
        private const int MaxOomLines = 20;

        private static Process _self;

        private static Timer _heartbeat;

        private static int _attached;

        private static int _busy;

        private static int _oomLines;

        private static int _firstChanceCount;

        private static string _lastOther;

        /// <summary> Вызов сразу после Attach гварда в App.OnStartup </summary>
        public static void AttachIfEnabled()
        {
            if (Environment.GetEnvironmentVariable("OSENGINE_DIAG_FIRSTCHANCE") != "1")
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _attached, 1, 0) != 0)
            {
                return;
            }

            _self = Process.GetCurrentProcess();

            AppDomain.CurrentDomain.FirstChanceException += (sender, e) => OnFirstChance(e.Exception);
            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;

            _heartbeat = new Timer(LogHeartbeat, null, 30000, 30000);

            StartupCrashGuard.LogStartup("IsMainWindow  DIAG PROBE ATTACHED. Pid " + _self.Id
                + " WS " + WorkingSetMb() + "MB");
        }

        private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            // ProcessExit не срабатывает при Kill()/TerminateProcess — отсутствие строки при живом
            // heartbeat отличает снятие извне от выхода через Shutdown
            StartupCrashGuard.LogStartup("IsMainWindow  DIAG PROCESS_EXIT. WS " + WorkingSetMb() + "MB");
        }

        private static void OnFirstChance(Exception error)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            { // повторный вход: сама запись может бросить OOM
                return;
            }

            try
            {
                if (error == null)
                {
                    return;
                }

                Interlocked.Increment(ref _firstChanceCount);

                if (error is OutOfMemoryException
                    || error.InnerException is OutOfMemoryException)
                {
                    if (Interlocked.Increment(ref _oomLines) <= MaxOomLines)
                    {
                        StartupCrashGuard.LogStartup("IsMainWindow  DIAG FIRST-CHANCE OOM. WS "
                            + WorkingSetMb() + "MB GC " + ManagedMb() + "MB " + Short(error));
                    }

                    return;
                }

                _lastOther = Short(error);
            }
            catch
            { // зонд не имеет права ронять процесс
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private static void LogHeartbeat(object state)
        {
            try
            {
                StartupCrashGuard.LogStartup("IsMainWindow  DIAG HB. WS " + WorkingSetMb()
                    + "MB GC " + ManagedMb() + "MB firstChance " + _firstChanceCount
                    + " oom " + _oomLines + " last " + (_lastOther ?? "-"));
            }
            catch
            { // зонд не имеет права ронять процесс
            }
        }

        private static int WorkingSetMb()
        {
            try
            {
                return (int)(_self.WorkingSet64 / 1048576);
            }
            catch
            {
                return -1;
            }
        }

        private static int ManagedMb()
        {
            try
            {
                return (int)(GC.GetTotalMemory(false) / 1048576);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary> Одна строка, не длиннее 200 символов: под OOM длинная строка сама съест память </summary>
        private static string Short(Exception error)
        {
            try
            {
                string text = error.GetType().Name + ": " + error.Message;

                text = text.Replace('\r', ' ').Replace('\n', ' ');

                if (text.Length > 200)
                {
                    text = text.Substring(0, 200);
                }

                return text;
            }
            catch
            {
                return "unreadable exception";
            }
        }
    }
}
