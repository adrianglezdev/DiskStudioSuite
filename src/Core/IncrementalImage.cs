using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace DiskStudio.Core;

/// <summary>
/// Imágenes incrementales: después de una imagen completa (.img/.img.gz) solo se guardan los bloques de 1 MiB que han cambiado.
/// Cada imagen lleva al lado un archivo .blocks con el hash de cada bloque, que sirve de referencia para la siguiente.
/// Formato .dssinc: cabecera de 4 KiB + flujo gzip de registros [índice del bloque (8 bytes) + datos del bloque].
/// </summary>
public static class IncrementalImage
{
    public const int Block = 1 << 20;
    public const string Ext = ".dssinc";
    const int HeaderSize = 4096;
    static ReadOnlySpan<byte> Magic => "DSSINC01"u8;
    static ReadOnlySpan<byte> BlocksMagic => "DSSBLK01"u8;

    public class Header
    {
        public long DiskSize, ChangedBlocks;
        public DateTime Created;
        public string Parent = "";        // nombre de archivo de la imagen anterior (en la misma carpeta)
        public string DiskTitle = "";
    }

    public static bool IsIncremental(string path) => path.EndsWith(Ext, StringComparison.OrdinalIgnoreCase);
    public static string BlocksFile(string image) => image + ".blocks";

    // ───────────── Hash de bloques ─────────────
    public static ulong HashBlock(ReadOnlySpan<byte> data) => XxHash64.HashToUInt64(data);

