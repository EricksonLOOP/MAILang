using Mail.Contracts;

namespace Mail.Runtime.Providers;

public sealed class ModelBindings : IModelBindings
{
    private readonly Dictionary<string, ModelBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string logicalName, string providerName, string modelId) =>
        _bindings[logicalName] = new ModelBinding(providerName, modelId);

    public ModelBinding Resolve(string logicalName, string providerName)
    {
        if (_bindings.TryGetValue(logicalName, out var binding))
        {
            if (!string.Equals(binding.ProviderName, providerName, StringComparison.OrdinalIgnoreCase))
                throw new ModelBindingNotFoundException(logicalName, providerName);
            return binding;
        }

        // If no explicit binding, fall back: use providerName as modelId
        // This allows simulated provider to work without explicit bindings.
        return new ModelBinding(providerName, logicalName);
    }
}
