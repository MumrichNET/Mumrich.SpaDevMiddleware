namespace Mumrich.SpaDevMiddleware.Domain.Models
{
  /// <summary>
  /// YARP Cluster
  /// </summary>
  public class ActiveHealthCheck
  {
    public string Enabled { get; set; }

    public string Interval { get; set; }

    public string Timeout { get; set; }

    public string Policy { get; set; }

    public string Path { get; set; }

    public string Query { get; set; }
  }

  /// <summary>
  /// YARP Cluster
  /// </summary>
  public class HealthCheck
  {
    public ActiveHealthCheck Active { get; set; }
  }
}
