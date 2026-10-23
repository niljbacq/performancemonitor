using System;

namespace TaskManager;

// The desktop app supplies the overlay window; Android leaves this null.
public static class OverlayHost
{
    public static Action<object?>? Toggle { get; set; }
}
