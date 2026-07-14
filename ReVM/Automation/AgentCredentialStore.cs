using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ReVM.Automation;

public interface IAgentCredentialStore
{
    string? Read(string agentId);
    void Write(string agentId, string secret);
    void Delete(string agentId);
}

public sealed partial class WindowsAgentCredentialStore : IAgentCredentialStore
{
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const string TargetPrefix = "REPlayer/AgentCenter/";

    public string? Read(string agentId)
    {
        var target = Target(agentId);
        if (!CredRead(target, CredentialTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new InvalidOperationException($"Windows Credential Manager could not read {target} (Win32 {error}).");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return string.Empty;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string agentId, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("API key cannot be empty.", nameof(secret));
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > 5120) throw new ArgumentException("API key is too large for Windows Credential Manager.", nameof(secret));
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = Target(agentId),
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException($"Windows Credential Manager could not save the API key (Win32 {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            for (var index = 0; index < bytes.Length; index++) Marshal.WriteByte(blob, index, 0);
            Array.Clear(bytes);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete(string agentId)
    {
        var target = Target(agentId);
        if (!CredDelete(target, CredentialTypeGeneric, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new InvalidOperationException($"Windows Credential Manager could not delete {target} (Win32 {Marshal.GetLastWin32Error()}).");
    }

    private static string Target(string agentId)
    {
        if (!AgentIdPattern().IsMatch(agentId ?? string.Empty))
            throw new ArgumentException("Agent ID may contain only letters, numbers, dot, underscore, and hyphen.", nameof(agentId));
        return TargetPrefix + agentId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex AgentIdPattern();
}

public sealed partial class AiAgentProfileStore
{
    private readonly IAgentCredentialStore _credentials;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public AiAgentProfileStore(string path, IAgentCredentialStore credentials)
    {
        Path = System.IO.Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public string Path { get; }

    public IReadOnlyList<AiAgentProfile> Load()
    {
        try
        {
            if (!File.Exists(Path)) return Array.Empty<AiAgentProfile>();
            var profiles = JsonSerializer.Deserialize<List<AiAgentProfile>>(File.ReadAllText(Path), _json) ?? new();
            return profiles.Select(Validate).OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return Array.Empty<AiAgentProfile>();
        }
    }

    public void Save(AiAgentProfile profile, string? secret = null)
    {
        profile = Validate(profile);
        var profiles = Load().Where(existing => !string.Equals(existing.Id, profile.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        profiles.Add(profile);
        WriteProfiles(profiles);
        if (!string.IsNullOrWhiteSpace(secret)) _credentials.Write(profile.Id, secret.Trim());
    }

    public void Delete(string agentId)
    {
        var profiles = Load().Where(profile => !string.Equals(profile.Id, agentId, StringComparison.OrdinalIgnoreCase)).ToList();
        WriteProfiles(profiles);
        _credentials.Delete(agentId);
    }

    public bool HasCredential(string agentId) => !string.IsNullOrWhiteSpace(_credentials.Read(agentId));

    public string? ReadCredential(string agentId) => _credentials.Read(agentId);

    private void WriteProfiles(IReadOnlyList<AiAgentProfile> profiles)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path) ?? ".");
        var temporary = Path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profiles, _json) + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static AiAgentProfile Validate(AiAgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!AgentIdPattern().IsMatch(profile.Id ?? string.Empty)) throw new InvalidDataException("Agent ID may contain only letters, numbers, dot, underscore, and hyphen.");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 96 || profile.Name.Any(char.IsControl)) throw new InvalidDataException("Agent name is invalid.");
        if (string.IsNullOrWhiteSpace(profile.Model) || profile.Model.Length > 160 || profile.Model.Any(char.IsControl)) throw new InvalidDataException("Model name is invalid.");
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Provider endpoint must be an HTTPS URL without embedded credentials.");
        if (profile.MaximumTurns is < 1 or > 64) throw new InvalidDataException("Maximum turns must be between 1 and 64.");
        return profile with { Id = profile.Id!.Trim(), Name = profile.Name!.Trim(), Model = profile.Model!.Trim(), BaseUrl = profile.BaseUrl!.Trim().TrimEnd('/') };
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex AgentIdPattern();
}
