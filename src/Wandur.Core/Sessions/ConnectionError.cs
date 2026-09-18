using L = Wandur.Core.Localization.Strings;
using System.Net.Sockets;
using Wandur.Core.Settings;

namespace Wandur.Core.Sessions;

public static class ConnectionError
{
    public static string Describe(Exception error, ConnectionProfile profile) => error switch
    {
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } =>
            L.Format(L.CouldnTFindTheHostnameCheckTheAddressThe, profile.Host),
        SocketException { SocketErrorCode: SocketError.TryAgain } =>
            L.Format(L.CouldnTLookUpRightNowCheckYourNetwork, profile.Host),
        _ => error.Message
    };
}
