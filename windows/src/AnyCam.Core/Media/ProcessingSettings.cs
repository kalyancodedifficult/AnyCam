namespace AnyCam.Core.Media;

/// <summary>How the program image is framed, colored and monitored. Immutable; swap the whole object.</summary>
public sealed record ProcessingSettings
{
    // Framing -------------------------------------------------------------------------------
    public bool Mirror { get; init; }
    public bool Flip { get; init; }

    /// <summary>Clockwise rotation in degrees: 0, 90, 180 or 270.</summary>
    public int Rotation { get; init; }

    /// <summary>Digital zoom ≥ 1 (punch-in; lossless-looking when the phone sends 4K and the output is 1080p).</summary>
    public double Zoom { get; init; } = 1;

    /// <summary>Pan of the zoomed window, −1…1 on each axis (0 = centered).</summary>
    public double PanX { get; init; }
    public double PanY { get; init; }

    /// <summary>Program (output) size. 0 = same as the incoming video after rotation.</summary>
    public int OutputWidth { get; init; }
    public int OutputHeight { get; init; }

    /// <summary>True: crop to fill when aspect ratios differ. False: letterbox.</summary>
    public bool Fill { get; init; } = true;

    // Color ---------------------------------------------------------------------------------
    public double Exposure { get; init; }        // EV, −2…2
    public double Contrast { get; init; } = 1;    // 0.5…1.5
    public double Saturation { get; init; } = 1;  // 0…2
    public double Vibrance { get; init; }         // −1…1
    public double Temperature { get; init; }      // −1 (cool) … 1 (warm)
    public double Tint { get; init; }             // −1 (green) … 1 (magenta)
    public double Gamma { get; init; } = 1;       // 0.5…2
    public double Brightness { get; init; }       // −0.25…0.25
    public string? LutPath { get; init; }
    public double LutAmount { get; init; } = 1;

    // Monitoring (preview only) -------------------------------------------------------------
    public bool ShowGrid { get; init; }
    public bool ShowZebras { get; init; }
    public double ZebraThreshold { get; init; } = 0.95;
    public bool ShowPeaking { get; init; }
    public double PeakingThreshold { get; init; } = 0.22;
    public bool ShowSafeAreas { get; init; }
    public bool ShowVerticalGuide { get; init; }

    public bool HasColorAdjustments =>
        Exposure != 0 || Contrast != 1 || Saturation != 1 || Vibrance != 0 || Temperature != 0 ||
        Tint != 0 || Gamma != 1 || Brightness != 0;
}
