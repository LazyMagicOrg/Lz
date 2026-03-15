namespace Lz.Core.Config;

public class SubtenantEntry
{
    public string SubDomain { get; set; } = string.Empty;
    public BehaviorsConfig? Behaviors { get; set; }
}
