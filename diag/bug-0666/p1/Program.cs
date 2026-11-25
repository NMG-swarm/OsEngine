using System;
using System.Diagnostics;
using System.Threading;

// bug-0666 DIAG P1: эталонные подписи смерти для пина P1 (Arifacts/Stands, в PR не попадает).
// Собирается как OsEngine.exe, чтобы WER LocalDumps реагировал по имени процесса.
// Ожидаемые эталоны см. runbook-b0666.md, блок «P1».
internal static class Program
{
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "exit0";

        Console.WriteLine("P1 start mode=" + mode + " pid=" + Process.GetCurrentProcess().Id);

        Thread.Sleep(5000); // время обёртке, ETW и сэмплеру подключиться

        if (mode == "failfast-oom")
        { // эталон a′: необработанный OOM фонового треда → FailFast рантайма
            Thread worker = new Thread(() => throw new OutOfMemoryException("synthetic a-prime"));
            worker.IsBackground = true;
            worker.Start();
            Thread.Sleep(30000);
            return 0;
        }

        if (mode == "kill")
        { // эталон внешнего снятия: TerminateProcess, ни one stop-строки, ни дампа
            Process.GetCurrentProcess().Kill();
            return 0;
        }

        return 0; // эталон чистого выхода
    }
}
