using System;
using System.Collections.Generic;

namespace ScpItdMod.Pc {
    public enum JobState : byte { Queued, Printing, Done, Failed, Cancelled }

    public enum PrinterState : byte { Ready, Off, NoPaper, Busy, Gone }

    public sealed class PrintJob {
        public uint Id;
        public ulong Account;
        public uint Token;
        public ushort Printer;
        public uint PrinterEpoch;
        public ulong File;
        public uint Rev;
        public string Title = "";
        public string[] Pages = Array.Empty<string>();
        public int Printed;
        public JobState State;
        public PcError Error;
        public double Created;
        public double PageStarted;
        public double PausedAt;
        public bool Paused;
        public double Finished;
    }

    public sealed class PrintQueue {
        public const int MaxPerPrinter = 8;
        public const int MaxPerAccount = 3;
        public const int MaxPages = 6;
        public const double PageSeconds = 4.0;
        public const double KeepFinished = 120.0;
        public const int MaxJobs = 256;

        private readonly List<PrintJob> jobs = new List<PrintJob>();
        private uint nextId = 1;

        public IReadOnlyList<PrintJob> Jobs => jobs;

        public void Clear() => jobs.Clear();

        public PrintJob Find(uint id) {
            for (int i = 0; i < jobs.Count; i++) {
                if (jobs[i].Id == id) { return jobs[i]; }
            }
            return null;
        }

        public static bool Active(PrintJob j) => j.State == JobState.Queued || j.State == JobState.Printing;

        public PcError Enqueue(ulong account, uint token, ushort printer, uint epoch, ulong file, uint rev, string title, string body, double now, out PrintJob job) {
            job = null;
            if (epoch == 0) { return PcError.NoPrinter; }
            for (int i = 0; i < jobs.Count; i++) {
                PrintJob j = jobs[i];
                if (j.Account == account && j.Token == token && token != 0) {
                    job = j;
                    return PcError.Ok;
                }
            }
            int forPrinter = 0;
            int forAccount = 0;
            for (int i = 0; i < jobs.Count; i++) {
                if (!Active(jobs[i])) { continue; }
                if (jobs[i].Printer == printer && jobs[i].PrinterEpoch != epoch) { Fail(jobs[i], PcError.NoPrinter, now); continue; }
                if (jobs[i].Printer == printer) { forPrinter++; }
                if (jobs[i].Account == account) { forAccount++; }
            }
            if (forPrinter >= MaxPerPrinter || forAccount >= MaxPerAccount || jobs.Count >= MaxJobs && !Prune(now, true)) { return PcError.Full; }
            string[] pages = Paginate(body ?? "", PcLimits.PageChars, PcLimits.PageLines);
            if (pages.Length > MaxPages) { return PcError.TooLarge; }
            job = new PrintJob {
                Id = nextId++, Account = account, Token = token, Printer = printer, PrinterEpoch = epoch, File = file, Rev = rev,
                Title = title ?? "", Pages = pages, State = JobState.Queued, Created = now
            };
            jobs.Add(job);
            return PcError.Ok;
        }

        public PcError Cancel(ulong account, bool gm, uint id, double now = 0) {
            PrintJob j = Find(id);
            if (j == null) { return PcError.NotFound; }
            if (!gm && j.Account != account) { return PcError.Denied; }
            if (!Active(j)) { return PcError.Invalid; }
            j.State = JobState.Cancelled;
            j.Finished = now;
            return PcError.Ok;
        }

        public PrintJob Head(ushort printer, uint epoch = 0) {
            PrintJob best = null;
            for (int i = 0; i < jobs.Count; i++) {
                PrintJob j = jobs[i];
                if (j.Printer != printer || epoch != 0 && j.PrinterEpoch != epoch || !Active(j)) { continue; }
                if (j.State == JobState.Printing) { return j; }
                if (best == null || j.Id < best.Id) { best = j; }
            }
            return best;
        }

        public bool Step(ushort printer, uint epoch, PrinterState state, double now, out PrintJob job, out int page) {
            job = Head(printer);
            page = -1;
            if (job == null) { return false; }
            if (state == PrinterState.Gone || job.PrinterEpoch != epoch) {
                Fail(job, PcError.NoPrinter, now);
                return false;
            }
            if (state != PrinterState.Ready) {
                if (job.State == JobState.Printing && !job.Paused) { job.Paused = true; job.PausedAt = now; }
                return false;
            }
            if (job.Paused) { job.PageStarted += Math.Max(0, now - job.PausedAt); job.Paused = false; }
            if (job.State == JobState.Queued) {
                job.State = JobState.Printing;
                job.PageStarted = now;
                return false;
            }
            if (now - job.PageStarted < PageSeconds) { return false; }
            page = job.Printed;
            return true;
        }

        public void PagePrinted(PrintJob job, double now) {
            if (job == null || job.State != JobState.Printing) { return; }
            job.Printed++;
            job.PageStarted = now;
            job.Paused = false;
            if (job.Printed >= job.Pages.Length) {
                job.State = JobState.Done;
                job.Finished = now;
            }
        }

        public void Fail(PrintJob job, PcError error, double now) {
            if (job == null || !Active(job)) { return; }
            job.State = JobState.Failed;
            job.Error = error;
            job.Finished = now;
        }

        public float Progress(PrintJob j, double now) {
            if (j.Pages.Length == 0) { return 1; }
            float page = j.State == JobState.Printing ? (float)Math.Clamp(((j.Paused ? j.PausedAt : now) - j.PageStarted) / PageSeconds, 0, 1) : 0;
            return Math.Clamp((j.Printed + page) / j.Pages.Length, 0, 1);
        }

        public bool Prune(double now, bool force = false) {
            int before = jobs.Count;
            int retained = 0;
            for (int i = 0; i < jobs.Count; i++) {
                PrintJob job = jobs[i];
                if (!Active(job) && (force || now - job.Finished > KeepFinished)) { continue; }
                jobs[retained++] = job;
            }
            if (retained < jobs.Count) { jobs.RemoveRange(retained, jobs.Count - retained); }
            return jobs.Count < before;
        }

        public static string[] Paginate(string text, int chars, int lines) {
            var pages = new List<string>();
            int start = 0;
            while (start < text.Length || pages.Count == 0) {
                int end = start;
                int rows = 1;
                int lastBreak = -1;
                while (end < text.Length && end - start < chars) {
                    if (text[end] == '\n') {
                        if (rows >= lines) { break; }
                        rows++;
                        lastBreak = end;
                    }
                    end++;
                }
                if (end < text.Length && end - start >= chars && lastBreak > start) { end = lastBreak; }
                if (end < text.Length && char.IsLowSurrogate(text[end]) && end > start) { end--; }
                pages.Add(text.Substring(start, end - start).TrimEnd('\n'));
                start = end;
                if (start < text.Length && text[start] == '\n') { start++; }
                if (pages.Count > MaxPages * 4) { break; }
            }
            return pages.ToArray();
        }
    }
}
