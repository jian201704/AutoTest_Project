using OpenTap;

namespace OpenTap.Poc;

[Display("DoIP Read Vehicle Part Number", Description: "Reads DID F190 from a DoIP ECU.", Group: "OpenTAP PoC")]
public sealed class DoipReadStep : TestStep
{
    [Display("Host", Order: 1)]
    public string Host { get; set; } = "127.0.0.1";

    [Display("Port", Order: 2)]
    public int Port { get; set; }

    [Display("Expected Part Number", Order: 3)]
    public string ExpectedPartNumber { get; set; } = "DOIP";

    public override void Run()
    {
        try
        {
            var partNumber = DoipClient.ReadVehiclePartNumberAsync(Host, Port).GetAwaiter().GetResult();
            Log.Info("DoIP DID F190 returned '{0}'.", partNumber);

            if (partNumber == ExpectedPartNumber)
                UpgradeVerdict(Verdict.Pass);
            else
                UpgradeVerdict(Verdict.Fail);
        }
        catch (Exception ex)
        {
            Log.Error("DoIP request failed: {0}", ex.Message);
            UpgradeVerdict(Verdict.Fail);
        }
    }
}