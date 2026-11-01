namespace Connapse.Application.Ports;

/// <summary>The AWS session Connapse holds, dropped after its credential changes so the next AWS call uses the new one.</summary>
public interface IAwsSession
{
    void Clear();
}
