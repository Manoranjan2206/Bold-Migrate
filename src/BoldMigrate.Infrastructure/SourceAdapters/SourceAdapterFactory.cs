using System.Net;
using BoldMigrate.Core.Common;
using BoldMigrate.Core.Dto;
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
        var (adapter, api, _) = await CreateAndProbeWithResultAsync(baseUrl, credential, preferredApi.ToString(), ct);
        return (adapter, api);
    }

    public static async Task<(ISourceCatalog Adapter, SourceApi DetectedApi, SourceProbeResult Probe)> CreateAndProbeWithResultAsync(
        string baseUrl,
        NetworkCredential? credential = null,
        string preferredApiSetting = "Auto",
        CancellationToken ct = default)
    {
        var resolvedUrl = HostResolver.Resolve(baseUrl);
        var restAdapter = new RestV2SourceAdapter(resolvedUrl, credential);
        var soapAdapter = new SoapSourceAdapter(resolvedUrl, credential);

        if (string.Equals(preferredApiSetting, "Soap2010", StringComparison.OrdinalIgnoreCase))
        {
            var probeSoap = await soapAdapter.ProbeAsync(resolvedUrl, credential, ct);
            if (probeSoap.Success)
            {
                restAdapter.Dispose();
                return (soapAdapter, SourceApi.Soap2010, probeSoap);
            }

            var probeRest = await restAdapter.ProbeAsync(resolvedUrl, credential, ct);
            if (probeRest.Success)
            {
                soapAdapter.Dispose();
                return (restAdapter, SourceApi.RestV2, probeRest);
            }

            restAdapter.Dispose();
            return (soapAdapter, SourceApi.Soap2010, probeSoap);
        }

        // Auto or RestV2: probe REST v2 first, then fall back to SOAP 2010.
        var restProbe = await restAdapter.ProbeAsync(resolvedUrl, credential, ct);
        if (restProbe.Success)
        {
            soapAdapter.Dispose();
            return (restAdapter, SourceApi.RestV2, restProbe);
        }

        if (!string.Equals(preferredApiSetting, "RestV2", StringComparison.OrdinalIgnoreCase))
        {
            var soapProbe = await soapAdapter.ProbeAsync(resolvedUrl, credential, ct);
            if (soapProbe.Success)
            {
                restAdapter.Dispose();
                return (soapAdapter, SourceApi.Soap2010, soapProbe);
            }
        }

        soapAdapter.Dispose();
        return (restAdapter, SourceApi.RestV2, restProbe);
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
