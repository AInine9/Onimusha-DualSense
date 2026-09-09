using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OnimushaDualSense;

// Exact float32 storage. Only the control thread reads this store; active mixer
// voices keep their own arrays, so eviction cannot interrupt playback.
sealed class SampleStore : IDictionary<string, float[]>, IDisposable
{
    sealed class Entry(long offset, int length)
    {
        public readonly long Offset = offset;
        public readonly int Length = length;
        public float[]? Data;
        public LinkedListNode<Entry>? Node;
        public string? Path;
        public string Hash = "";
    }
    readonly Dictionary<string, Entry> entries = [];
    readonly ConditionalWeakTable<float[], Entry> identities = new();
    readonly LinkedList<Entry> recent = new();
    FileStream? file;
    readonly Dictionary<string, Entry> waveEntries = [];
    readonly long budget;
    long bytes;
    public long ResidentBytes => bytes;
    public long StoredBytes => file?.Length ?? waveEntries.Values.Sum(e => (long)e.Length * 4);
    public SampleStore(long budgetBytes = 64L * 1024 * 1024)
    {
        budget = Math.Max(0, budgetBytes);
    }
    FileStream Scratch => file ??= new FileStream(Path.Combine(Path.GetTempPath(), "onimusha-waves-" + Guid.NewGuid().ToString("N") + ".tmp"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
    public void RegisterWave(string key, string path, int length, string hash)
    {
        if (!waveEntries.TryGetValue(path, out var entry))
            waveEntries[path] = entry = new Entry(0, length) { Path = path, Hash = hash };
        entries.Add(key, entry);
    }
    void Cache(Entry entry, float[] data)
    {
        if (entry.Node != null) recent.Remove(entry.Node);
        else bytes += (long)data.Length * 4;
        entry.Data = data; entry.Node = recent.AddLast(entry);
        while (bytes > budget && recent.First is { } first)
        {
            var old = first.Value; recent.RemoveFirst(); old.Node = null;
            bytes -= (long)old.Length * 4; old.Data = null;
        }
    }
    public float[] this[string key]
    {
        get
        {
            var entry = entries[key];
            var data = entry.Data;
            if (data == null)
            {
                if (entry.Path != null)
                {
                    data = PreparedWaves.ReadWave(entry.Path, entry.Length, entry.Hash);
                }
                else
                {
                    data = new float[entry.Length]; Scratch.Position = entry.Offset;
                    Scratch.ReadExactly(MemoryMarshal.AsBytes(data.AsSpan()));
                }
                identities.Add(data, entry);
            }
            Cache(entry, data); return data;
        }
        set
        {
            if (!identities.TryGetValue(value, out var entry))
            {
                entry = new Entry(Scratch.Length, value.Length); Scratch.Position = Scratch.Length;
                Scratch.Write(MemoryMarshal.AsBytes(value.AsSpan())); identities.Add(value, entry);
            }
            entries[key] = entry; Cache(entry, value);
        }
    }
    public bool TryGetValue(string key, out float[] value)
    {
        if (!entries.ContainsKey(key)) { value = null!; return false; }
        value = this[key]; return true;
    }
    public bool ContainsKey(string key) => entries.ContainsKey(key);
    public int Count => entries.Count;
    public ICollection<string> Keys => entries.Keys;
    public ICollection<float[]> Values => entries.Keys.Select(key => this[key]).ToArray();
    public bool IsReadOnly => false;
    public void Add(string key, float[] value) { if (ContainsKey(key)) throw new ArgumentException("Duplicate sample"); this[key] = value; }
    public void Add(KeyValuePair<string, float[]> item) => Add(item.Key, item.Value);
    public bool Remove(string key) => entries.Remove(key);
    public void Clear() { entries.Clear(); identities.Clear(); recent.Clear(); waveEntries.Clear(); bytes = 0; file?.SetLength(0); }
    public bool Contains(KeyValuePair<string, float[]> item) => TryGetValue(item.Key, out var value) && ReferenceEquals(value, item.Value);
    public bool Remove(KeyValuePair<string, float[]> item) => Contains(item) && Remove(item.Key);
    public void CopyTo(KeyValuePair<string, float[]>[] array, int index) { foreach (var item in this) array[index++] = item; }
    public IEnumerator<KeyValuePair<string, float[]>> GetEnumerator() { foreach (var key in entries.Keys) yield return new(key, this[key]); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() => file?.Dispose();
    // One collection before opening audio, never during live playback.
    public static void FinishLoading()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }
}
