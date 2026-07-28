using ImageMagick;
using ImageMagick.Configuration;

namespace Jiten.Api.Services;

/// <summary>
/// Process-wide ImageMagick lockdown applied once at startup, before any image is decoded. User-uploaded
/// card media is the only attacker-controlled path into the native decoder (see <see cref="CardMediaImageProcessor"/>),
/// and the upload sniffer already restricts input to raster/audio magic bytes. This is the second layer:
/// resource ceilings that bound a decompression bomb (a tiny file that inflates to gigabytes of pixels), plus a
/// coder policy that neutralises the ImageMagick RCE/SSRF classes (MVG/MSL scripting, PS/PDF delegates,
/// url/http coders, indirect @file reads) in case a crafted file ever reaches the decoder.
/// </summary>
public static class ImageMagickHardening
{
    // A 5 MB upload can still declare enormous dimensions; the input-size gate does not bound the decoded
    // pixel buffer. Q16 holds 8 bytes per RGBA pixel, so these caps are what actually stop an OOM.
    private const int MaxDimension = 30_000;

    // policymap format is ImageMagick's own policy.xml. "none" rights fully disable a coder; the delegate and
    // path rules kill external-program invocation and indirect file reads. Raster coders (JPEG/PNG/GIF/WebP/
    // HEIC/AVIF) are deliberately left enabled — those are the formats card-media upload accepts.
    private const string PolicyXml =
        """
        <policymap>
          <policy domain="coder" rights="none" pattern="{PS,PS2,PS3,EPS,PDF,XPS}" />
          <policy domain="coder" rights="none" pattern="MSL" />
          <policy domain="coder" rights="none" pattern="MVG" />
          <policy domain="coder" rights="none" pattern="SVG" />
          <policy domain="coder" rights="none" pattern="{URL,HTTPS,HTTP,FTP}" />
          <policy domain="coder" rights="none" pattern="EPHEMERAL" />
          <policy domain="coder" rights="none" pattern="{TEXT,LABEL,CAPTION}" />
          <policy domain="delegate" rights="none" pattern="{ps,eps,pdf,xps,url,https,http,show,win}" />
          <policy domain="path" rights="none" pattern="@*" />
        </policymap>
        """;

    public static void Configure(ILogger? logger = null)
    {
        try
        {
            var configFiles = ConfigurationFiles.Default;
            configFiles.Policy.Data = PolicyXml;
            MagickNET.Initialize(configFiles);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Applying the ImageMagick security policy failed; resource limits are still set below.");
        }

        ResourceLimits.Width = MaxDimension;
        ResourceLimits.Height = MaxDimension;
        ResourceLimits.Memory = 256UL * 1024 * 1024;
        ResourceLimits.Disk = 512UL * 1024 * 1024;
        ResourceLimits.Time = 15;
    }
}
