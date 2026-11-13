using System.Security.Cryptography;
using System.Text;
using MNVTEngine.IO.Packages;

namespace MNVTEngine.IO.Tests;

public sealed class PackageTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mnvt-pack-").FullName;
    private static readonly Guid Build = Guid.Parse("5b3a3f6c-6a3e-4d0b-9a59-0d8f0b0d1c11");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string File(string name) => Path.Combine(_directory, name);

    /// <summary>Compressible text, incompressible noise, a large file spanning many chunks and an empty file.</summary>
    private static Dictionary<string, (byte[] Data, Guid Guid)> SampleFiles()
    {
        var random = new Random(42);
        var noise = new byte[100_000];
        random.NextBytes(noise);
        var large = new byte[(5 * 1024 * 1024) + 123];
        for (var i = 0; i < large.Length; i++)
        {
            large[i] = (byte)(i % 251 < 200 ? i % 7 : random.Next(256));
        }

        return new Dictionary<string, (byte[], Guid)>
        {
            ["Assets/Scenes/Main.mnvt"] = (Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"entity\": \"Player\", \"x\": 1.5}\n", 2000))), Guid.NewGuid()),
            ["Assets/Textures/noise.png"] = (noise, Guid.NewGuid()),
            ["Assets/Models/big.bin"] = (large, Guid.NewGuid()),
            ["Assets/empty.txt"] = ([], Guid.NewGuid()),
            ["Bytecode/x.mnvtb"] = ("bytecode"u8.ToArray(), Guid.Empty),
        };
    }

    private static PackageWriteResult Write(string file, Dictionary<string, (byte[] Data, Guid Guid)> files, PackageWriterOptions options)
    {
        using var writer = new PackageWriter(file, options);
        foreach (var (path, (data, guid)) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            writer.Add(path, data, guid, guid == Guid.Empty ? null : [Guid.Parse("11111111-2222-3333-4444-555555555555")]);
        }

        return writer.Complete();
    }

    private static PackageWriterOptions Options(PackageCompression compression = PackageCompression.Balanced, PackageEncryptionKey? key = null, PackageSigningKey? signer = null) =>
        new(Guid.Parse("0e8b8d43-77bf-4b0e-9a8b-6f9f8f7d0c01"), Build, compression, EncryptionKey: key, SigningKey: signer, Properties: new Dictionary<string, string> { ["name"] = "GameData", ["target"] = "win-x64" });

    [Theory]
    [InlineData(PackageCompression.None, false, false)]
    [InlineData(PackageCompression.Fast, false, false)]
    [InlineData(PackageCompression.Balanced, true, false)]
    [InlineData(PackageCompression.Small, true, true)]
    public void RoundTrip_EveryFileReadsBackExactly(PackageCompression compression, bool encrypt, bool sign)
    {
        var files = SampleFiles();
        var key = encrypt ? PackageEncryptionKey.Create() : null;
        using var signer = sign ? PackageSigningKey.Create() : null;
        var result = Write(File("a.mnvtpack"), files, Options(compression, key, signer));
        Assert.Equal(files.Count, result.Files);
        if (compression != PackageCompression.None)
        {
            Assert.True(result.StoredBytes < result.OriginalBytes);
        }

        using var package = MnvtPackage.Open(File("a.mnvtpack"), new PackageOpenOptions(key is null ? null : new PackageKeyRing(key), signer is null ? null : [signer.PublicKey], RequireSignature: sign));
        Assert.Equal(sign ? PackageSignatureStatus.Trusted : PackageSignatureStatus.Unsigned, package.Signature);
        Assert.Equal(encrypt, package.Info.IsEncrypted);
        Assert.Equal("GameData", package.Info.Properties["name"]);
        foreach (var (path, (data, guid)) in files)
        {
            using var stream = package.OpenRead(path);
            Assert.Equal(data.Length, stream.Length);
            Assert.Equal(data, ReadAll(stream));
            if (guid != Guid.Empty)
            {
                Assert.Equal(path, package.FindAsset(guid));
                Assert.True(package.TryGetEntry(path, out var entry));
                Assert.Equal([Guid.Parse("11111111-2222-3333-4444-555555555555")], entry.Dependencies);
            }
        }

        Assert.True(package.Verify().Succeeded);
    }

    [Fact]
    public void RandomAccess_ReadsAnyRangeAcrossChunks()
    {
        var files = SampleFiles();
        var large = files["Assets/Models/big.bin"].Data;
        Write(File("a.mnvtpack"), files, Options(key: PackageEncryptionKey.Create()) with { EncryptionKey = null });
        using var package = MnvtPackage.Open(File("a.mnvtpack"));
        using var stream = package.OpenRead("Assets/Models/big.bin");
        var random = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var offset = random.NextInt64(large.Length);
            var count = random.Next(1, 300_000);
            stream.Position = offset;
            var buffer = new byte[count];
            var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
            Assert.Equal((int)Math.Min(count, large.Length - offset), read);
            Assert.True(large.AsSpan((int)offset, read).SequenceEqual(buffer.AsSpan(0, read)), $"range {offset}+{count}");
        }

        stream.Seek(0, SeekOrigin.End);
        Assert.Equal(0, stream.Read(new byte[10]));
    }

    [Fact]
    public async Task VirtualFileSystem_MountsPackages_AndReadsAsync()
    {
        var files = SampleFiles();
        var key = PackageEncryptionKey.Create();
        Write(File("GameData.mnvtpack"), files, Options(key: key));
        using var vfs = new VirtualFileSystem();
        var handle = vfs.MountPackage(File("GameData.mnvtpack"), new PackageOpenOptions(new PackageKeyRing(key)));
        var (data, guid) = files["Assets/Models/big.bin"];

        Assert.Equal(data, await vfs.ReadAllBytesAsync("Assets/Models/big.bin", TestContext.Current.CancellationToken));
        Assert.Equal("Assets/Models/big.bin", vfs.FindAsset(guid));
        Assert.True(vfs.DirectoryExists("Assets/Models"));
        Assert.Equal(files.Count, vfs.EnumerateFiles("").Count());
        var mount = Assert.Single(vfs.Mounts);
        Assert.Equal(("package", files.Count), (mount.Kind, mount.FileCount));

        var stats = vfs.Statistics.Snapshot();
        Assert.True(stats.ChunksDecoded >= 80 && stats.BytesRead == data.Length && stats.ActiveStreams == 0 && stats.PendingReads == 0, stats.ToString());
        Assert.True(stats.DecryptTime > TimeSpan.Zero && stats.DecompressTime > TimeSpan.Zero && stats.PackageOpenTime > TimeSpan.Zero);

        // Unmount and remount.
        Assert.True(vfs.Unmount(handle));
        Assert.False(vfs.FileExists("Assets/Models/big.bin"));
        vfs.MountPackage(File("GameData.mnvtpack"), new PackageOpenOptions(new PackageKeyRing(key)));
        Assert.Equal(files["Assets/Scenes/Main.mnvt"].Data, vfs.ReadAllBytes("Assets/Scenes/Main.mnvt"));
    }

    [Fact]
    public async Task AsyncRead_HonoursCancellation()
    {
        Write(File("a.mnvtpack"), SampleFiles(), Options());
        using var package = MnvtPackage.Open(File("a.mnvtpack"));
        await using var stream = package.OpenRead("Assets/Models/big.bin");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadExactlyAsync(new byte[1 << 20], cancelled.Token));
    }

    [Fact]
    public void UnmountWhileReading_KeepsTheOpenStreamWorking()
    {
        var files = SampleFiles();
        Write(File("a.mnvtpack"), files, Options());
        var package = MnvtPackage.Open(File("a.mnvtpack"));
        var stream = package.OpenRead("Assets/Models/big.bin");
        package.Dispose();
        Assert.Throws<ObjectDisposedException>(() => package.OpenRead("Assets/empty.txt"));
        Assert.Equal(files["Assets/Models/big.bin"].Data, ReadAll(stream)); // the file closes with the last stream
        stream.Dispose();
    }

    [Fact]
    public void IdenticalInput_GivesIdenticalPackages_AndDuplicatesShareChunks()
    {
        var files = SampleFiles();
        files["Assets/Copy/big-copy.bin"] = (files["Assets/Models/big.bin"].Data, Guid.NewGuid());
        var key = PackageEncryptionKey.Create();
        using var signer = PackageSigningKey.Create();
        var first = Write(File("1.mnvtpack"), files, Options(key: key, signer: signer));
        var second = Write(File("2.mnvtpack"), files, Options(key: key, signer: signer));

        Assert.Equal(1, first.DuplicateFiles);
        Assert.Equal(first.HeaderSha256, second.HeaderSha256);
        // ECDSA signatures are randomized; everything before the signature is byte-identical.
        var a = System.IO.File.ReadAllBytes(File("1.mnvtpack"));
        var b = System.IO.File.ReadAllBytes(File("2.mnvtpack"));
        Assert.Equal(a.Length, b.Length);
        Assert.True(a.AsSpan(PackageFormat.HeaderSize, a.Length - PackageFormat.HeaderSize - 100).SequenceEqual(b.AsSpan(PackageFormat.HeaderSize, b.Length - PackageFormat.HeaderSize - 100)));

        using var package = MnvtPackage.Open(File("1.mnvtpack"), new PackageOpenOptions(new PackageKeyRing(key)));
        Assert.True(package.TryGetEntry("Assets/Copy/big-copy.bin", out var copy) && copy.SharesChunks);
        Assert.Equal(files["Assets/Models/big.bin"].Data, ReadAll(package.OpenRead("Assets/Copy/big-copy.bin")));
    }

    [Fact]
    public void PrecompressedFiles_AreStoredAsIs()
    {
        Write(File("a.mnvtpack"), SampleFiles(), Options());
        using var package = MnvtPackage.Open(File("a.mnvtpack"));
        Assert.True(package.TryGetEntry("Assets/Textures/noise.png", out var png));
        Assert.Equal(png.Length, png.StoredLength);
    }

    [Fact]
    public void Writer_RefusesDuplicatePathsGuidsAndEscapingPaths()
    {
        using var writer = new PackageWriter(File("a.mnvtpack"), Options());
        var guid = Guid.NewGuid();
        writer.Add("Assets/a.txt", [1], guid);
        Assert.Throws<ArgumentException>(() => writer.Add("assets/A.TXT", [2]));
        Assert.Throws<ArgumentException>(() => writer.Add("Assets/b.txt", [2], guid));
        Assert.Throws<ArgumentException>(() => writer.Add("../x.txt", [3]));
    }

    [Fact]
    public void IncompletePackage_IsRejected()
    {
        using (var writer = new PackageWriter(File("a.mnvtpack"), Options()))
        {
            writer.Add("a.txt", [1, 2, 3]);
        }

        Assert.Equal(PackageError.Malformed, Assert.Throws<PackageException>(() => MnvtPackage.Open(File("a.mnvtpack"))).Error);
    }

    // ---- Keys -------------------------------------------------------------------------------------------------

    [Fact]
    public void EncryptedPackage_NeedsItsKey()
    {
        var key = PackageEncryptionKey.Create();
        Write(File("a.mnvtpack"), SampleFiles(), Options(key: key));

        Assert.Equal(PackageError.MissingKey, Open().Error);
        Assert.Equal(PackageError.MissingKey, Open(new PackageKeyRing(PackageEncryptionKey.Create())).Error);
        var info = MnvtPackage.ReadInfo(File("a.mnvtpack")); // public part needs no key
        Assert.True(info.IsEncrypted);
        Assert.Equal(key.Id, info.KeyId);
        Assert.Equal("win-x64", info.Properties["target"]);

        // The index is not readable in the file: no path appears in plain text.
        Assert.DoesNotContain("Assets/Models", Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(File("a.mnvtpack"))), StringComparison.Ordinal);

        PackageException Open(PackageKeyRing? keys = null) => Assert.Throws<PackageException>(() => MnvtPackage.Open(File("a.mnvtpack"), new PackageOpenOptions(keys)));
    }

    [Fact]
    public void KeyRing_SupportsRotation_EachPackageFindsItsKey()
    {
        var old = PackageEncryptionKey.Create();
        var current = PackageEncryptionKey.Create();
        Write(File("old.mnvtpack"), SampleFiles(), Options(key: old));
        Write(File("new.mnvtpack"), SampleFiles(), Options(key: current) with { PackageGuid = Guid.NewGuid() });
        var ring = new PackageKeyRing(current, old);
        using var a = MnvtPackage.Open(File("old.mnvtpack"), new PackageOpenOptions(ring));
        using var b = MnvtPackage.Open(File("new.mnvtpack"), new PackageOpenOptions(ring));
        Assert.Equal(old.Id, a.Info.KeyId);
        Assert.Equal(current.Id, b.Info.KeyId);
    }

    [Fact]
    public void EachPackage_GetsItsOwnDataKey()
    {
        var key = PackageEncryptionKey.Create();
        var files = new Dictionary<string, (byte[], Guid)> { ["a.txt"] = (new byte[10_000], Guid.Empty) };
        Write(File("1.mnvtpack"), files, Options(PackageCompression.None, key));
        Write(File("2.mnvtpack"), files, Options(PackageCompression.None, key) with { PackageGuid = Guid.NewGuid() });
        var a = System.IO.File.ReadAllBytes(File("1.mnvtpack")).AsSpan(PackageFormat.HeaderSize, 10_000).ToArray();
        var b = System.IO.File.ReadAllBytes(File("2.mnvtpack")).AsSpan(PackageFormat.HeaderSize, 10_000).ToArray();
        Assert.NotEqual(a, b); // same plaintext, same master key, different packages: different ciphertext
        Assert.NotEqual(new byte[10_000], a);
    }

    // ---- Integrity and tampering ----------------------------------------------------------------------------

    [Fact]
    public void CorruptedChunk_IsDetected_OnReadAndByVerify()
    {
        var files = SampleFiles();
        Write(File("a.mnvtpack"), files, Options());
        FlipByte(PackageFormat.HeaderSize + 10);
        using var package = MnvtPackage.Open(File("a.mnvtpack"));
        var first = package.Entries.First(e => e.Chunks > 0).Path;
        var error = Assert.Throws<PackageException>(() => ReadAll(package.OpenRead(first)));
        Assert.Equal(PackageError.IntegrityFailure, error.Error);
        var verification = package.Verify();
        Assert.False(verification.Succeeded);
        Assert.Contains(first, Assert.Single(verification.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void ModifiedEncryptedChunk_FailsAuthentication_EvenWithAMatchingHash()
    {
        var key = PackageEncryptionKey.Create();
        Write(File("a.mnvtpack"), SampleFiles(), Options(key: key));
        using (var package = MnvtPackage.Open(File("a.mnvtpack"), new PackageOpenOptions(new PackageKeyRing(key))))
        {
            Assert.True(package.Verify().Succeeded);
        }

        // Swap two ciphertext chunks: the attacker cannot recompute hashes inside the encrypted index, so even a
        // perfectly placed swap of valid ciphertext fails.
        var bytes = System.IO.File.ReadAllBytes(File("a.mnvtpack"));
        var first = bytes.AsSpan(PackageFormat.HeaderSize, 100).ToArray();
        bytes.AsSpan(PackageFormat.HeaderSize + 100, 100).CopyTo(bytes.AsSpan(PackageFormat.HeaderSize));
        first.CopyTo(bytes, PackageFormat.HeaderSize + 100);
        System.IO.File.WriteAllBytes(File("a.mnvtpack"), bytes);
        using var tampered = MnvtPackage.Open(File("a.mnvtpack"), new PackageOpenOptions(new PackageKeyRing(key)));
        Assert.False(tampered.Verify().Succeeded);
    }

    [Fact]
    public void CorruptedHeader_IsDetected()
    {
        Write(File("a.mnvtpack"), SampleFiles(), Options());
        FlipByte(PackageFormat.OffPackageGuid);
        Assert.Equal(PackageError.IntegrityFailure, Assert.Throws<PackageException>(() => MnvtPackage.Open(File("a.mnvtpack"))).Error);
    }

    [Fact]
    public void NotAPackage_AndUnsupportedVersions_AreRefused()
    {
        System.IO.File.WriteAllText(File("x.mnvtpack"), "PK\u0003\u0004 this is a zip");
        Assert.Equal(PackageError.Malformed, Assert.Throws<PackageException>(() => MnvtPackage.Open(File("x.mnvtpack"))).Error);

        Write(File("a.mnvtpack"), SampleFiles(), Options());
        Patch(PackageFormat.OffMajor, [2, 0], rehashHeader: true);
        Assert.Equal(PackageError.UnsupportedVersion, Assert.Throws<PackageException>(() => MnvtPackage.Open(File("a.mnvtpack"))).Error);
    }

    [Fact]
    public void InvalidOffsets_AreRefused()
    {
        Write(File("a.mnvtpack"), SampleFiles(), Options());
        Patch(PackageFormat.OffIndexOffset, BitConverter.GetBytes(long.MaxValue - 5), rehashHeader: true);
        Assert.Equal(PackageError.Malformed, Assert.Throws<PackageException>(() => MnvtPackage.Open(File("a.mnvtpack"))).Error);
    }

    [Fact]
    public void ModifiedIndexAuthenticationTag_IsRejected()
    {
        var key = PackageEncryptionKey.Create();
        Write(File("a.mnvtpack"), SampleFiles(), Options(key: key));
        Patch(PackageFormat.OffIndexTag, [0xAA], rehashHeader: true);
        Assert.Equal(PackageError.WrongKey, Assert.Throws<PackageException>(() => MnvtPackage.Open(File("a.mnvtpack"), new PackageOpenOptions(new PackageKeyRing(key)))).Error);
    }

    // ---- Signatures -----------------------------------------------------------------------------------------

    [Fact]
    public void Signatures_AreEnforcedByPolicy()
    {
        using var signer = PackageSigningKey.Create();
        using var stranger = PackageSigningKey.Create();
        Write(File("signed.mnvtpack"), SampleFiles(), Options(signer: signer));
        Write(File("unsigned.mnvtpack"), SampleFiles(), Options());
        var policy = new PackageOpenOptions(TrustedSigners: [signer.PublicKey], RequireSignature: true);

        using (var ok = MnvtPackage.Open(File("signed.mnvtpack"), policy))
        {
            Assert.Equal(PackageSignatureStatus.Trusted, ok.Signature);
            Assert.Equal(signer.PublicKey.Id, ok.Info.SignerId);
        }

        Assert.Equal(PackageError.UntrustedSignature, Assert.Throws<PackageException>(() => MnvtPackage.Open(File("unsigned.mnvtpack"), policy)).Error);
        Assert.Equal(PackageError.UntrustedSignature, Assert.Throws<PackageException>(() => MnvtPackage.Open(File("signed.mnvtpack"), policy with { TrustedSigners = [stranger.PublicKey] })).Error);
        using var lenient = MnvtPackage.Open(File("signed.mnvtpack"), new PackageOpenOptions(TrustedSigners: [stranger.PublicKey]));
        Assert.Equal(PackageSignatureStatus.UnknownSigner, lenient.Signature);
    }

    [Fact]
    public void ModifiedSignedPackage_FailsItsSignature_EvenWithRecomputedHashes()
    {
        using var signer = PackageSigningKey.Create();
        Write(File("a.mnvtpack"), SampleFiles(), Options(signer: signer));

        // Change the build id and fix up the header hash, as an attacker without the private key can.
        Patch(PackageFormat.OffBuildId, [0x42], rehashHeader: true);
        var policy = new PackageOpenOptions(TrustedSigners: [signer.PublicKey], RequireSignature: true);
        Assert.Equal(PackageError.InvalidSignature, Assert.Throws<PackageException>(() => MnvtPackage.Open(File("a.mnvtpack"), policy)).Error);

        // Stripping the signature does not help either.
        Write(File("a.mnvtpack"), SampleFiles(), Options(signer: signer));
        Patch(PackageFormat.OffFlags, BitConverter.GetBytes(0u), rehashHeader: false);
        Patch(PackageFormat.OffSignatureOffset, new byte[12], rehashHeader: true);
        Assert.Equal(PackageError.UntrustedSignature, Assert.Throws<PackageException>(() => MnvtPackage.Open(File("a.mnvtpack"), policy)).Error);
    }

    [Fact]
    public void SigningKeys_RoundTripThroughPem()
    {
        using var key = PackageSigningKey.Create();
        using var loaded = PackageSigningKey.FromPem(key.ExportPrivateKeyPem());
        Assert.Equal(key.PublicKey.Id, loaded.PublicKey.Id);
        Assert.Equal(key.PublicKey.Id, PackageVerificationKey.FromBase64(key.PublicKey.ToBase64()).Id);
        var signature = DetachedSignature.Sign(loaded, "manifest"u8);
        Assert.True(key.PublicKey.VerifyDetached("manifest"u8, signature));
        Assert.False(key.PublicKey.VerifyDetached("manifesT"u8, signature));
        Assert.Throws<ArgumentException>(() => PackageSigningKey.FromPem(key.PublicKey.ToPem()));
    }

    // ---- Malformed input --------------------------------------------------------------------------------------

    [Fact]
    public void MalformedIndexes_NeverCauseAnythingButPackageExceptions()
    {
        var files = SampleFiles();
        files.Remove("Assets/Models/big.bin");
        Write(File("good.mnvtpack"), files, Options(PackageCompression.Fast));
        var good = System.IO.File.ReadAllBytes(File("good.mnvtpack"));
        var indexOffset = (int)BitConverter.ToInt64(good, PackageFormat.OffIndexOffset);
        var indexLength = BitConverter.ToInt32(good, PackageFormat.OffIndexStoredLength);
        var random = new Random(1234);
        var opened = 0;
        for (var round = 0; round < 1500; round++)
        {
            var bytes = (byte[])good.Clone();
            for (var flips = random.Next(1, 4); flips > 0; flips--)
            {
                bytes[indexOffset + random.Next(indexLength)] = (byte)random.Next(256);
            }

            // Fix up the hashes so the parser, not the hash check, sees the damage.
            SHA256.HashData(bytes.AsSpan(indexOffset, indexLength)).CopyTo(bytes, PackageFormat.OffIndexHash);
            SHA256.HashData(bytes.AsSpan(0, PackageFormat.SignedHeaderSize)).CopyTo(bytes, PackageFormat.OffHeaderHash);
            System.IO.File.WriteAllBytes(File("fuzz.mnvtpack"), bytes);
            try
            {
                using var package = MnvtPackage.Open(File("fuzz.mnvtpack"));
                opened++;
                foreach (var entry in package.Entries)
                {
                    try
                    {
                        ReadAll(package.OpenRead(entry.Path));
                    }
                    catch (PackageException)
                    {
                    }
                }
            }
            catch (PackageException)
            {
            }
        }

        Assert.True(opened > 0, "Some mutations (e.g. in content hashes) leave a parseable index.");
    }

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy, 70_000); // a buffer that never lines up with chunks
            return copy.ToArray();
        }
    }

    private void FlipByte(long offset) => Patch(offset, [(byte)(System.IO.File.ReadAllBytes(File("a.mnvtpack"))[offset] ^ 0xFF)], rehashHeader: false);

    private void Patch(long offset, byte[] data, bool rehashHeader)
    {
        var bytes = System.IO.File.ReadAllBytes(File("a.mnvtpack"));
        data.CopyTo(bytes, offset);
        if (rehashHeader)
        {
            SHA256.HashData(bytes.AsSpan(0, PackageFormat.SignedHeaderSize)).CopyTo(bytes, PackageFormat.OffHeaderHash);
        }

        System.IO.File.WriteAllBytes(File("a.mnvtpack"), bytes);
    }
}
