using System.Net;
using System.Net.Sockets;

namespace TD.Core.Domain;

/// <summary>mail.public_addresses: where the program may call out (SMTP, webhook): never a local or reserved network.</summary>
public static class Network
{
    /// <summary>ipaddress.is_global.</summary>
    public static bool Global(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !Till.Private(ip) && !(b[0] == 100 && b[1] is >= 64 and <= 127) && b[0] < 224 && !b.All(x => x == 0);
        }
        var v = ip.GetAddressBytes();
        return !(IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6None) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (v[0] & 0xfe) == 0xfc
            || v[0] == 0x20 && v[1] == 0x01 && v[2] == 0x0d && v[3] == 0xb8 || v[0] == 0x20 && v[1] == 0x01 && v[2] == 0 && v[3] == 0 || v[0] == 0x20 && v[1] == 0x02);
    }
    public static List<IPAddress> PublicAddresses(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Any(ch => "/@ \r\n".Contains(ch))) throw new BookingException("Host SMTP non valido.");
        IPAddress[] found;
        try { found = Dns.GetHostAddresses(host); } catch (SocketException) { throw new BookingException("Indirizzo non raggiungibile."); }
        if (found.Any(ip => !Global(ip))) throw new BookingException("SMTP verso reti locali o riservate non consentito.");
        if (found.Length == 0) throw new BookingException("SMTP non risolvibile.");
        return found.ToList();
    }
    /// <summary>api.check_url: the address of a webhook: https, towards a public host.</summary>
    public static void CheckWebhook(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host == "") throw new BookingException("L'indirizzo del webhook deve iniziare con https://");
        PublicAddresses(u.Host);
    }
}
