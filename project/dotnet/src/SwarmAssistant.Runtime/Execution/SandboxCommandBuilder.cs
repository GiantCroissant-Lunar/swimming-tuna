using System.Text;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Execution;

internal static class SandboxCommandBuilder
{
    public static SandboxLevel ParseLevel(string mode) =>
        (mode ?? "host").Trim().ToLowerInvariant() switch
        {
            "host" => SandboxLevel.BareCli,
            "docker" => SandboxLevel.Container,
            "apple-container" => SandboxLevel.Container,
            _ => throw new InvalidOperationException($"Unsupported sandbox mode '{mode}'.")
        };

    public static SandboxCommand Build(
        RuntimeOptions options,
        string command,
        IReadOnlyList<string> args)
    {
        var mode = (options.SandboxMode ?? "host").Trim().ToLowerInvariant();
        return mode switch
        {
            "host" => new SandboxCommand(command, args.ToArray()),
            "docker" => BuildWrapped(command, args, options.DockerSandboxWrapper, "docker"),
            "apple-container" => BuildWrapped(command, args, options.AppleContainerSandboxWrapper, "apple-container"),
            _ => throw new InvalidOperationException($"Unsupported sandbox mode '{options.SandboxMode}'.")
        };
    }

    private static SandboxCommand BuildWrapped(
        string command,
        IReadOnlyList<string> args,
        SandboxWrapperOptions wrapper,
        string mode)
    {
        if (string.IsNullOrWhiteSpace(wrapper.Command))
        {
            throw new InvalidOperationException(
                $"Sandbox mode '{mode}' requires wrapper command configuration.");
        }

        var renderedArgs = new List<string>();
        var joinedArgs = string.Join(" ", args.Select(ShellEscape));
        foreach (var token in wrapper.Args)
        {
            if (string.Equals(token, "{{args}}", StringComparison.Ordinal))
            {
                renderedArgs.AddRange(args);
                continue;
            }

            var rendered = token
                .Replace("{{command}}", command, StringComparison.Ordinal)
                .Replace("{{args_joined}}", joinedArgs, StringComparison.Ordinal);

            renderedArgs.Add(rendered);
        }

        return new SandboxCommand(wrapper.Command, renderedArgs.ToArray());
    }

    private static string ShellEscape(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        var builder = new StringBuilder();
        builder.Append('\'');
        foreach (var character in value)
        {
            if (character == '\'')
            {
                builder.Append("'\"'\"'");
            }
            else
            {
                builder.Append(character);
            }
        }

        builder.Append('\'');
        return builder.ToString();
    }
}

internal sealed record SandboxCommand(string Command, string[] Args);
