using System.Net;
using System.Net.Sockets;

namespace PonteCI;

/// <summary>
/// Checks whether a TCP port can be bound right now. Used only to auto-pick a free port for a new
/// environment, never to decide whether a site is "running" (that is IIS state, see IisStateProbe).
/// </summary>
public static class PortProbe
{
    public static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
