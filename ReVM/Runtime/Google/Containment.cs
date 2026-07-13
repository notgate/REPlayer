using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Text.Json;

namespace ReVM;

internal sealed class OfficialEmulatorRunContext
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();

    public required string VmId { get; init; }
    public required string CaseId { get; init; }
    public required string CaseDirectory { get; init; }
    public required string ArtifactsDirectory { get; init; }
    public required string RunAvdHome { get; init; }
    public required string RunAvdName { get; init; }
    public required string RunAvdDirectory { get; init; }
    public required string BaselineAvdDirectory { get; init; }
    public required string SystemImageDirectory { get; init; }
    public required string ManifestPath { get; init; }
    public required string PacketCapturePath { get; init; }
    public required string StdoutPath { get; init; }
    public required string StderrPath { get; init; }
    public required int ConsolePort { get; init; }
    public required int AdbPort { get; init; }
    public required string AdbSerial { get; init; }
    public required bool DisposableMode { get; init; }
    public bool StopRequested { get; set; }
    public bool Finalized { get; private set; }
    public IDisposable? BaselineReadLease { get; set; }
    public OfficialEmulatorPersonaPlan? PersonaPlan { get; set; }
    public OfficialEmulatorRunManifest Manifest { get; init; } = new();

    public void WriteManifest()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(CaseDirectory);
            var temporaryPath = ManifestPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Manifest, JsonOptions));
            File.Move(temporaryPath, ManifestPath, overwrite: true);
        }
    }

    public bool TryBeginFinalization()
    {
        lock (_gate)
        {
            if (Finalized) return false;
            Finalized = true;
            return true;
        }
    }

    public void RegisterArtifact(string kind, string path)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(path)) return;
        lock (_gate)
        {
            Manifest.Artifacts[SanitizeArtifactKey(kind)] = Path.GetFullPath(path);
            WriteManifest();
        }
    }

    public static OfficialEmulatorRunContext Create(
        string casesRoot,
        string vmId,
        string profile,
        string baselineAvdDirectory,
        string systemImageDirectory,
        int preferredAdbPort,
        bool disposableMode)
    {
        var now = DateTime.UtcNow;
        var caseId = $"case-{now:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}"[..42];
        var caseDirectory = Path.Combine(casesRoot, now.ToString("yyyy-MM-dd"), caseId);
        var artifactsDirectory = Path.Combine(caseDirectory, "artifacts");
        var runAvdHome = Path.Combine(caseDirectory, "run-avd-home");
        var runAvdName = "REPlayer_Run_" + caseId.Replace('-', '_');
        var runAvdDirectory = Path.Combine(runAvdHome, runAvdName + ".avd");
        var (consolePort, adbPort) = OfficialEmulatorPortAllocator.Allocate(preferredAdbPort);
        Directory.CreateDirectory(artifactsDirectory);
        Directory.CreateDirectory(runAvdHome);

        var manifestPath = Path.Combine(caseDirectory, "manifest.json");
        var packetCapturePath = Path.Combine(artifactsDirectory, "network.pcap");
        var stdoutPath = Path.Combine(artifactsDirectory, "emulator-stdout.log");
        var stderrPath = Path.Combine(artifactsDirectory, "emulator-stderr.log");
        var context = new OfficialEmulatorRunContext
        {
            VmId = vmId,
            CaseId = caseId,
            CaseDirectory = caseDirectory,
            ArtifactsDirectory = artifactsDirectory,
            RunAvdHome = runAvdHome,
            RunAvdName = runAvdName,
            RunAvdDirectory = runAvdDirectory,
            BaselineAvdDirectory = Path.GetFullPath(baselineAvdDirectory),
            SystemImageDirectory = Path.GetFullPath(systemImageDirectory),
            ManifestPath = manifestPath,
            PacketCapturePath = packetCapturePath,
            StdoutPath = stdoutPath,
            StderrPath = stderrPath,
            ConsolePort = consolePort,
            AdbPort = adbPort,
            AdbSerial = $"emulator-{consolePort}",
            DisposableMode = disposableMode,
            Manifest = new OfficialEmulatorRunManifest
            {
                CaseId = caseId,
                RunId = caseId,
                VmId = vmId,
                Profile = profile,
                BaselineAvdDirectory = Path.GetFullPath(baselineAvdDirectory),
                RunAvdName = runAvdName,
                RunAvdDirectory = Path.GetFullPath(runAvdDirectory),
                ExecutionMode = disposableMode ? "disposable-detonation" : "fresh-clone",
                ConsolePort = consolePort,
                AdbPort = adbPort,
                AdbSerial = $"emulator-{consolePort}",
                StartedAtUtc = now,
                StartState = "preparing",
                EndState = "pending",
                Containment = new OfficialEmulatorContainmentSettings
                {
                    FreshBaselineClone = true,
                    DisposeRunStorage = true,
                    ReadOnly = disposableMode,
                    NoSnapshotLoad = disposableMode,
                    NoSnapshotSave = disposableMode,
                    NoSnapshotStorage = disposableMode,
                    FastBootDisabled = true,
                    TcpdumpFromProcessStart = true
                },
                Artifacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["caseDirectory"] = Path.GetFullPath(caseDirectory),
                    ["manifest"] = Path.GetFullPath(manifestPath),
                    ["packetCapture"] = Path.GetFullPath(packetCapturePath),
                    ["emulatorStdout"] = Path.GetFullPath(stdoutPath),
                    ["emulatorStderr"] = Path.GetFullPath(stderrPath),
                    ["artifactsDirectory"] = Path.GetFullPath(artifactsDirectory)
                }
            }
        };
        context.WriteManifest();
        return context;
    }

    private static string SanitizeArtifactKey(string value)
    {
        var sanitized = new string(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "artifact" : sanitized;
    }
}

