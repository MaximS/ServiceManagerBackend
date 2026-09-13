using Microsoft.Extensions.Options;
using ServiceManagerBackend.DBus;
using ServiceManagerBackend.Options;

namespace ServiceManagerBackend;

/// <summary>
/// Startup validation for ServiceManager options: the whitelist must be non-empty and
/// every sequence entry must be part of it. The app refuses to start with a broken config.
/// </summary>
public sealed class ServicesOptionsValidator : IValidateOptions<ServicesOptions>
{
    public ValidateOptionsResult Validate(string? name, ServicesOptions options)
    {
        var errors = new List<string>();

        if (options.Services is not { Length: > 0 })
        {
            errors.Add($"{ServicesOptions.SectionName}:Services must list at least one service.");
        }
        else
        {
            foreach (string service in options.Services)
            {
                if (string.IsNullOrWhiteSpace(service))
                {
                    errors.Add($"{ServicesOptions.SectionName}:Services contains an empty entry.");
                }
            }
        }

        if (options.Sequences.StartAll is not { Length: > 0 })
        {
            errors.Add($"{ServicesOptions.SectionName}:Sequences:StartAll must list at least one service.");
        }

        if (options.Sequences.StopAll is not { Length: > 0 })
        {
            errors.Add($"{ServicesOptions.SectionName}:Sequences:StopAll must list at least one service.");
        }

        // Sequence entries must be part of the whitelist (compare normalized names).
        var whitelist = new HashSet<string>(
            options.Services.Where(s => !string.IsNullOrWhiteSpace(s)).Select(UnitName.Normalize),
            StringComparer.Ordinal);

        foreach (string service in options.Sequences.StartAll.Concat(options.Sequences.StopAll))
        {
            if (!whitelist.Contains(UnitName.Normalize(service)))
            {
                errors.Add($"{ServicesOptions.SectionName}:Sequences references '{service}', which is not in Services.");
            }
        }

        if (options.JobTimeoutSeconds <= 0)
        {
            errors.Add($"{ServicesOptions.SectionName}:JobTimeoutSeconds must be positive.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
