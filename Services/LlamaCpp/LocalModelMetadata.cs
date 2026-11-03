using System.Collections.Concurrent;
using System.Text;

namespace AgentsHub.Services.LlamaCpp;

/// <summary>
/// What a GGUF header says about attention, which is all that is needed to size a KV cache.
///
/// The fit estimate used to count the weights and nothing else, so a model with a 262k context was
/// advertised as fitting a card it could never load: at that context the cache is larger than the
/// file. These four numbers turn that guess into arithmetic.
/// </summary>
public sealed record LocalModelMetadata(
    int Layers,
    int HeadCount,
    int HeadCountKv,
    int KeyLength,
    int ValueLength,
    string SizeLabel = "",
    string Name = "")
{
    /// <summary>False for a header that did not carry attention sizes; callers fall back to weights only.</summary>
    public bool Usable => Layers > 0 && HeadCountKv > 0 && KeyLength > 0 && ValueLength > 0;

    /// <summary>Bytes one token of context costs across every layer, K and V together.</summary>
    public double KvBytesPerToken(string? cacheType)
    {
        if (!Usable) return 0;

        return Layers * (double)HeadCountKv * (KeyLength + ValueLength) * BytesPerElement(cacheType);
    }

    /// <summary>
    /// Bytes per cached value for a llama.cpp cache type. The quantised types hold a small block per
    /// 32 values, so their effective size is the block size over 32, not a whole byte.
    /// </summary>
    public static double BytesPerElement(string? cacheType) => (cacheType ?? "f16").ToLowerInvariant() switch
    {
        "f32" => 4,
        "bf16" or "f16" => 2,
        "q8_0" => 1.0625,
        "q5_1" => 0.75,
        "q5_0" => 0.6875,
        "q4_1" => 0.625,
        "q4_0" => 0.5625,
        _ => 2
    };
}

/// <summary>
/// Reads the metadata block at the top of a GGUF file and stops before the tensors.
///
/// This is a header read, not a model load: no llama.cpp binary, no GPU, no state. Every failure
/// returns an empty record, because a file that cannot be parsed only means the old estimate stands.
/// </summary>
public static class LocalModelMetadataReader
{
    private const uint GgufMagic = 0x46554747;
    private const int MaxStringBytes = 4 * 1024 * 1024;

    /// <summary>Metadata is a few megabytes at most; this stops a corrupt length from reading the file.</summary>
    private const long ReadBudget = 64L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(long Length, DateTime Modified, LocalModelMetadata Metadata);

