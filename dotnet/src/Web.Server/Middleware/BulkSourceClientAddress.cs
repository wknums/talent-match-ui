using System.Net;
using TalentMatch.Application.BulkScoringRequests.SourceConnections;

namespace TalentMatch.Web.Server.Middleware;

public sealed class BulkSourceClientAddress(IHttpContextAccessor httpContextAccessor)
    : IBulkSourceClientAddress
{
    public IPAddress GetAddress()
    {
        var address = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress;
        if (address is null)
            return IPAddress.None;

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
