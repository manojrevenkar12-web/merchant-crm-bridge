namespace Bridge.Emulator;

/// <summary>A row: column values with CLR types (string, int, long, decimal, DateTimeOffset, Guid, bool).</summary>
public sealed class Row(Dictionary<string, object?> values)
{
    public Dictionary<string, object?> Values { get; } = values;
    public long Version { get; set; }
    public Row Clone() => new(new Dictionary<string, object?>(Values, StringComparer.Ordinal)) { Version = Version };
}

/// <summary>
/// The tables in memory with unique alternate-key indexes. Every request runs in a transaction with an
/// undo log, so a failing plug-in or a failing operation in a $batch changeset rolls back everything the
/// transaction did, as in Dataverse.
/// </summary>
public sealed class Store(Metadata metadata)
{
    private readonly Dictionary<string, Dictionary<Guid, Row>> _tables = metadata.BySet.Keys.ToDictionary(k => k, _ => new Dictionary<Guid, Row>(), StringComparer.Ordinal);
    private readonly Dictionary<(string Set, string Key, string Values), Guid> _keyIndex = [];
    private long _version = 1_000_000;

    public Metadata Metadata { get; } = metadata;

    public SemaphoreSlim Lock { get; } = new(1, 1);

    public IEnumerable<Row> Rows(string set) => _tables[set].Values;

    public int Count(string set) => _tables[set].Count;

    public Row? Get(string set, Guid id) => _tables[set].GetValueOrDefault(id);

    public Row? FindByKey(string set, string[] attributes, IReadOnlyDictionary<string, object?> values)
    {
        var meta = Metadata.BySet[set];
        var key = meta.Keys.FirstOrDefault(k => k.Attributes.OrderBy(a => a, StringComparer.Ordinal).SequenceEqual(attributes.OrderBy(a => a, StringComparer.Ordinal), StringComparer.Ordinal))
            ?? throw DataverseException.Invalid($"No alternate key on {meta.LogicalName} has the columns {string.Join(",", attributes)}.");
        var index = (set, key.Name, KeyString(key, values));
        return _keyIndex.TryGetValue(index, out var id) ? _tables[set][id] : null;
    }

    public long NextVersion() => ++_version;

    public void Reset()
    {
        foreach (var t in _tables.Values) t.Clear();
        _keyIndex.Clear();
    }

    private static string KeyString(KeyMeta key, IReadOnlyDictionary<string, object?> values) =>
        string.Join("\u001f", key.Attributes.Select(a => Convert.ToString(values.GetValueOrDefault(a), System.Globalization.CultureInfo.InvariantCulture) ?? ""));

    public sealed class Transaction(Store store)
    {
        private readonly List<Action> _undo = [];

        public void Insert(string set, Guid id, Row row)
        {
            var meta = store.Metadata.BySet[set];
            if (store._tables[set].ContainsKey(id)) throw new DataverseException(412, DataverseException.DuplicateKey, "A record with the same id already exists.");
            foreach (var key in meta.Keys)
            {
                if (key.Attributes.Any(a => row.Values.GetValueOrDefault(a) is null)) continue;
                var index = (set, key.Name, KeyString(key, row.Values));
                if (store._keyIndex.ContainsKey(index))
                    throw new DataverseException(412, DataverseException.DuplicateKey, $"A record that has the attribute values {string.Join(", ", key.Attributes.Select(a => row.Values[a]))} already exists. The entity key {key.Name} requires that this set of attributes contains unique values.");
            }
            store._tables[set][id] = row;
            foreach (var key in meta.Keys)
            {
                if (key.Attributes.Any(a => row.Values.GetValueOrDefault(a) is null)) continue;
                var index = (set, key.Name, KeyString(key, row.Values));
                store._keyIndex[index] = id;
                _undo.Add(() => store._keyIndex.Remove(index));
            }
            _undo.Add(() => store._tables[set].Remove(id));
        }

        public void Update(string set, Guid id, Row current, IReadOnlyDictionary<string, object?> changes, long version)
        {
            var meta = store.Metadata.BySet[set];
            var before = current.Clone();
            var after = current.Clone();
            foreach (var (k, v) in changes) after.Values[k] = v;
            foreach (var key in meta.Keys)
            {
                if (!key.Attributes.Any(changes.ContainsKey)) continue;
                var oldIndex = (set, key.Name, KeyString(key, before.Values));
                var newIndex = (set, key.Name, KeyString(key, after.Values));
                if (oldIndex == newIndex) continue;
                if (key.Attributes.All(a => after.Values.GetValueOrDefault(a) is not null) && store._keyIndex.TryGetValue(newIndex, out var other) && other != id)
                    throw new DataverseException(412, DataverseException.DuplicateKey, $"The entity key {key.Name} requires unique values.");
            }
            foreach (var key in meta.Keys)
            {
                if (!key.Attributes.Any(changes.ContainsKey)) continue;
                var oldIndex = (set, key.Name, KeyString(key, before.Values));
                var newIndex = (set, key.Name, KeyString(key, after.Values));
                if (store._keyIndex.Remove(oldIndex)) _undo.Add(() => store._keyIndex[oldIndex] = id);
                if (key.Attributes.All(a => after.Values.GetValueOrDefault(a) is not null))
                {
                    store._keyIndex[newIndex] = id;
                    _undo.Add(() => store._keyIndex.Remove(newIndex));
                }
            }
            foreach (var (k, v) in changes) current.Values[k] = v;
            current.Version = version;
            _undo.Add(() =>
            {
                current.Values.Clear();
                foreach (var (k, v) in before.Values) current.Values[k] = v;
                current.Version = before.Version;
            });
        }

        public void Delete(string set, Guid id)
        {
            var meta = store.Metadata.BySet[set];
            var row = store._tables[set][id];
            store._tables[set].Remove(id);
            foreach (var key in meta.Keys)
            {
                var index = (set, key.Name, KeyString(key, row.Values));
                if (store._keyIndex.Remove(index)) _undo.Add(() => store._keyIndex[index] = id);
            }
            _undo.Add(() => store._tables[set][id] = row);
        }

        public void Rollback()
        {
            for (var i = _undo.Count - 1; i >= 0; i--) _undo[i]();
            _undo.Clear();
        }
    }
}
