namespace Mail.Contracts;

public interface IToolImplementation
{
    Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct);
}
