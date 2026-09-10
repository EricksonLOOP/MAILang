using Mail.Contracts;
using System.Collections.Immutable;

namespace Mail.Cli;

public sealed class GenerateTokenTool : IToolImplementation
{
    public string? LastToken { get; private set; }

    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N");
        LastToken = token;
        return Task.FromResult(new MailSchema("GenerateTokenOutput",
            ImmutableDictionary<string, MailValue>.Empty
                .Add("token", new MailString(token))));
    }
}
