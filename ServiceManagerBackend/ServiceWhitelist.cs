using Microsoft.Extensions.Options;
using ServiceManagerBackend.DBus;
using ServiceManagerBackend.Options;

namespace ServiceManagerBackend;

/// <summary>
/// The set of services the API is allowed to touch. All names are stored
/// normalized (".service" appended when missing) and compared case-sensitively
/// (unit names are case-sensitive).
/// </summary>
public sealed class ServiceWhitelist
{
    private readonly HashSet<string> _names;

    public ServiceWhitelist(IOptions<ServicesOptions> options)
    {
        _names = new(options.Value.Services.Select(UnitName.Normalize), StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> All => _names;

    public bool IsAllowed(string name) => _names.Contains(UnitName.Normalize(name));
}
