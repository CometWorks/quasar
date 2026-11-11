using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Quasar.Services;

// SE-1349: a cluster's backups share one content-addressed object store. A Host archive is kept as a
// skeleton (its tar headers and small files, gzipped) plus the objects its larger files are, so a backup
// adds only the files no other backup of the cluster has. The skeleton turns back into the very bytes the
// Host sent: the archive keeps its SHA-256, and the Host protocol and restore do not change. Bytes that do
// not parse as tar stay in the skeleton as they are; they only miss the deduplication.
internal static class ClusterBackupStore
{
    internal const string Objects = ".objects";
    internal const string SkeletonSuffix = ".skeleton";
    internal const string ReferencesSuffix = ".objects";
    // Smaller files stay in the skeleton: an object costs a file and a disk block.
    internal const int InlineBytes = 4096;
    // Files up to this size are hashed in memory first, so an object the store has is not written again.
    internal const int BufferedBytes = 64 * 1024 * 1024;
    private const byte Raw = 1, Object = 2;

    internal static string ObjectPath(string clusterRoot, string sha256) => Path.Combine(clusterRoot, Objects, sha256[..2], sha256);

    /// <summary>Stores the archive read from <paramref name="archive"/> as <paramref name="path"/> plus the
    /// skeleton suffix, and lists the objects it refers to next to it. Returns the archive's length and SHA-256.</summary>
    internal static (long Bytes, string Sha256) Write(string clusterRoot, string path, Stream archive, int buffered = BufferedBytes)
    {
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var input = new HashingReader(archive, whole);
        var references = new SortedSet<string>(StringComparer.Ordinal);
        using (var file = new FileStream(path + SkeletonSuffix, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using (var skeleton = new BinaryWriter(new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true)))
                WriteEntries(clusterRoot, input, skeleton, references, buffered);
            file.Flush(true);
        }
        File.WriteAllLines(path + ReferencesSuffix, references);
        return (input.Total, Convert.ToHexString(whole.GetHashAndReset()).ToLowerInvariant());
    }

    private static void WriteEntries(string clusterRoot, HashingReader input, BinaryWriter skeleton, SortedSet<string> references, int buffered)
    {
        byte[] header = new byte[512];
        long? paxSize = null;
        while (true)
        {
            int got = input.Read(header);
            WriteRaw(skeleton, header.AsSpan(0, got));
            if (got < header.Length) return;
            if (header.All(b => b == 0) || !TryParse(header, out char type, out long size))
            {
                input.CopyRest(skeleton); // The end blocks, or no tar after all.
                return;
            }
            size = paxSize ?? size;
            paxSize = null;
            long padded = (size + 511) / 512 * 512;
            if (type is 'x' or 'g' or 'L' or 'K')
            {
                if (padded > BufferedBytes || !WriteBuffered(input, skeleton, padded, out byte[] data)) { input.CopyRest(skeleton); return; }
                if (type == 'x') paxSize = PaxSize(data.AsSpan(0, (int)size));
                continue;
            }
            // The Host's snapshot.json differs in every backup; in the skeleton it is at least compressed.
            if (type is '0' or '\0' or '7' && size >= InlineBytes && !IsManifest(header))
            {
                string? sha = StoreObject(clusterRoot, input, skeleton, size, buffered);
                if (sha is null) return; // The archive ended inside the file; its bytes are in the skeleton.
                references.Add(sha);
                skeleton.Write(Object);
                skeleton.Write(Convert.FromHexString(sha));
                skeleton.Write(size);
                if (!WriteBuffered(input, skeleton, padded - size, out _)) return;
            }
            else if (padded > BufferedBytes || !WriteBuffered(input, skeleton, padded, out _)) { input.CopyRest(skeleton); return; }
        }
    }

    /// <summary>The archive's bytes: the Host's tar file when it is kept whole (backups before SE-1349),
    /// else the skeleton with its objects.</summary>
    internal static Stream Open(string clusterRoot, string path) =>
        File.Exists(path) ? File.OpenRead(path) : new SkeletonReader(clusterRoot, path + SkeletonSuffix);

    internal static bool Exists(string path) => File.Exists(path) || File.Exists(path + SkeletonSuffix);

