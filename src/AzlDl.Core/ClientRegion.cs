namespace AzlDl.Core;

// One regional game client: the gate server that answers version queries and the CDN that serves hash lists and assets.
public sealed record ClientRegion(string Name, string GateHost, int GatePort, string CdnUrl)
{
    public static readonly ClientRegion En = new("EN", "blhxusgate.yo-star.com", 80, "https://blhxusstatic.yo-star.com");

    public static IReadOnlyList<ClientRegion> All { get; } = [En];

    public static ClientRegion? Find(string name) =>
        All.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
}