    public static LocalModelMetadata For(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return Empty;

            if (Cache.TryGetValue(path, out var cached)
                && cached.Length == file.Length
                && cached.Modified == file.LastWriteTimeUtc)
            {
                return cached.Metadata;
            }

            var metadata = Parse(path);
            Cache[path] = new Entry(file.Length, file.LastWriteTimeUtc, metadata);
            return metadata;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Empty;
        }
    }

    public static LocalModelMetadata Empty { get; } = new(0, 0, 0, 0, 0);

    private static LocalModelMetadata Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        if (stream.Length < 32) return Empty;
        if (reader.ReadUInt32() != GgufMagic) return Empty;

        uint version = reader.ReadUInt32();
        if (version is < 2 or > 3) return Empty;

        _ = reader.ReadUInt64(); // tensor count: the tensors sit after the metadata block
        ulong pairs = reader.ReadUInt64();

        int layers = 0, heads = 0, headsKv = 0, keyLength = 0, valueLength = 0, embedding = 0;
        string sizeLabel = string.Empty, name = string.Empty;

        for (ulong i = 0; i < pairs; i++)
        {
            if (stream.Position > ReadBudget) break;

            string key = ReadString(reader);
            if (key.Length == 0) break;

            var type = (GgufType)reader.ReadUInt32();

            // The two text fields that say which model this is. A file renamed by hand still carries
            // them, so the model's size survives a name that no longer mentions it.
            if (key is "general.size_label" or "general.name")
            {
                if (type != GgufType.String)
                {
                    _ = ReadNumber(reader, type, out _);
                    continue;
                }

                string value = type == GgufType.String ? ReadString(reader) : string.Empty;
                if (key == "general.size_label") sizeLabel = value;
                else name = value;
                continue;
            }

            bool wanted = key.EndsWith(".block_count", StringComparison.Ordinal)
                || key.EndsWith(".attention.head_count", StringComparison.Ordinal)
                || key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal)
                || key.EndsWith(".attention.key_length", StringComparison.Ordinal)
                || key.EndsWith(".attention.value_length", StringComparison.Ordinal)
                || key.EndsWith(".embedding_length", StringComparison.Ordinal);

            var number = ReadNumber(reader, type, out int max);

            if (!wanted) continue;

            if (key.EndsWith(".block_count", StringComparison.Ordinal)) layers = number;
            else if (key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal)) headsKv = max > 0 ? max : number;
            else if (key.EndsWith(".attention.head_count", StringComparison.Ordinal)) heads = max > 0 ? max : number;
            else if (key.EndsWith(".attention.key_length", StringComparison.Ordinal)) keyLength = number;
            else if (key.EndsWith(".attention.value_length", StringComparison.Ordinal)) valueLength = number;
            else if (key.EndsWith(".embedding_length", StringComparison.Ordinal)) embedding = number;
        }

        // Transformers that omit the explicit head sizes use the usual head_dim of embedding / heads.
        if (heads > 0 && embedding > 0)
        {
            if (keyLength == 0) keyLength = embedding / heads;
            if (valueLength == 0) valueLength = keyLength;
        }

        if (headsKv == 0) headsKv = heads;

        return new LocalModelMetadata(layers, heads, headsKv, keyLength, valueLength, sizeLabel, name);
    }

    private enum GgufType : uint
    {
        UInt8 = 0, Int8 = 1, UInt16 = 2, Int16 = 3,
        UInt32 = 4, Int32 = 5, Float32 = 6, Bool = 7,
        String = 8, Array = 9,
        UInt64 = 10, Int64 = 11, Float64 = 12
    }

    private static string ReadString(BinaryReader reader)
    {
        ulong length = reader.ReadUInt64();
        if (length == 0) return string.Empty;
        if (length > MaxStringBytes) throw new InvalidDataException("GGUF string is too long.");

        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    /// <summary>
    /// Reads one value and consumes it, whatever its type. An array answers its first member and the
    /// largest one through <paramref name="max"/>: grouped-query models list a key/value head count
    /// per layer, and the largest is what decides the cache size.
    /// </summary>
    private static int ReadNumber(BinaryReader reader, GgufType type, out int max)
    {
        max = 0;

        if (type == GgufType.String)
        {
            _ = ReadString(reader);
            return 0;
        }

        if (type != GgufType.Array)
        {
            int scalar = ReadScalar(reader, type);
            max = scalar;
            return scalar;
        }

        var element = (GgufType)reader.ReadUInt32();
        ulong count = reader.ReadUInt64();
        int first = 0;

        for (ulong i = 0; i < count; i++)
        {
            int value = ReadNumber(reader, element, out int nested);
            if (i == 0) first = value;
            if (value > max) max = value;
            if (nested > max) max = nested;
        }

        return first;
    }

    private static int ReadScalar(BinaryReader reader, GgufType type) => type switch
    {
        GgufType.UInt8 => reader.ReadByte(),
        GgufType.Int8 => reader.ReadSByte(),
        GgufType.UInt16 => reader.ReadUInt16(),
        GgufType.Int16 => reader.ReadInt16(),
        GgufType.UInt32 => (int)Math.Min(reader.ReadUInt32(), (uint)int.MaxValue),
        GgufType.Int32 => reader.ReadInt32(),
        GgufType.Float32 => (int)reader.ReadSingle(),
        GgufType.Bool => reader.ReadByte(),
        GgufType.UInt64 => (int)Math.Min(reader.ReadUInt64(), int.MaxValue),
        GgufType.Int64 => (int)Math.Clamp(reader.ReadInt64(), int.MinValue, int.MaxValue),
        GgufType.Float64 => (int)reader.ReadDouble(),
        _ => 0
    };
}
