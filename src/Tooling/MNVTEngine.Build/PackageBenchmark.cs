using System.Diagnostics;
using System.Globalization;
using System.Text;
using MNVTEngine.IO;
using MNVTEngine.IO.Packages;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build;

/// <summary>
/// Measures reading a game's content from its packages against the same files as plain files on disk (the development
/// path), and repacks the content with every compression profile (<c>mnvt package benchmark</c>). Both sides are read
/// through the virtual file system after a warm-up pass, so the OS file cache is warm for both: the difference is the
/// cost of the package format itself (lookup, hashing, decryption, decompression). Decoding images or audio is the
/// same on both paths and not included.
/// </summary>
public static class PackageBenchmark
{
    /// <param name="dataDirectory">An exported game's data folder (or a <c>mnvt package build</c> output).</param>
    /// <param name="options">Keys and trust to open the packages.</param>
    /// <param name="repack">Also repack the content with each compression profile (slow: Small compresses at a few MB/s).</param>
    public static string Run(string dataDirectory, PackageOpenOptions options, bool repack = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        var text = new StringBuilder();
        var manifest = GamePackage.Load(dataDirectory);
        var scratch = Directory.CreateTempSubdirectory("mnvt-bench-").FullName;
        try
        {
            // Open (startup cost) — best of 5.
            var open = Best(5, () =>
            {
                using var content = GameContent.Open(dataDirectory, manifest.PackageFiles, options);
            });

            using var packaged = GameContent.Open(dataDirectory, manifest.PackageFiles, options);
            var files = packaged.Entries.ToList();
            var plainFolder = Path.Combine(scratch, "plain");
            foreach (var file in files)
            {
                var target = Path.Combine(plainFolder, file.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, packaged.Read(file));
            }

            using var physical = new VirtualFileSystem();
            physical.Mount(new PhysicalFileSystemProvider(plainFolder, "files on disk"));
            var total = files.Sum(f => new FileInfo(Path.Combine(plainFolder, f)).Length);
            Line($"Content: {files.Count} files, {Mb(total)} in {manifest.PackageFiles.Count} packages ({Mb(manifest.PackageFiles.Sum(p => new FileInfo(Path.Combine(dataDirectory, p)).Length))} on disk)");
            Line($"Open all packages (header, index, signature):   {Ms(open)}");
            Line("");
            Line($"{"Read (best of 3)",-34} {"packages",12} {"plain files",12} {"overhead",9}");

            foreach (var group in files.GroupBy(Category).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Compare($"{group.Key} ({group.Count()} files, {Mb(group.Sum(f => new FileInfo(Path.Combine(plainFolder, f)).Length))})", vfs => ReadAll(vfs, group));
            }

            Compare("everything, one thread", vfs => ReadAll(vfs, files));
            Compare($"everything, {Environment.ProcessorCount} threads", vfs => Parallel.ForEach(files, f => vfs.ReadAllBytes(f)));
            var random = new Random(1);
            var large = files.OrderByDescending(f => new FileInfo(Path.Combine(plainFolder, f)).Length).First();
            var largeLength = new FileInfo(Path.Combine(plainFolder, large)).Length;
            var offsets = Enumerable.Range(0, 2000).Select(_ => random.NextInt64(Math.Max(1, largeLength - 4096))).ToArray();
            Compare($"2000 random 4 KB reads ({Path.GetFileName(large)})", vfs => RandomReads(vfs, large, offsets));
            Compare("10000 lookups (exists + size)", vfs =>
            {
                for (var i = 0; i < 10_000; i++)
                {
                    vfs.TryGetFile(files[i % files.Count], out _);
                }
            });

            var before = GC.GetTotalAllocatedBytes(precise: true);
            ReadAll(packaged.Files, files);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Line("");
            Line($"Managed allocations reading everything once: {Mb(allocated)} for {Mb(total)} of content (the result arrays are {Mb(total)}; streams use pooled chunk buffers)");
            var stats = packaged.Files.Statistics.Snapshot();
            Line($"Package work so far: verify {Ms(stats.VerifyTime)}, decrypt {Ms(stats.DecryptTime)}, decompress {Ms(stats.DecompressTime)} over {stats.ChunksDecoded} chunks");

            if (repack)
            {
                Line("");
                Line($"{"Compression profile",-22} {"package size",12} {"ratio",7} {"write",10} {"read all",10}");
                var content = files.ToDictionary(f => f, packaged.Read, StringComparer.Ordinal);
                foreach (var profile in Enum.GetValues<PackageCompression>())
                {
                    var file = Path.Combine(scratch, $"{profile}.mnvtpack");
                    var write = Stopwatch.StartNew();
                    using (var writer = new PackageWriter(file, new PackageWriterOptions(Guid.NewGuid(), Guid.NewGuid(), profile)))
                    {
                        foreach (var (path, data) in content)
                        {
                            writer.Add(path, data);
                        }

                        writer.Complete();
                    }

                    write.Stop();
                    using var vfs = new VirtualFileSystem();
                    vfs.Mount(new IO.Packages.PackageFileSystemProvider(MnvtPackage.Open(file)));
                    Line($"{profile,-22} {Mb(new FileInfo(file).Length),12} {(double)new FileInfo(file).Length / total,7:P0} {Ms(write.Elapsed),10} {Ms(Best(3, () => ReadAll(vfs, files))),10}");
                }

                Line("(repacked unencrypted; encryption adds the decrypt time shown above)");
            }

            return text.ToString();

            void Compare(string name, Action<VirtualFileSystem> action)
            {
                action(packaged.Files);
                action(physical); // warm-up
                var fromPackages = Best(3, () => action(packaged.Files));
                var fromFiles = Best(3, () => action(physical));
                Line($"{name,-34} {Ms(fromPackages),12} {Ms(fromFiles),12} {(fromFiles.TotalMilliseconds > 0 ? fromPackages / fromFiles : 0),8:0.00}x");
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }

        void Line(string line) => text.Append(line).Append('\n');
    }

    private static void ReadAll(VirtualFileSystem vfs, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            vfs.ReadAllBytes(file);
        }
    }

    private static void RandomReads(VirtualFileSystem vfs, string file, long[] offsets)
    {
        using var stream = vfs.OpenRead(file);
        var buffer = new byte[4096];
        foreach (var offset in offsets)
        {
            stream.Position = offset;
            stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
    }

    private static string Category(string path) => path switch
    {
        _ when path.StartsWith("Shaders/", StringComparison.Ordinal) => "shaders (SPIR-V)",
        _ when path.StartsWith("Scripts/", StringComparison.Ordinal) => "C# code",
        _ when path.StartsWith("Bytecode/", StringComparison.Ordinal) => ".mnvts bytecode",
        _ when path.StartsWith("Artifacts/", StringComparison.Ordinal) => "models (artifacts)",
        _ => Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".PNG" or ".JPG" or ".JPEG" or ".SVG" => "textures",
            ".WAV" or ".OGG" or ".MP3" => "audio",
            ".MNVT" or ".MNVTPREFAB" => "scenes and prefabs",
            ".TTF" or ".OTF" => "fonts",
            ".HTML" or ".CSS" or ".JS" => "web pages",
            _ => "other",
        },
    };

    private static TimeSpan Best(int runs, Action action)
    {
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            action();
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }

        return best;
    }

    private static string Ms(TimeSpan time) => time.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture) + " ms";

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
}
