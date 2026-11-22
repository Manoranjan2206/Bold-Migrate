using System.Net;
using BoldMigrate.Core.Common;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;

namespace BoldMigrate.Infrastructure.SourceAdapters;

public static class SourceAdapterFactory
{
    public static async Task<(ISourceCatalog Adapter, SourceApi DetectedApi)> CreateAndProbeAsync(
        string baseUrl,
        NetworkCredential? credential = null,
        SourceApi preferredApi = SourceApi.RestV2,
        CancellationToken ct = default)
    {
        var resolvedUrl = HostResolver.Resolve(baseUrl);
        var restAdapter = new RestV2SourceAdapter(resolvedUrl, credential);
        var soapAdapter = new SoapSourceAdapter(resolvedUrl, credential);

        if (preferredApi == SourceApi.RestV2)
        {
            var probeRest = await restAdapter.ProbeAsync(resolvedUrl, credential, ct);
            if (probeRest.Success)
            {
                return (restAdapter, SourceApi.RestV2);
            }

            var probeSoap = await soapAdapter.ProbeAsync(resolvedUrl, credential, ct);
            if (probeSoap.Success)
            {
                return (soapAdapter, SourceApi.Soap2010);
            }

            return (restAdapter, SourceApi.RestV2);
        }
        else
        {
            var probeSoap = await soapAdapter.ProbeAsync(resolvedUrl, credential, ct);
            if (probeSoap.Success)
            {
                return (soapAdapter, SourceApi.Soap2010);
            }

            return (soapAdapter, SourceApi.Soap2010);
        }
    }

    public static ISourceCatalog Create(string baseUrl, SourceApi api, NetworkCredential? credential = null)
    {
        var resolvedUrl = HostResolver.Resolve(baseUrl);
        return api switch
        {
            SourceApi.Soap2010 => new SoapSourceAdapter(resolvedUrl, credential),
            _ => new RestV2SourceAdapter(resolvedUrl, credential)
        };
    }
}
