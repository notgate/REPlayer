using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReVM;

/// <summary>
/// Builds the version-pinned REPlayer Emulator runtime without modifying Google's
/// signed SDK installation. Every file is hard-linked where NTFS permits; only
/// the transformed gfxstream backend is materialized separately.
/// </summary>
internal static class GfxstreamPersonaRuntime
{
    public const string EmulatorVersion = "37.1.7";
    public const string EmulatorBuildId = "15769812";
    public const string SourceSha256 = "f84d3a277e1ecc380470e7ed988b79cf361e0cedf9d9ee9bf7ee188d90b04947";
    public const string OutputSha256 = "4658c1e77b33be807cd164292a53e0a6da376225b6f8999cfc7810031de3ba3a";

    private const int VendorOffset = 0xF758C0;
    private const int VendorInlineOffset = 0x17EB8B;
    private const int VendorAppendOffset = 0x17EB9E;
    private const int RendererOffset = 0xF758D0;
    private const int RendererInlineTailOffset = 0x17ED5B;
    private const int RendererAppendOffset = 0x17ED70;
    private const int VersionSuffixOffset = 0x17EF3A;

    private static readonly Patch[] Patches =
    {
        Patch.Text(VendorOffset, "Google (", "REPlayer"),
        Patch.Hex(VendorInlineOffset, "48B8476F6F676C652028", "48B85245506C61796572"),
        Patch.Hex(VendorAppendOffset, "498B4D1049", "E99D000000"),
        Patch.Text(RendererOffset,
            "Android Emulator OpenGL ES Translator (",
            "REPlayer Virtual GPU (OpenGL ES 3.1 1.0"),
        Patch.Hex(RendererInlineTailOffset, "48B8736C61746F722028", "48B820332E3120312E30"),
        Patch.Hex(RendererAppendOffset, "498B4F10498B47184889", "4C8B6C2428E94B000000"),
        Patch.Hex(VersionSuffixOffset, "488B461048", "E9EC000000"),
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    public static bool IsReady(string runtimeDirectory)
    {
        var emulator = Path.Combine(runtimeDirectory, "emulator.exe");
        var backend = Path.Combine(runtimeDirectory, "lib64", "libgfxstream_backend.dll");
        return File.Exists(emulator) && File.Exists(backend) &&
               string.Equals(HashFile(backend), OutputSha256, StringComparison.OrdinalIgnoreCase);
    }

    public static void Ensure(string sourceDirectory, string runtimeDirectory, Action<string>? log = null)
    {
        if (IsReady(runtimeDirectory)) return;

        var sourceBackend = Path.Combine(sourceDirectory, "lib64", "libgfxstream_backend.dll");
        if (!File.Exists(sourceBackend))
            throw new FileNotFoundException("Google gfxstream backend is missing.", sourceBackend);
        var sourceHash = HashFile(sourceBackend);
        if (!string.Equals(sourceHash, SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported Google gfxstream backend SHA-256: {sourceHash}");

        if (Directory.Exists(runtimeDirectory)) Directory.Delete(runtimeDirectory, recursive: true);
        Directory.CreateDirectory(runtimeDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(runtimeDirectory, Path.GetRelativePath(sourceDirectory, directory)));

        var linked = 0;
        var copied = 0;
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(sourceBackend), StringComparison.OrdinalIgnoreCase))
                continue;
            var destination = Path.Combine(runtimeDirectory, Path.GetRelativePath(sourceDirectory, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (CreateHardLinkW(destination, source, IntPtr.Zero)) linked++;
            else { File.Copy(source, destination, overwrite: false); copied++; }
        }

        var data = File.ReadAllBytes(sourceBackend);
        foreach (var patch in Patches) patch.Apply(data);
        var outputHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (!string.Equals(outputHash, OutputSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Transformed gfxstream SHA-256 mismatch: {outputHash}");
        var outputBackend = Path.Combine(runtimeDirectory, "lib64", "libgfxstream_backend.dll");
        File.WriteAllBytes(outputBackend, data);

        var manifest = new
        {
            schema = 1,
            emulatorVersion = EmulatorVersion,
            emulatorBuildId = EmulatorBuildId,
            input = sourceBackend,
            inputSha256 = sourceHash,
            output = outputBackend,
            outputSha256 = outputHash,
            vendor = "REPlayer",
            renderer = "REPlayer Virtual GPU (OpenGL ES 3.1 1.0)",
            linkedFiles = linked,
            copiedFiles = copied,
            patches = Array.ConvertAll(Patches, p => new { offset = p.Offset, before = Convert.ToHexString(p.Before).ToLowerInvariant(), after = Convert.ToHexString(p.After).ToLowerInvariant() })
        };
        File.WriteAllText(Path.Combine(runtimeDirectory, "replayer-gfxstream-persona.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        log?.Invoke($"Prepared gfxstream persona runtime ({linked} hard links, {copied} copied files, {outputHash}).");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record Patch(int Offset, byte[] Before, byte[] After)
    {
        public static Patch Text(int offset, string before, string after) =>
            new(offset, Encoding.ASCII.GetBytes(before), Encoding.ASCII.GetBytes(after));

        public static Patch Hex(int offset, string before, string after) =>
            new(offset, Convert.FromHexString(before), Convert.FromHexString(after));

        public void Apply(byte[] data)
        {
            if (Before.Length != After.Length)
                throw new InvalidDataException($"Patch at 0x{Offset:x} changes compiled length.");
            if (Offset < 0 || Offset + Before.Length > data.Length ||
                !data.AsSpan(Offset, Before.Length).SequenceEqual(Before))
                throw new InvalidDataException($"gfxstream bytes do not match at pinned offset 0x{Offset:x}.");
            After.CopyTo(data, Offset);
        }
    }
}
