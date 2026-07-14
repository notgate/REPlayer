using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReVM.Automation;

public static class AdbAgentCommandPolicy
{
    private static readonly HashSet<string> ReadOnlyTopLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        "devices", "features", "get-serialno", "get-state", "host-features", "jdwp", "version",
    };

    private static readonly HashSet<string> ReadOnlyShellCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cat", "df", "getprop", "id", "ls", "md5sum", "pidof", "printenv", "ps",
        "readlink", "sha1sum", "sha256sum", "stat", "top", "uname", "uptime", "wc", "whoami",
    };

    private static readonly HashSet<string> MutatingDumpsysTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "clear", "delete", "disable", "enable", "force-idle", "reset", "set", "start", "step", "stop", "unforce", "write",
    };

    private static readonly char[] ShellMetacharacters = [';', '|', '&', '>', '<', '`', '$', '\r', '\n'];

    public static bool IsObserveOnly(IReadOnlyList<string> arguments, out string error)
    {
        error = string.Empty;
        if (arguments is null || arguments.Count == 0)
        {
            error = "Observe ADB requires at least one argument.";
            return false;
        }

        var command = arguments[0].Trim();
        if (command.StartsWith("-", StringComparison.Ordinal))
        {
            error = "ADB global options and device selectors are managed by REPlayer.";
            return false;
        }
        if (ReadOnlyTopLevel.Contains(command)) return true;
        if (string.Equals(command, "logcat", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Skip(1).Any(argument => argument.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
                                                  argument.Equals("--clear", StringComparison.OrdinalIgnoreCase) ||
                                                  argument.Equals("-f", StringComparison.OrdinalIgnoreCase) ||
                                                  argument.Equals("-G", StringComparison.OrdinalIgnoreCase) ||
                                                  argument.Equals("-n", StringComparison.OrdinalIgnoreCase) ||
                                                  argument.Equals("-r", StringComparison.OrdinalIgnoreCase)))
            {
                error = "Observe access cannot clear, resize, rotate, or redirect Android logs.";
                return false;
            }
            return true;
        }
        if (!string.Equals(command, "shell", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(command, "exec-out", StringComparison.OrdinalIgnoreCase))
        {
            error = $"ADB command '{command}' requires device-control access.";
            return false;
        }

        var remote = arguments.Skip(1).ToArray();
        if (remote.Length == 0)
        {
            error = "Interactive Android shells are not available to Observe agents.";
            return false;
        }
        if (remote.Any(argument => argument.IndexOfAny(ShellMetacharacters) >= 0))
        {
            error = "Observe shell arguments cannot contain shell operators or substitutions.";
            return false;
        }

        var firstTokens = remote[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (firstTokens.Length == 0)
        {
            error = "Observe shell command is empty.";
            return false;
        }
        var verb = Path.GetFileName(firstTokens[0]);
        var tail = firstTokens.Skip(1).Concat(remote.Skip(1)).ToArray();
        if (ReadOnlyShellCommands.Contains(verb)) return true;
        if (string.Equals(verb, "screencap", StringComparison.OrdinalIgnoreCase))
        {
            if (tail.All(argument => string.Equals(argument, "-p", StringComparison.OrdinalIgnoreCase))) return true;
            error = "Observe screencap may stream pixels but cannot write an Android file.";
            return false;
        }
        if (string.Equals(verb, "dumpsys", StringComparison.OrdinalIgnoreCase))
        {
            if (!tail.Any(MutatingDumpsysTokens.Contains)) return true;
            error = "Observe dumpsys cannot invoke state-changing service verbs.";
            return false;
        }

        if (string.Equals(verb, "pm", StringComparison.OrdinalIgnoreCase))
            return RequireSubcommand("pm", tail, new[] { "dump", "list", "path" }, out error);
        if (string.Equals(verb, "settings", StringComparison.OrdinalIgnoreCase))
            return RequireSubcommand("settings", tail, new[] { "get", "list" }, out error);
        if (string.Equals(verb, "cmd", StringComparison.OrdinalIgnoreCase))
        {
            if (tail.Length < 2 || !string.Equals(tail[0], "package", StringComparison.OrdinalIgnoreCase))
            {
                error = "Observe access permits only read-only 'cmd package' queries.";
                return false;
            }
            return RequireSubcommand("cmd package", tail.Skip(1).ToArray(),
                new[] { "dump", "list", "path", "query-activities", "query-receivers", "query-services", "resolve-activity" }, out error);
        }

        error = $"Android shell command '{verb}' requires device-control access.";
        return false;
    }

    private static bool RequireSubcommand(string command, IReadOnlyList<string> arguments, IReadOnlyList<string> allowed, out string error)
    {
        if (arguments.Count > 0 && allowed.Contains(arguments[0], StringComparer.OrdinalIgnoreCase))
        {
            error = string.Empty;
            return true;
        }
        error = $"Observe access does not permit this '{command}' subcommand.";
        return false;
    }
}
