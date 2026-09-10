namespace Mail.Contracts;

public interface IProviderRegistry
{
    IModelProvider Resolve(string providerName);
}

public interface IToolRegistry
{
    IToolImplementation Resolve(string toolName);
    FieldContract[] InputContract(string toolName);
    FieldContract[] OutputContract(string toolName);
    bool IsRegistered(string toolName);
}

public interface IModelBindings
{
    ModelBinding Resolve(string logicalName, string providerName);
}

public sealed record ModelBinding(string ProviderName, string ModelId);
