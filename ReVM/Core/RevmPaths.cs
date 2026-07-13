using System;
using System.IO;

namespace ReVM;

public static class RevmPaths
{
    public static string BaseDir { get; } = ResolveBaseDir();
    // Elevated helpers must not trust Environment.CurrentDirectory, which an
    // unelevated caller can select. Resolve only from the executable location
    // when crossing the administrator boundary.
    public static string ExecutableBaseDir { get; } = ResolveExecutableBaseDir();

    public static string RuntimeDir => Path.Combine(BaseDir, "runtime");
    public static string LogsDir => Path.Combine(BaseDir, "logs");

    private static string ResolveBaseDir()
    {
        var candidates = new[]
        {
            AppDomain.CurrentDomain.BaseDirectory,
            Environment.CurrentDirectory
        };

        foreach (var candidate in candidates)
        {
            var resolved = WalkForProjectRoot(candidate);
            if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
        }

        // Last-resort compatibility with the normal dotnet build layout:
        // <root>\ReVM\bin\Debug\net9.0-windows\
        return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
    }

    private static string ResolveExecutableBaseDir()
    {
        var resolved = WalkForProjectRoot(AppDomain.CurrentDomain.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(resolved)) return resolved;

        // Installed builds place runtime beside REPlayer.exe. Returning the
        // executable directory is safer than consulting a caller-controlled CWD.
        return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
    }

    private static string? WalkForProjectRoot(string start)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(start));
            while (dir != null)
            {
                var runtimeDir = Path.Combine(dir.FullName, "runtime");
                var backendSettings = Path.Combine(runtimeDir, "backend-settings.json");
                var nativeManifest = Path.Combine(runtimeDir, "revm-engine", "config", "runtime.json");
                var projectFile = Path.Combine(dir.FullName, "ReVM", "ReVM.csproj");
                var installedExecutable = Path.Combine(dir.FullName, "REPlayer.exe");
                var distributionManifest = Path.Combine(dir.FullName, "replayer-distribution-manifest.json");
                var runtimeManifest = Path.Combine(runtimeDir, "replayer-runtime-manifest.json");

                var installedDistribution = File.Exists(installedExecutable) &&
                                            File.Exists(distributionManifest) &&
                                            File.Exists(runtimeManifest);
                if (installedDistribution || File.Exists(backendSettings) || File.Exists(nativeManifest) || File.Exists(projectFile))
                    return dir.FullName;

                dir = dir.Parent;
            }
        }
        catch
        {
            // Ignore and let the caller try the next candidate.
        }

        return null;
    }
}
