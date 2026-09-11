using Mail.Compiler;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;

namespace Mail.Cli;

public static class ProviderRegistrar
{
    public static void RegisterDeclaredProviders(
        ProviderRegistry registry,
        ValidatedPlan plan,
        DotEnvLoader env,
        HttpClient client,
        Func<string, IModelProvider>? simulatedFactory = null)
    {
        foreach (var (name, decl) in plan.Providers)
        {
            if (decl.IsSimulated)
            {
                registry.Register(name, simulatedFactory is not null
                    ? simulatedFactory(name)
                    : new SimulatedModelProvider([]));
            }
            else
            {
                var apiKeyVarName = decl.ApiKey!.EnvVarName;
                var apiKey = env.Resolve(apiKeyVarName);
                if (string.IsNullOrEmpty(apiKey))
                    throw new ProviderConfigurationException(name,
                        $"[MAIL-CONFIG-002] Environment variable '{apiKeyVarName}' required by provider '{name}' " +
                        "is not set or is empty in the OS environment and in .env.");
                registry.Register(name, new HttpModelProvider(decl, apiKey, client));
            }
        }
    }
}