    public static void WriteBlocks(string file, long diskSize, ulong[] hashes)
    {
        using var fs = File.Create(file);
        Span<byte> h = stackalloc byte[32];
        BlocksMagic.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], Block);
        BinaryPrimitives.WriteInt64LittleEndian(h[12..], diskSize);
        BinaryPrimitives.WriteInt64LittleEndian(h[20..], hashes.Length);
        fs.Write(h);
        var buf = new byte[hashes.Length * 8];
        for (int i = 0; i < hashes.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(i * 8), hashes[i]);
        fs.Write(buf);
    }

    public static ulong[] ReadBlocks(string file, out long diskSize)
    {
        var all = File.ReadAllBytes(file);
        if (all.Length < 32 || !all.AsSpan(0, 8).SequenceEqual(BlocksMagic)) throw new InvalidDataException("Archivo de bloques no válido: " + file);
        diskSize = BinaryPrimitives.ReadInt64LittleEndian(all.AsSpan(12));
        long n = BinaryPrimitives.ReadInt64LittleEndian(all.AsSpan(20));
        var r = new ulong[n];
        for (long i = 0; i < n; i++) r[i] = BinaryPrimitives.ReadUInt64LittleEndian(all.AsSpan(32 + (int)(i * 8)));
        return r;
    }

    /// <summary>Calcula los hashes de bloque de cualquier flujo (para imágenes completas antiguas sin .blocks).</summary>
    public static ulong[] HashStream(Stream s, long length, JobContext ctx = null, string what = "Analizando la imagen anterior")
    {
        long count = (length + Block - 1) / Block;
        var r = new ulong[count];
        var buf = new byte[Block];
        for (long i = 0; i < count; i++)
        {
            ctx?.Check();
            int n = ReadBlock(s, buf, (int)Math.Min(Block, length - i * Block));
            r[i] = HashBlock(buf.AsSpan(0, n));
            ctx?.SetCurrent($"{what}: {Fmt.Size(i * Block)} de {Fmt.Size(length)}");
        }
        return r;
    }

    static int ReadBlock(Stream s, byte[] buf, int want)
    {
        int n = 0, r;
        while (n < want && (r = s.Read(buf, n, want - n)) > 0) n += r;
        if (n < want) Array.Clear(buf, n, want - n);
        return want;
    }

    // ───────────── Cabecera ─────────────
    static byte[] EncodeHeader(Header h)
    {
        var b = new byte[HeaderSize];
        Magic.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), Block);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16), h.DiskSize);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), h.ChangedBlocks);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(32), h.Created.ToUniversalTime().Ticks);
        int off = 40;
        foreach (var s in new[] { h.Parent, h.DiskTitle })
        {
            var t = Encoding.UTF8.GetBytes(s ?? "");
            if (t.Length > 1000) t = t[..1000];
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off), (ushort)t.Length);
            t.CopyTo(b, off + 2);
            off += 2 + t.Length;
        }
        return b;
    }

    public static Header ReadHeader(Stream s)
    {
        var b = new byte[HeaderSize];
        s.ReadExactly(b);
        if (!b.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("No es una imagen incremental de Disk Studio Suite.");
        if (BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12)) != Block) throw new InvalidDataException("Tamaño de bloque no admitido.");
        var h = new Header
        {
            DiskSize = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(16)),
            ChangedBlocks = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(24)),
            Created = new DateTime(BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(32)), DateTimeKind.Utc).ToLocalTime()
        };
        int off = 40;
        string str() { int n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(off)); string r = Encoding.UTF8.GetString(b, off + 2, n); off += 2 + n; return r; }
        h.Parent = str(); h.DiskTitle = str();
        return h;
    }

    public static Header ReadHeader(string file) { using var fs = File.OpenRead(file); return ReadHeader(fs); }

    // ───────────── Crear ─────────────
    /// <summary>Lee el disco y escribe solo los bloques distintos de <paramref name="parent"/>. Devuelve los hashes nuevos.</summary>
    public static ulong[] Write(Stream disk, long diskSize, ulong[] parent, Stream output, Header header, JobContext ctx = null)
    {
        long count = (diskSize + Block - 1) / Block;
        if (parent.Length != count) throw new InvalidDataException("El disco ha cambiado de tamaño desde la imagen anterior: crea una imagen completa nueva.");
        long start = output.Position;
        output.Write(EncodeHeader(header));
        var hashes = new ulong[count];
        var buf = new byte[Block];
        Span<byte> idx = stackalloc byte[8];
        long changed = 0;
        using (var gz = new GZipStream(output, CompressionLevel.Fastest, true))
        {
            for (long i = 0; i < count; i++)
            {
                ctx?.Check();
                int n = ReadBlock(disk, buf, (int)Math.Min(Block, diskSize - i * Block));
                hashes[i] = HashBlock(buf.AsSpan(0, n));
                if (hashes[i] != parent[i])
                {
                    BinaryPrimitives.WriteInt64LittleEndian(idx, i);
                    gz.Write(idx);
                    gz.Write(buf, 0, Block);
                    changed++;
                }
                ctx?.AddBytes(n);
                ctx?.SetCurrent($"Leyendo {Fmt.Size(i * Block)} de {Fmt.Size(diskSize)} · {changed} bloques cambiados");
            }
        }
        long end = output.Position;
        header.ChangedBlocks = changed;
        output.Position = start;
        output.Write(EncodeHeader(header));
        output.Position = end;
        return hashes;
    }

    public static TransferJob CreateJob(PhysicalDisk disk, string parent, string file) => new()
    {
        Title = $"Imagen incremental del disco {disk.Number}",
        Subtitle = $"{disk.Title} → {Path.GetFileName(file)} (basada en {Path.GetFileName(parent)})",
        Glyph = Icons.Image,
        Work = ctx => Task.Run(() =>
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(parent)), Path.GetDirectoryName(Path.GetFullPath(file)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Guarda la imagen incremental en la misma carpeta que la imagen anterior.");
            ulong[] prev;
            long prevSize;
            if (File.Exists(BlocksFile(parent))) prev = ReadBlocks(BlocksFile(parent), out prevSize);
            else
            {
                if (IsIncremental(parent)) throw new FileNotFoundException("Falta el archivo de bloques de la imagen anterior.", BlocksFile(parent));
                using var ps = VirtualDisk.OpenSource(parent, out prevSize, out var cl);
                try { prev = HashStream(ps, prevSize, ctx); WriteBlocks(BlocksFile(parent), prevSize, prev); }
                finally { cl(); }
            }
            using var src = Native.OpenRaw(disk.Device, false);
            long len = Native.GetDeviceLength(src);
            if (len != prevSize) throw new InvalidDataException($"El disco mide {Fmt.Size(len)} y la imagen anterior {Fmt.Size(prevSize)}: no corresponden al mismo disco.");
            ctx.SetTotal(len);
            using var input = new FileStream(src, FileAccess.Read, 0, false);
            ulong[] hashes;
            var header = new Header { DiskSize = len, Created = DateTime.Now, Parent = Path.GetFileName(parent), DiskTitle = disk.Title };
            using (var output = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None, Block))
                hashes = Write(input, len, prev, output, header, ctx);
            WriteBlocks(BlocksFile(file), len, hashes);
            Log.Ok($"Imagen incremental creada: {file} · {header.ChangedBlocks} bloques cambiados ({Fmt.Size(header.ChangedBlocks * Block)})");
        })
    };

    /// <summary>Hashes para la imagen completa recién creada (la base de futuras incrementales).</summary>
    public static void SaveBlocksForFull(string image, long size, ulong[] hashes) => WriteBlocks(BlocksFile(image), size, hashes);

    // ───────────── Restaurar ─────────────
    /// <summary>Cadena de archivos desde la imagen completa hasta <paramref name="file"/>.</summary>
    public static List<string> Chain(string file)
    {
        var chain = new List<string> { file };
        string cur = file;
        for (int guard = 0; IsIncremental(cur); guard++)
        {
            if (guard > 500) throw new InvalidDataException("La cadena de imágenes es demasiado larga o circular.");
            var h = ReadHeader(cur);
            string parent = Path.Combine(Path.GetDirectoryName(cur)!, h.Parent);
            if (!File.Exists(parent)) throw new FileNotFoundException($"Falta la imagen anterior «{h.Parent}» en la misma carpeta.", parent);
            chain.Insert(0, parent);
            cur = parent;
        }
        return chain;
    }

    /// <summary>Flujo con el contenido final del disco: imagen completa + bloques de cada incremental (la más reciente gana).</summary>
    public static Stream OpenChain(string file, out long length, out Action cleanup)
    {
        var chain = Chain(file);
        var baseS = VirtualDisk.OpenSource(chain[0], out length, out cleanup);
        var incs = new List<Stream>();
        foreach (var f in chain.Skip(1))
        {
            var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, Block);
            var h = ReadHeader(fs);
            if (h.DiskSize != length) throw new InvalidDataException($"«{Path.GetFileName(f)}» no corresponde al mismo disco que la imagen completa.");
            incs.Add(new GZipStream(fs, CompressionMode.Decompress));
        }
        return new ChainStream(baseS, incs, length);
    }

    sealed class ChainStream : Stream
    {
        readonly Stream baseS;
        readonly List<Stream> incs;           // de la más antigua a la más reciente
        readonly long[] next;                 // índice del próximo bloque de cada incremental (-1 = terminado)
        readonly List<byte[]> pending;
        readonly long length;
        readonly byte[] block = new byte[Block];
        long blockIndex = -1, pos;

        public ChainStream(Stream b, List<Stream> i, long len)
        {
            baseS = b; incs = i; length = len;
            next = new long[i.Count];
            pending = i.Select(_ => new byte[Block]).ToList();
            for (int k = 0; k < i.Count; k++) Advance(k);
        }

        void Advance(int k)
        {
            Span<byte> idx = stackalloc byte[8];
            int n = 0, r;
            while (n < 8 && (r = incs[k].Read(idx[n..])) > 0) n += r;
            if (n < 8) { next[k] = -1; return; }
            next[k] = BinaryPrimitives.ReadInt64LittleEndian(idx);
            int got = 0;
            while (got < Block && (r = incs[k].Read(pending[k], got, Block - got)) > 0) got += r;
            if (got < Block) throw new InvalidDataException("Imagen incremental incompleta o dañada.");
        }

        void Load(long i)
        {
            ReadBlock(baseS, block, (int)Math.Min(Block, length - i * Block));
            for (int k = 0; k < incs.Count; k++)
            {
                while (next[k] >= 0 && next[k] < i) Advance(k);
                if (next[k] == i) { Buffer.BlockCopy(pending[k], 0, block, 0, Block); Advance(k); }
            }
            blockIndex = i;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (pos >= length) return 0;
            long i = pos / Block;
            if (i != blockIndex) Load(i);
            int inBlock = (int)(pos % Block);
            int n = (int)Math.Min(Math.Min(count, Block - inBlock), length - pos);
            Buffer.BlockCopy(block, inBlock, buffer, offset, n);
            pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { baseS.Dispose(); foreach (var s in incs) s.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
