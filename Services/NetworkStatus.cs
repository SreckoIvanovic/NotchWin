using Windows.Networking.Connectivity;

namespace NotchWin.Services;

public enum NetKind { None, Wifi, Ethernet, Other }

public static class NetworkStatus
{
    public static (NetKind kind, int bars, string name) Get()
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p == null || p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
                return (NetKind.None, 0, "Not connected");
            if (p.IsWlanConnectionProfile)
                return (NetKind.Wifi, p.GetSignalBars() ?? 3, p.WlanConnectionProfileDetails?.GetConnectedSsid() ?? p.ProfileName);
            if (p.NetworkAdapter?.IanaInterfaceType == 6)
                return (NetKind.Ethernet, 4, p.ProfileName);
            return (NetKind.Other, 4, p.ProfileName);
        }
        catch { return (NetKind.None, 0, "Unknown"); }
    }
}
