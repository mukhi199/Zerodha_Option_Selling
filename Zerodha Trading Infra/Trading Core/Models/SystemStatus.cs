using System;

namespace Trading.Core.Models;

public class SystemStatus
{
    public bool WebSocketConnected { get; set; }
    public long TicksProcessed { get; set; }
    public DateTime ServerTime { get; set; } = DateTime.Now;
}
