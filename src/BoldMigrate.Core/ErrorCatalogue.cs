namespace BoldMigrate.Core;

/// <summary>
/// Record describing a standardized error code in the Bold Migrate specification.
/// </summary>
public record ErrorDefinition(string Code, string Category, string RaisedWhen, string MessageTemplate);

/// <summary>
/// Shipped error catalogue containing codes and user-facing messages per the Bold Migrate specification.
/// Each follows the three-part rule: what happened, the likely cause, what to do next.
/// </summary>
public static class ErrorCatalogue
{
    // Source Server Errors (SRC)
    public const string Src001 = "SRC-001";
    public const string Src002 = "SRC-002";
    public const string Src003 = "SRC-003";
    public const string Src004 = "SRC-004";
    public const string Src005 = "SRC-005";
    public const string Src006 = "SRC-006";
    public const string Src007 = "SRC-007";
    public const string Src008 = "SRC-008";
    public const string Src009 = "SRC-009";

    // Destination Server Errors (DST)
    public const string Dst001 = "DST-001";
    public const string Dst002 = "DST-002";
    public const string Dst003 = "DST-003";
    public const string Dst004 = "DST-004";

    // Credential Errors (CRED)
    public const string Cred001 = "CRED-001";
    public const string Cred002 = "CRED-002";
    public const string Cred003 = "CRED-003";

    // Transform Errors (XFRM)
    public const string Xfrm001 = "XFRM-001";
    public const string Xfrm002 = "XFRM-002";
    public const string Xfrm003 = "XFRM-003";
    public const string Xfrm004 = "XFRM-004";

    // Validation Errors (VALD)
    public const string Vald001 = "VALD-001";
    public const string Vald002 = "VALD-002";
    public const string Vald003 = "VALD-003";
    public const string Vald004 = "VALD-004";
    public const string Vald005 = "VALD-005";

    // System Errors (SYS)
    public const string Sys001 = "SYS-001";
    public const string Sys002 = "SYS-002";
    public const string Sys003 = "SYS-003";
    public const string Sys004 = "SYS-004";

    private static readonly Dictionary<string, ErrorDefinition> Definitions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Source
        [Src001] = new(
            Src001,
            "Source Server",
            "No endpoint responded during detection",
            "We could not reach a report server at that address. The URL may be wrong, or the server may not be running. Check the address and test the connection again."),
        [Src002] = new(
            Src002,
            "Source Server",
            "HTTP 401 and the probe shows the service reached the server",
            "We could not sign in to the SSRS server. The user name or password is wrong, or the account does not have Browse permission on the root folder. Check the credentials and test again."),
        [Src003] = new(
            Src003,
            "Source Server",
            "HTTP 403",
            "We signed in, but the account is not allowed to read the report catalogue. Grant the account the Browser role on the root folder, then test again."),
        [Src004] = new(
            Src004,
            "Source Server",
            "401 with a Kerberos failure and the module on a different host from SSRS",
            "We signed in, but SSRS rejected the credentials when it passed them on. This is usually Kerberos delegation, not a wrong password. Either configure delegation for the Bold Reports service account, or use Basic authentication over HTTPS."),
        [Src005] = new(
            Src005,
            "Source Server",
            "Only /api/v1.0 responded",
            "This server looks like SSRS 2016, which does not have the supported REST API. We will use the older SOAP interface instead, which works but is slower."),
        [Src006] = new(
            Src006,
            "Source Server",
            "SSRS 2008 or 2008 R2 detected",
            "This server is SSRS 2008, which Bold Migrate does not support. Reports can still be exported by hand and uploaded individually."),
        [Src007] = new(
            Src007,
            "Source Server",
            "Basic authentication selected over plain HTTP",
            "Basic authentication sends the password in a form that can be read in transit, so we will not use it over an unencrypted connection. Use HTTPS, or choose Windows authentication."),
        [Src008] = new(
            Src008,
            "Source Server",
            "A custom security extension is detected",
            "This server uses a custom security extension, which we cannot authenticate against. Contact support with your server configuration so we can advise."),
        [Src009] = new(
            Src009,
            "Source Server",
            "Repeated 429 or 503 from the source",
            "The SSRS server is refusing requests because it is busy. We have slowed down and will keep going. If this continues, run the migration outside business hours."),

