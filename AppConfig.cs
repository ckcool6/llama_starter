using System.Collections.Generic;

namespace MyMarkupApp;

public class AppConfig
{
    public string ServerPath { get; set; } = string.Empty;
    public List<InstanceConfig> Instances { get; set; } = new();
}

public class InstanceConfig
{
    public int Id { get; set; }
    public string ModelPath { get; set; } = string.Empty;
    public string Port { get; set; } = "8080";
    public string Ctx { get; set; } = "4096";
    public string Ngl { get; set; } = "99";
    public string MmprojPath { get; set; } = string.Empty;
    public string ExtraArgs { get; set; } = string.Empty;
}