internal sealed class OfficialEmulatorRunManifest
{
    public string CaseId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string VmId { get; set; } = string.Empty;
    public string Profile { get; set; } = string.Empty;
    public string BaselineAvdDirectory { get; set; } = string.Empty;
    public string RunAvdName { get; set; } = string.Empty;
    public string RunAvdDirectory { get; set; } = string.Empty;
    public string ExecutionMode { get; set; } = "disposable-detonation";
    public int ConsolePort { get; set; }
    public int AdbPort { get; set; }
    public string AdbSerial { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string StartState { get; set; } = "preparing";
    public string EndState { get; set; } = "pending";
    public string? Failure { get; set; }
    public int? EmulatorProcessId { get; set; }
    public bool? BaselineHashMatch { get; set; }
    public bool RunStorageDisposed { get; set; }
    public OfficialEmulatorContainmentSettings Containment { get; set; } = new();
    public object? NetworkPolicy { get; set; }
    public object? Persona { get; set; }
    public object? PersonaVerification { get; set; }
    public bool? GuestNetworkFilterApplied { get; set; }
    public string? GuestNetworkFilterStatus { get; set; }
    public List<string> LaunchArguments { get; set; } = new();
    public Dictionary<string, string> BaselineHashesBefore { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> BaselineHashesAfter { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Artifacts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class OfficialEmulatorContainmentSettings
{
    public bool FreshBaselineClone { get; set; }
    public bool DisposeRunStorage { get; set; }
    public bool ReadOnly { get; set; }
    public bool NoSnapshotLoad { get; set; }
    public bool NoSnapshotSave { get; set; }
    public bool NoSnapshotStorage { get; set; }
    public bool FastBootDisabled { get; set; }
    public bool TcpdumpFromProcessStart { get; set; }
}

internal static class OfficialEmulatorPortAllocator
{
    private static readonly object Gate = new();
    private static readonly HashSet<int> ReservedConsolePorts = new();
    private const int MinimumConsolePort = 5554;
    private const int MaximumConsolePort = 5680;

    public static (int ConsolePort, int AdbPort) Allocate(int preferredAdbPort)
    {
        lock (Gate)
        {
            var preferredConsole = preferredAdbPort is >= MinimumConsolePort + 1 and <= MaximumConsolePort + 1 && preferredAdbPort % 2 == 1
                ? preferredAdbPort - 1
                : MinimumConsolePort;
            foreach (var consolePort in CandidatePorts(preferredConsole))
            {
                var adbPort = consolePort + 1;
                if (!ReservedConsolePorts.Contains(consolePort) && PortIsAvailable(consolePort) && PortIsAvailable(adbPort))
                {
                    ReservedConsolePorts.Add(consolePort);
                    return (consolePort, adbPort);
                }
            }
        }

        throw new InvalidOperationException($"No free official-emulator console/ADB port pair was available in {MinimumConsolePort}-{MaximumConsolePort + 1}.");
    }

    public static void Release(int consolePort)
    {
        lock (Gate) ReservedConsolePorts.Remove(consolePort);
    }

    private static IEnumerable<int> CandidatePorts(int preferredConsole)
    {
        yield return preferredConsole;
        for (var port = MinimumConsolePort; port <= MaximumConsolePort; port += 2)
            if (port != preferredConsole) yield return port;
    }

    private static bool PortIsAvailable(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { }
        }
    }
}

internal sealed class OfficialEmulatorJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectBasicAccountingInformationClass = 1;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint SnapshotProcesses = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private IntPtr _handle;

    private OfficialEmulatorJob(IntPtr handle) => _handle = handle;

    public static OfficialEmulatorJob Attach(System.Diagnostics.Process process)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the emulator containment job object.");

        var job = new OfficialEmulatorJob(handle);
        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(limits, pointer, false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, pointer, (uint)length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure emulator kill-on-close containment.");
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }

            if (!AssignProcessToJobObject(handle, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign emulator.exe to the containment job object.");
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public bool HasActiveProcesses
    {
        get
        {
            var handle = _handle;
            if (handle == IntPtr.Zero) return false;
            var length = Marshal.SizeOf<JobObjectBasicAccountingInformation>();
            var pointer = Marshal.AllocHGlobal(length);
            try
            {
                if (!QueryInformationJobObject(handle, JobObjectBasicAccountingInformationClass, pointer, (uint)length, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query emulator containment job state.");
                return Marshal.PtrToStructure<JobObjectBasicAccountingInformation>(pointer).ActiveProcesses > 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    public int AttachTrustedDescendants(int rootProcessId, IReadOnlySet<string> trustedExecutablePaths)
    {
        var handle = _handle;
        if (handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(OfficialEmulatorJob));
        var attached = 0;
        foreach (var processId in GetDescendantProcessIds(rootProcessId))
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(processId);
                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path) || !trustedExecutablePaths.Contains(Path.GetFullPath(path))) continue;
                if (!IsProcessInJob(process.Handle, handle, out var alreadyInJob))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not query job ownership for process {processId}.");
                if (alreadyInJob) continue;
                if (!AssignProcessToJobObject(handle, process.Handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not attach trusted emulator child process {processId} to containment.");
                attached++;
            }
            catch (ArgumentException)
            {
                // Process exited between the snapshot and handle open.
            }
            catch (InvalidOperationException)
            {
                // Process exited while its path/handle was queried.
            }
            finally
            {
                process?.Dispose();
            }
        }
        return attached;
    }

    internal static IReadOnlySet<int> GetDescendantProcessIds(int rootProcessId)
    {
        var parentByProcess = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not snapshot the emulator process tree.");
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    parentByProcess[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                    entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
                } while (Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var descendants = new HashSet<int>();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var pair in parentByProcess)
            {
                if (pair.Key == rootProcessId || descendants.Contains(pair.Key)) continue;
                if (pair.Value != rootProcessId && !descendants.Contains(pair.Value)) continue;
                if (descendants.Add(pair.Key)) changed = true;
            }
        }
        return descendants;
    }

    public void Dispose()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal static class OfficialEmulatorRunRecovery
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int RecoverIncompleteRuns(string casesRoot)
    {
        if (!Directory.Exists(casesRoot)) return 0;
        var recovered = 0;
        foreach (var manifestPath in Directory.EnumerateFiles(casesRoot, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<OfficialEmulatorRunManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || !string.Equals(manifest.EndState, "pending", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (ProcessStillExists(manifest.EmulatorProcessId)) continue;

                var caseDirectory = Path.GetDirectoryName(manifestPath)!;
                var runAvdHome = Path.Combine(caseDirectory, "run-avd-home");
                try
                {
                    if (Directory.Exists(runAvdHome)) Directory.Delete(runAvdHome, recursive: true);
                }
                catch
                {
                    // Preserve the manifest and report failed cleanup rather than hiding it.
                }

                manifest.EndedAtUtc = DateTime.UtcNow;
                manifest.EndState = "aborted-recovered";
                manifest.Failure = "REPlayer terminated before normal run finalization; recovered on next startup.";
                manifest.RunStorageDisposed = !Directory.Exists(runAvdHome);
                var temporaryPath = manifestPath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions));
                File.Move(temporaryPath, manifestPath, overwrite: true);
                recovered++;
            }
            catch
            {
                // A malformed case manifest is evidence. Leave it untouched for the analyst.
            }
        }
        return recovered;
    }

    private static bool ProcessStillExists(int? processId)
    {
        if (processId is not > 0) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId.Value);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}

internal static class OfficialEmulatorBaseline
{
    private static readonly object CacheGate = new();

    private sealed class IntegrityCache
    {
        public int Version { get; set; } = 1;
        public string AvdDirectory { get; set; } = string.Empty;
        public string SystemImageDirectory { get; set; } = string.Empty;
        public Dictionary<string, IntegrityCacheEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class IntegrityCacheEntry
    {
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public long CreationUtcTicks { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
    public static void Clone(string sourceDirectory, string destinationDirectory, string? qemuImgPath = null, long desiredVirtualSize = 0)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException("Immutable AVD baseline is missing: " + sourceDirectory);
        if (Directory.Exists(destinationDirectory))
            Directory.Delete(destinationDirectory, recursive: true);

        CopyDirectory(sourceDirectory, destinationDirectory);
    }

    private static long GetImageVirtualSize(string qemuImgPath, string imageFile)
    {
        var psi = new ProcessStartInfo(qemuImgPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(qemuImgPath) ?? Environment.CurrentDirectory
        };
        foreach (var argument in new[] { "info", "--output=json", Path.GetFullPath(imageFile) })
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not inspect immutable userdata with qemu-img.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("qemu-img timed out while inspecting immutable userdata.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"qemu-img could not inspect immutable userdata (exit {process.ExitCode}): {stderr} {stdout}".Trim());
        using var document = JsonDocument.Parse(stdout);
        if (!document.RootElement.TryGetProperty("virtual-size", out var virtualSizeElement) ||
            !virtualSizeElement.TryGetInt64(out var virtualSize) || virtualSize <= 0)
            throw new InvalidDataException("qemu-img did not report a valid immutable userdata virtual size.");
        return virtualSize;
    }

    private static void CreateSparseRawAnchor(string qemuImgPath, string destinationFile, long length)
    {
        if (length <= 0) throw new InvalidDataException("The immutable userdata raw image has an invalid length.");
        var psi = new ProcessStartInfo(qemuImgPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(qemuImgPath) ?? Environment.CurrentDirectory
        };
        var sizeArgument = length % (1L << 30) == 0
            ? (length >> 30).ToString(System.Globalization.CultureInfo.InvariantCulture) + "G"
            : length % (1L << 20) == 0
                ? (length >> 20).ToString(System.Globalization.CultureInfo.InvariantCulture) + "M"
                : throw new InvalidDataException("The immutable userdata raw image length must be MiB-aligned.");
        foreach (var argument in new[] { "create", "-f", "raw", Path.GetFullPath(destinationFile), sizeArgument })
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start qemu-img for the disposable userdata raw anchor.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("qemu-img timed out while creating the disposable userdata raw anchor.");
        }
        if (process.ExitCode != 0 || !File.Exists(destinationFile))
            throw new InvalidOperationException($"qemu-img could not create the disposable userdata raw anchor (exit {process.ExitCode}): {stderr} {stdout}".Trim());
    }

    private static void CreateQcowOverlay(string qemuImgPath, string backingFile, string destinationFile, bool relativeBacking = false)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationFile) ?? Environment.CurrentDirectory;
        var psi = new ProcessStartInfo(qemuImgPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = relativeBacking ? destinationDirectory : Path.GetDirectoryName(qemuImgPath) ?? Environment.CurrentDirectory
        };
        var backingArgument = relativeBacking ? Path.GetFileName(backingFile) : Path.GetFullPath(backingFile);
        var destinationArgument = relativeBacking ? Path.GetFileName(destinationFile) : Path.GetFullPath(destinationFile);
        foreach (var argument in new[] { "create", "-f", "qcow2", "-F", "qcow2", "-b", backingArgument, destinationArgument })
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start qemu-img for the disposable userdata overlay.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("qemu-img timed out while creating the disposable userdata overlay.");
        }
        if (process.ExitCode != 0 || !File.Exists(destinationFile))
            throw new InvalidOperationException($"qemu-img could not create the disposable userdata overlay (exit {process.ExitCode}): {stderr} {stdout}".Trim());
    }

    private static void ConvertQcowImage(string qemuImgPath, string sourceFile, string destinationFile)
    {
        var psi = new ProcessStartInfo(qemuImgPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(qemuImgPath) ?? Environment.CurrentDirectory
        };
        foreach (var argument in new[] { "convert", "-O", "qcow2", Path.GetFullPath(sourceFile), Path.GetFullPath(destinationFile) })
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not flatten immutable userdata for the disposable run.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("qemu-img timed out while flattening disposable userdata.");
        }
        if (process.ExitCode != 0 || !File.Exists(destinationFile))
            throw new InvalidOperationException($"qemu-img could not flatten disposable userdata (exit {process.ExitCode}): {stderr} {stdout}".Trim());
    }

    private static void ResizeQcowImage(string qemuImgPath, string imageFile, long desiredVirtualSize)
    {
        if (desiredVirtualSize <= 0 || desiredVirtualSize % (1L << 20) != 0)
            throw new InvalidDataException("The disposable userdata size must be positive and MiB-aligned.");
        var sizeArgument = desiredVirtualSize % (1L << 30) == 0
            ? (desiredVirtualSize >> 30).ToString(System.Globalization.CultureInfo.InvariantCulture) + "G"
            : (desiredVirtualSize >> 20).ToString(System.Globalization.CultureInfo.InvariantCulture) + "M";
        var psi = new ProcessStartInfo(qemuImgPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(qemuImgPath) ?? Environment.CurrentDirectory
        };
        foreach (var argument in new[] { "resize", Path.GetFullPath(imageFile), sizeArgument })
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not resize the disposable userdata base overlay.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("qemu-img timed out while resizing disposable userdata.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"qemu-img could not resize disposable userdata (exit {process.ExitCode}): {stderr} {stdout}".Trim());
    }

    public static Dictionary<string, string> ComputeHashes(string avdDirectory, string systemImageDirectory, string? cacheDirectory = null)
    {
        lock (CacheGate)
        {
            var avdFullPath = Path.GetFullPath(avdDirectory);
            var systemFullPath = Path.GetFullPath(systemImageDirectory);
            IntegrityCache? previous = null;
            string? cachePath = null;
            if (!string.IsNullOrWhiteSpace(cacheDirectory))
            {
                Directory.CreateDirectory(cacheDirectory);
                var keyBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(avdFullPath + "\n" + systemFullPath));
                cachePath = Path.Combine(cacheDirectory, Convert.ToHexString(keyBytes).ToLowerInvariant() + ".json");
                try
                {
                    if (File.Exists(cachePath))
                    {
                        var candidate = JsonSerializer.Deserialize<IntegrityCache>(File.ReadAllText(cachePath));
                        if (candidate?.Version == 1 &&
                            string.Equals(candidate.AvdDirectory, avdFullPath, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(candidate.SystemImageDirectory, systemFullPath, StringComparison.OrdinalIgnoreCase))
                            previous = candidate;
                    }
                }
                catch
                {
                    previous = null;
                }
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var currentEntries = new Dictionary<string, IntegrityCacheEntry>(StringComparer.OrdinalIgnoreCase);
            HashTree("avd", avdFullPath, result, previous?.Entries, currentEntries);
            HashTree("system-image", systemFullPath, result, previous?.Entries, currentEntries);

            if (cachePath is not null)
            {
                var cache = new IntegrityCache
                {
                    AvdDirectory = avdFullPath,
                    SystemImageDirectory = systemFullPath,
                    Entries = currentEntries
                };
                var temporaryPath = cachePath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, cachePath, overwrite: true);
            }
            return result;
        }
    }

    public static IDisposable AcquireReadLease(string avdDirectory, string systemImageDirectory)
    {
        var streams = new List<FileStream>();
        try
        {
            foreach (var root in new[] { Path.GetFullPath(avdDirectory), Path.GetFullPath(systemImageDirectory) })
            {
                if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Baseline lease root is missing: " + root);
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (ShouldSkipTransientEntry(relative)) continue;
                    streams.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
                }
            }
            return new BaselineReadLease(streams);
        }
        catch
        {
            foreach (var stream in streams) stream.Dispose();
            throw;
        }
    }

    private sealed class BaselineReadLease(List<FileStream> streams) : IDisposable
    {
        private List<FileStream>? _streams = streams;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _streams, null);
            if (current is null) return;
            foreach (var stream in current) stream.Dispose();
        }
    }

    private static void HashTree(
        string label,
        string root,
        Dictionary<string, string> result,
        IReadOnlyDictionary<string, IntegrityCacheEntry>? previousEntries,
        Dictionary<string, IntegrityCacheEntry> currentEntries)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Baseline {label} directory is missing: {root}");
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (ShouldSkipTransientEntry(relative)) continue;
            var key = $"{label}/{relative}";
            var info = new FileInfo(file);
            string hash;
            if (previousEntries is not null && previousEntries.TryGetValue(key, out var cached) &&
                cached.Length == info.Length &&
                cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
                cached.CreationUtcTicks == info.CreationTimeUtc.Ticks &&
                cached.Sha256.Length == 64)
            {
                hash = cached.Sha256;
            }
            else
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            result[key] = hash;
            currentEntries[key] = new IntegrityCacheEntry
            {
                Length = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                CreationUtcTicks = info.CreationTimeUtc.Ticks,
                Sha256 = hash
            };
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory, string relativeRoot = "", IReadOnlySet<string>? skipRelativeFiles = null)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            var relative = string.IsNullOrEmpty(relativeRoot)
                ? Path.GetFileName(file)
                : relativeRoot + "/" + Path.GetFileName(file);
            if (ShouldSkipTransientEntry(relative) || (skipRelativeFiles?.Contains(relative) ?? false)) continue;
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var relative = string.IsNullOrEmpty(relativeRoot)
                ? Path.GetFileName(directory)
                : relativeRoot + "/" + Path.GetFileName(directory);
            if (ShouldSkipTransientEntry(relative + "/")) continue;
            CopyDirectory(directory, Path.Combine(destinationDirectory, Path.GetFileName(directory)), relative, skipRelativeFiles);
        }
    }

    private static bool ShouldSkipTransientEntry(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(normalized.TrimEnd('/'));
        if (normalized.StartsWith("snapshots/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("tmpAdbCmds/", StringComparison.OrdinalIgnoreCase))
            return true;
        if (normalized.Contains(".lock/", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            return true;
        return fileName.Equals("hardware-qemu.ini", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("emu-launch-params.txt", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("read-snapshot.txt", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("bootcompleted.ini", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("cache.img", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("cache.img.qcow2", StringComparison.OrdinalIgnoreCase);
    }
}
