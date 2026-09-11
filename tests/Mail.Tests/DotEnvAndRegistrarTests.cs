using System.Net;
using Mail.Cli;
using Mail.Compiler;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;
using Xunit;

namespace Mail.Tests;

public class DotEnvAndRegistrarTests
{
    // ── DotEnvLoader ──────────────────────────────────────────────────────────

    [Fact]
    public void DotEnvLoader_resolves_os_environment_variable()
    {
        var key = $"MAIL_TEST_DOTENV_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(key, "hello-world");
        try
        {
            var loader = DotEnvLoader.Load();
            Assert.Equal("hello-world", loader.Resolve(key));
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public void DotEnvLoader_returns_null_for_missing_variable()
    {
        var key = $"MAIL_TEST_DOTENV_{Guid.NewGuid():N}";
        var loader = DotEnvLoader.Load();
        Assert.Null(loader.Resolve(key));
    }

    [Fact]
    public void DotEnvLoader_ForTesting_injects_override_values()
    {
        var loader = DotEnvLoader.ForTesting(new Dictionary<string, string> { ["MY_KEY"] = "injected" });
        Assert.Equal("injected", loader.Resolve("MY_KEY"));
    }

    [Fact]
    public void DotEnvLoader_os_env_takes_precedence_over_injected_override()
    {
        var key = $"MAIL_TEST_DOTENV_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(key, "os-value");
        try
        {
            var loader = DotEnvLoader.ForTesting(new Dictionary<string, string> { [key] = "override-value" });
            Assert.Equal("os-value", loader.Resolve(key));
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public void DotEnvLoader_os_env_empty_falls_back_to_file_value()
    {
        var key = $"MAIL_TEST_DOTENV_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(key, "");
        try
        {
            var loader = DotEnvLoader.ForTesting(new Dictionary<string, string> { [key] = "from-file" });
            Assert.Equal("from-file", loader.Resolve(key));
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public void DotEnvLoader_both_absent_returns_null()
    {
        var key = $"MAIL_TEST_DOTENV_{Guid.NewGuid():N}";
        var loader = DotEnvLoader.ForTesting(new Dictionary<string, string>());
        Assert.Null(loader.Resolve(key));
    }

    // ── DotEnvLoader .env file parsing ────────────────────────────────────────

    [Fact]
    public void DotEnvLoader_parses_simple_key_value()
    {
        var path = WriteEnvFile("KEY=hello");
        try
        {
            var loader = DotEnvLoader.Load(path);
            Assert.Equal("hello", loader.Resolve("KEY"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DotEnvLoader_parses_double_quoted_value()
    {
        var path = WriteEnvFile("""KEY="hello world" """);
        try
        {
            var loader = DotEnvLoader.Load(path);
            Assert.Equal("hello world", loader.Resolve("KEY"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DotEnvLoader_parses_escaped_chars_in_quoted_value()
    {
        var path = WriteEnvFile("""KEY="va\"lue\\end" """);
        try
        {
            var loader = DotEnvLoader.Load(path);
            Assert.Equal("va\"lue\\end", loader.Resolve("KEY"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DotEnvLoader_skips_comment_lines()
    {
        var path = WriteEnvFile("# this is a comment\nKEY=val");
        try
        {
            var loader = DotEnvLoader.Load(path);
            Assert.Equal("val", loader.Resolve("KEY"));
            Assert.Null(loader.Resolve("# this is a comment"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DotEnvLoader_last_duplicate_key_wins()
    {
        var path = WriteEnvFile("KEY=first\nKEY=second");
        try
        {
            var loader = DotEnvLoader.Load(path);
            Assert.Equal("second", loader.Resolve("KEY"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DotEnvLoader_value_with_equals_sign_preserved()
    {
        var path = WriteEnvFile("KEY=a=b=c");
        try
        {
            var loader = DotEnvLoader.Load(path);
            Assert.Equal("a=b=c", loader.Resolve("KEY"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DotEnvLoader_strips_bom()
    {
        var path = Path.GetTempFileName();
        // Write file with BOM prefix
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("KEY=bom-value")]);
        try
        {
            var loader = DotEnvLoader.Load(path);
            Assert.Equal("bom-value", loader.Resolve("KEY"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DotEnvLoader_throws_env001_on_missing_equals()
    {
        var path = WriteEnvFile("VALID=ok\nBAD_LINE\nOTHER=ok");
        try
        {
            var ex = Assert.Throws<MailConfigurationException>(() => DotEnvLoader.Load(path));
            Assert.Contains("MAIL-ENV-001", ex.Message);
            Assert.Contains("line 2", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DotEnvLoader_throws_env002_on_unclosed_quote()
    {
        var path = WriteEnvFile("KEY=\"unclosed");
        try
        {
            var ex = Assert.Throws<MailConfigurationException>(() => DotEnvLoader.Load(path));
            Assert.Contains("MAIL-ENV-002", ex.Message);
            Assert.Contains("line 1", ex.Message);
            Assert.Contains("KEY", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DotEnvLoader_missing_file_returns_empty_loader()
    {
        var loader = DotEnvLoader.Load("/nonexistent/.env.MISSING");
        Assert.Null(loader.Resolve("ANY_KEY"));
    }

    // ── ProviderRegistrar — MAIL-CONFIG-002 error message ─────────────────────

    [Fact]
    public void RegisterDeclaredProviders_throws_config_002_message_on_missing_key()
    {
        const string providerSource = """
            provider Api {
                base_url "https://api.example.com"
                api_key env("NONEXISTENT_KEY_99999")
                call { method POST path "/v1" headers { } body { } }
                response { text "result" }
            }
            """;

        var plan = CompilePlan(providerSource);
        var registry = new ProviderRegistry();
        var env = DotEnvLoader.ForTesting(new Dictionary<string, string>());
        using var client = new HttpClient();

        var ex = Assert.Throws<ProviderConfigurationException>(
            () => ProviderRegistrar.RegisterDeclaredProviders(registry, plan, env, client));
        Assert.Contains("MAIL-CONFIG-002", ex.Message);
        Assert.Contains("NONEXISTENT_KEY_99999", ex.Message);
    }

    private static string WriteEnvFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content, System.Text.Encoding.UTF8);
        return path;
    }

    // ── ProviderRegistrar ─────────────────────────────────────────────────────

    private const string MinWorkflow = """
        schema R { x: String }
        workflow W { input R output R finish with input }
        """;

    private static ValidatedPlan CompilePlan(string providerSource)
    {
        var source = MinWorkflow + "\n" + providerSource;
        var (plan, diagnostics) = MailCompiler.Compile(source, "test.mail");
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.NotNull(plan);
        return plan!;
    }

    [Fact]
    public void RegisterDeclaredProviders_registers_simulated_provider()
    {
        var plan = CompilePlan("provider Sim { type simulated }");
        var registry = new ProviderRegistry();
        var env = DotEnvLoader.ForTesting(new Dictionary<string, string>());
        using var client = new HttpClient();

        ProviderRegistrar.RegisterDeclaredProviders(registry, plan, env, client);

        var provider = registry.Resolve("Sim");
        Assert.IsType<SimulatedModelProvider>(provider);
    }

    [Fact]
    public void RegisterDeclaredProviders_registers_http_provider_with_valid_api_key()
    {
        const string providerSource = """
            provider Api {
                base_url "https://api.example.com"
                api_key env("TEST_API_KEY")
                call { method POST path "/v1" headers { } body { "model" $model } }
                response { text "result" }
            }
            """;

        var plan = CompilePlan(providerSource);
        var registry = new ProviderRegistry();
        var env = DotEnvLoader.ForTesting(new Dictionary<string, string> { ["TEST_API_KEY"] = "sk-test-key" });
        using var client = new HttpClient();

        ProviderRegistrar.RegisterDeclaredProviders(registry, plan, env, client);

        var provider = registry.Resolve("Api");
        Assert.IsType<HttpModelProvider>(provider);
    }

    [Fact]
    public void RegisterDeclaredProviders_throws_on_missing_api_key_env_var()
    {
        const string providerSource = """
            provider Api {
                base_url "https://api.example.com"
                api_key env("NONEXISTENT_KEY_12345")
                call { method POST path "/v1" headers { } body { } }
                response { text "result" }
            }
            """;

        var plan = CompilePlan(providerSource);
        var registry = new ProviderRegistry();
        var env = DotEnvLoader.ForTesting(new Dictionary<string, string>());
        using var client = new HttpClient();

        Action act = () => ProviderRegistrar.RegisterDeclaredProviders(registry, plan, env, client);
        var ex = Assert.Throws<ProviderConfigurationException>(act);

        Assert.Equal("Api", ex.ProviderName);
        Assert.Contains("NONEXISTENT_KEY_12345", ex.Message);
    }

    [Fact]
    public void RegisterDeclaredProviders_registers_multiple_providers()
    {
        const string providerSource = """
            provider Sim { type simulated }
            provider Api {
                base_url "https://api.example.com"
                api_key env("API_KEY")
                call { method POST path "/v1" headers { } body { } }
                response { text "result" }
            }
            """;

        var plan = CompilePlan(providerSource);
        var registry = new ProviderRegistry();
        var env = DotEnvLoader.ForTesting(new Dictionary<string, string> { ["API_KEY"] = "secret" });
        using var client = new HttpClient();

        ProviderRegistrar.RegisterDeclaredProviders(registry, plan, env, client);

        Assert.IsType<SimulatedModelProvider>(registry.Resolve("Sim"));
        Assert.IsType<HttpModelProvider>(registry.Resolve("Api"));
    }
}
