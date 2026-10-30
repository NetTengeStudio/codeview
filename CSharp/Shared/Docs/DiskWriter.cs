using System;
using System.Collections.Generic;
using System.Threading;

namespace ScpItdMod.Docs {
    // Owns every disk write of one store on a single background thread. The game thread never waits
    // on the disk: it posts a snapshot, and reads outcomes later. A newer snapshot of a file replaces an
    // unwritten older one, so the disk always receives the latest state and no write is queued twice.
    public sealed class DiskWriter : IDisposable {
        public readonly struct Outcome {
            public readonly string File;
            public readonly long Generation;
            public readonly string Root;
            public readonly Exception Error;

            public Outcome(string file, long generation, string root, Exception error) {
                File = file;
                Generation = generation;
                Root = root;
                Error = error;
            }
        }

        private struct Job {
            public byte[] Data;
            public long Generation;
        }

        private readonly Func<string, byte[], string> save;
        private readonly object gate = new object();
        private readonly Dictionary<string, Job> waiting = new Dictionary<string, Job>();
        private readonly List<KeyValuePair<string, Job>> batch = new List<KeyValuePair<string, Job>>();
        private readonly Queue<Outcome> outcomes = new Queue<Outcome>();
        private readonly Thread worker;
        private bool working, stopping;

        // save writes one file and returns the folder it landed in. It runs only on the background thread.
        public DiskWriter(Func<string, byte[], string> save) {
            this.save = save ?? throw new ArgumentNullException(nameof(save));
            worker = new Thread(Run) { IsBackground = true, Name = "scpitd-disk" };
            worker.Start();
        }

        public void Post(string file, byte[] data, long generation) {
            lock (gate) {
                waiting[file] = new Job { Data = data, Generation = generation };
                Monitor.PulseAll(gate);
            }
        }

        public bool TryTake(out Outcome outcome) {
            lock (gate) {
                if (outcomes.Count == 0) {
                    outcome = default;
                    return false;
                }
                outcome = outcomes.Dequeue();
                return true;
            }
        }

        // Blocks until nothing is waiting or being written. Used only when the store shuts down.
        public void WaitIdle() {
            lock (gate) {
                while (waiting.Count > 0 || working) { Monitor.Wait(gate); }
            }
        }

        public void Dispose() {
            lock (gate) {
                stopping = true;
                Monitor.PulseAll(gate);
            }
            worker.Join();
        }

        private void Run() {
            while (true) {
                lock (gate) {
                    while (waiting.Count == 0 && !stopping) {
                        Monitor.PulseAll(gate);
                        Monitor.Wait(gate);
                    }
                    if (waiting.Count == 0) { return; }
                    working = true;
                    foreach (KeyValuePair<string, Job> pair in waiting) { batch.Add(pair); }
                    waiting.Clear();
                }
                for (int i = 0; i < batch.Count; i++) {
                    KeyValuePair<string, Job> pair = batch[i];
                    Outcome outcome;
                    try {
                        outcome = new Outcome(pair.Key, pair.Value.Generation, save(pair.Key, pair.Value.Data), null);
                    } catch (Exception error) {
                        outcome = new Outcome(pair.Key, pair.Value.Generation, null, error);
                    }
                    lock (gate) { outcomes.Enqueue(outcome); }
                }
                lock (gate) {
                    batch.Clear();
                    working = false;
                    Monitor.PulseAll(gate);
                }
            }
        }
    }
}
