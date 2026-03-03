namespace Connapse.Core.Interfaces;

public interface IConnectorFactory
{
    IConnector Create(Container container);
}