        // Destination
        [Dst001] = new(
            Dst001,
            "Destination Server",
            "Token request fails with 401",
            "We could not sign in to Bold Reports with those administrator credentials. Check the email address and password and test again."),
        [Dst002] = new(
            Dst002,
            "Destination Server",
            "Token request succeeds, item creation returns 403",
            "That account can sign in but cannot create items. It needs administrator rights on the destination server."),
        [Dst003] = new(
            Dst003,
            "Destination Server",
            "Item creation returns 400 with a validation message",
            "Bold Reports rejected this report. The message from the server was: {serverMessage}. The report has been added to your review list."),
        [Dst004] = new(
            Dst004,
            "Destination Server",
            "Disk or quota failure on the destination",
            "The destination server ran out of space while saving. Free space and retry the failed items."),

        // Credentials
        [Cred001] = new(
            Cred001,
            "Credentials",
            "A data source test fails to connect",
            "We could not connect to {dataSourceName} with those details. Check the user name and password for that database."),
        [Cred002] = new(
            Cred002,
            "Credentials",
            "A connection string cannot be parsed",
            "We could not read the connection string SSRS returned for {dataSourceName}. You can enter the server and database by hand instead."),
        [Cred003] = new(
            Cred003,
            "Credentials",
            "Vault reference cannot be resolved",
            "We could not read the secret at that reference. Check that the Bold Reports service account is allowed to read it."),

        // Transform
        [Xfrm001] = new(
            Xfrm001,
            "Transform",
            "The definition is not valid XML",
            "This report definition could not be read. It may be corrupt, or it may not be a report definition at all."),
        [Xfrm002] = new(
            Xfrm002,
            "Transform",
            "Auto-fix exhausted its attempts",
            "We tried three times to make this report work and could not. The last problem was: {lastError}. It is on your review list with everything we tried."),
        [Xfrm003] = new(
            Xfrm003,
            "Transform",
            "A blocked rule fired",
            "{ruleMessage} — with a link to the knowledge base article for that rule"),
        [Xfrm004] = new(
            Xfrm004,
            "Transform",
            "The rules file cannot be loaded at all",
            "We could not load the compatibility rules, so we cannot tell you what will move. Check that the server can reach the internet, or supply the rules file locally."),

        // Validation
        [Vald001] = new(
            Vald001,
            "Validation",
            "Rendering fails on the source server",
            "SSRS could not render this report, so there is nothing to compare against. The report may already be broken on SSRS."),
        [Vald002] = new(
            Vald002,
            "Validation",
            "Rendering fails on the destination",
            "Bold Reports could not render this report. The server reported: {serverMessage}."),
        [Vald003] = new(
            Vald003,
            "Validation",
            "Page counts differ",
            "The two renders have different page counts, so we have not scored the images. Open them side by side to see where they diverge."),
        [Vald004] = new(
            Vald004,
            "Validation",
            "Calibration below 99 percent",
            "Some fonts your reports use are not installed on this server, so reports will look different without being wrong. The missing fonts are: {fontList}. Installing them will remove most of the differences."),
        [Vald005] = new(
            Vald005,
            "Validation",
            "A parameter had no default and no valid values",
            "We had to choose a value for {parameterName} to render this report, so the comparison is weaker than usual. You can supply your own values and run it again."),

        // System
        [Sys001] = new(
            Sys001,
            "System",
            "Staging path is not writable",
            "We cannot write to the staging folder at {path}. Check the folder exists and that the Bold Reports service account can write to it."),
        [Sys002] = new(
            Sys002,
            "System",
            "Staging space exhausted mid-run",
            "The server ran out of disk space partway through. Free space and resume; nothing already done will be repeated."),
        [Sys003] = new(
            Sys003,
            "System",
            "The orchestrator stopped unexpectedly",
            "The migration stopped unexpectedly and has been resumed from where it left off. {resumedCount} items were already finished."),
        [Sys004] = new(
            Sys004,
            "System",
            "A second run is started while one is active",
            "A migration is already running on this server. Finish or abandon it before starting another.")
    };

    /// <summary>
    /// Gets the ErrorDefinition for the specified error code.
    /// </summary>
    public static ErrorDefinition? GetDefinition(string code) =>
        Definitions.TryGetValue(code, out var def) ? def : null;

    /// <summary>
    /// Gets the formatted error message for the given code and optional template argument values.
    /// </summary>
    public static string FormatMessage(string code, IDictionary<string, string>? placeholders = null)
    {
        if (!Definitions.TryGetValue(code, out var def))
        {
            return $"An unknown error occurred ({code}).";
        }

        var message = def.MessageTemplate;
        if (placeholders != null)
        {
            foreach (var kvp in placeholders)
            {
                message = message.Replace($"{{{kvp.Key}}}", kvp.Value);
            }
        }

        return message;
    }
}