    // Removes the objects no backup of the cluster refers to, the staging copy of a running capture included.
    // Callers hold the cluster's store lock, so no capture adds a reference meanwhile. Returns the bytes freed.
    internal static long Collect(string clusterRoot)
    {
        string objects = Path.Combine(clusterRoot, Objects);
        if (!Directory.Exists(objects)) return 0;
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (string backup in Directory.EnumerateDirectories(clusterRoot))
            if (backup != objects)
                foreach (string list in Directory.EnumerateFiles(backup, "*" + ReferencesSuffix))
                    referenced.UnionWith(File.ReadLines(list));
        long freed = 0;
        foreach (string file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories))
            if (!referenced.Contains(Path.GetFileName(file)))
            {
                freed += new FileInfo(file).Length;
                File.Delete(file);
            }
        return freed;
    }

    // Null when the archive ends inside the file: what was read is then in the skeleton as raw bytes.
    private static string? StoreObject(string clusterRoot, HashingReader input, BinaryWriter skeleton, long size, int buffered)
    {
        if (size <= buffered)
        {
            byte[] data = new byte[size];
            int got = input.Read(data);
            if (got < data.Length) { WriteRaw(skeleton, data.AsSpan(0, got)); return null; }
            string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            string target = ObjectPath(clusterRoot, sha);
            if (!File.Exists(target)) Publish(target, temporary => File.WriteAllBytes(temporary, data));
            return sha;
        }
        string directory = Path.Combine(clusterRoot, Objects);
        Directory.CreateDirectory(directory);
        string incoming = Path.Combine(directory, "incoming-" + Guid.NewGuid().ToString("N"));
        try
        {
            long left = size;
            string sha;
            using (var output = new FileStream(incoming, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[1024 * 1024];
                while (left > 0)
                {
                    int got = input.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, left)));
                    if (got == 0) break;
                    hash.AppendData(buffer, 0, got);
                    output.Write(buffer, 0, got);
                    left -= got;
                }
                if (left > 0)
                {
                    output.Position = 0;
                    while (output.Read(buffer) is var count and > 0) WriteRaw(skeleton, buffer.AsSpan(0, count));
                    return null;
                }
                output.Flush(true);
                sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            string target = ObjectPath(clusterRoot, sha);
            if (!File.Exists(target)) Publish(target, temporary => File.Move(incoming, temporary));
            return sha;
        }
        finally { if (File.Exists(incoming)) File.Delete(incoming); }
    }

    // An object appears under its name only once it is complete and read-only.
    private static void Publish(string target, Action<string> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + ".incoming-" + Guid.NewGuid().ToString("N");
        try
        {
            write(temporary);
            using (var file = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite)) file.Flush(true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead);
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // False when the archive ended first; what was read is in the skeleton either way.
    private static bool WriteBuffered(HashingReader input, BinaryWriter skeleton, long count, out byte[] data)
    {
        data = new byte[count];
        int got = input.Read(data);
        WriteRaw(skeleton, data.AsSpan(0, got));
        return got == data.Length;
    }

    private static void WriteRaw(BinaryWriter skeleton, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        skeleton.Write(Raw);
        skeleton.Write((long)data.Length);
        skeleton.Write(data);
    }

    private static bool IsManifest(byte[] header) =>
        header.AsSpan(0, 14).SequenceEqual("snapshot.json\0"u8) && header[345] == 0;

    // A ustar or GNU header whose checksum holds. Size is octal, or base-256 when the field's top bit is set.
    private static bool TryParse(byte[] header, out char type, out long size)
    {
        type = (char)header[156];
        size = 0;
        long sum = 0;
        for (int i = 0; i < header.Length; i++) sum += i is >= 148 and < 156 ? ' ' : header[i];
        if (!TryOctal(header.AsSpan(148, 8), out long recorded) || recorded != sum) return false;
        var field = header.AsSpan(124, 12);
        if ((field[0] & 0x80) == 0) return TryOctal(field, out size);
        if ((field[0] & 0x7f) != 0) return false;
        foreach (byte b in field[1..])
        {
            if (size > long.MaxValue >> 8) return false;
            size = size << 8 | b;
        }
        return true;
    }

    private static bool TryOctal(ReadOnlySpan<byte> field, out long value)
    {
        value = 0;
        int i = 0;
        while (i < field.Length && field[i] == ' ') i++;
        int digits = 0;
        for (; i < field.Length && field[i] is >= (byte)'0' and <= (byte)'7'; i++, digits++)
        {
            if (value > long.MaxValue >> 3) return false;
            value = value << 3 | (long)(field[i] - '0');
        }
        for (; i < field.Length; i++) if (field[i] is not (0 or (byte)' ')) return false;
        return digits > 0;
    }

    // A pax header's "size" record overrides the next entry's size field (files of 8 GiB and more).
    private static long? PaxSize(ReadOnlySpan<byte> records)
    {
        foreach (string record in Encoding.UTF8.GetString(records).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int space = record.IndexOf(' ');
            if (space > 0 && record.AsSpan(space + 1).StartsWith("size=", StringComparison.Ordinal)
                && long.TryParse(record.AsSpan(space + 1 + "size=".Length), out long size) && size >= 0)
                return size;
        }
        return null;
    }

    // Reads the archive once, hashing every byte that passes.
    private sealed class HashingReader(Stream source, IncrementalHash hash)
    {
        internal long Total { get; private set; }

        // Fills the buffer unless the archive ends first; returns how much it read.
        internal int Read(Span<byte> buffer)
        {
            int at = 0;
            while (at < buffer.Length && source.Read(buffer[at..]) is var read and > 0) at += read;
            hash.AppendData(buffer[..at]);
            Total += at;
            return at;
        }

        internal void CopyRest(BinaryWriter skeleton)
        {
            byte[] buffer = new byte[1024 * 1024];
            while (Read(buffer) is var read and > 0) WriteRaw(skeleton, buffer.AsSpan(0, read));
        }
    }

    // Reads a skeleton back as the archive's bytes, one record at a time.
    private sealed class SkeletonReader(string clusterRoot, string path) : Stream
    {
        private readonly BinaryReader _records = new(new GZipStream(File.OpenRead(path), CompressionMode.Decompress));
        private Stream? _object;
        private long _left;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            while (_left == 0)
            {
                _object?.Dispose();
                _object = null;
                int kind = _records.BaseStream.ReadByte();
                if (kind < 0) return 0;
                if (kind == Object)
                {
                    string sha = Convert.ToHexString(_records.ReadBytes(32)).ToLowerInvariant();
                    _left = _records.ReadInt64();
                    string path = ObjectPath(clusterRoot, sha);
                    if (!File.Exists(path)) throw new InvalidDataException($"Backup object {sha} is missing.");
                    _object = File.OpenRead(path);
                    if (_object.Length != _left) throw new InvalidDataException($"Backup object {sha} has the wrong size.");
                }
                else if (kind == Raw) _left = _records.ReadInt64();
                else throw new InvalidDataException("Invalid backup skeleton.");
            }
            var wanted = buffer[..(int)Math.Min(buffer.Length, _left)];
            int read = _object is null ? _records.BaseStream.Read(wanted) : _object.Read(wanted);
            if (read == 0) throw new InvalidDataException("A backup skeleton or object ends early.");
            _left -= read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _object?.Dispose(); _records.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
