using Mail.Contracts;

namespace Mail.Runtime.Providers;

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, IModelProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, IModelProvider provider) => _providers[name] = provider;

    public IModelProvider Resolve(string providerName)
    {
        if (_providers.TryGetValue(providerName, out var p)) return p;
        throw new ProviderNotFoundException(providerName);
    }
}
