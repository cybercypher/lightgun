/*
 * G'AIM'E white reference border for ReShade.
 *
 * Draws a white frame of an exact number of BACKBUFFER pixels around the game,
 * matching the tray gallery and the 5-point calibration window (see
 * src/GaimeFeeder/ScreenBorder.cs, ScreenBorder.Pixels). Same pixel count
 * everywhere, on purpose: Sinden's border is a fixed pixel count too, and a
 * border that changes thickness between screens changes the reference the
 * player aims inside.
 *
 * The game's resolution is the backbuffer, so at 3840x2160 this is 32 px on
 * all four edges. If the game runs at a different render size the border is
 * still 32 px of *that* buffer, which is the correct behaviour for a border
 * drawn by the renderer at the end of the frame.
 *
 * Deployed to C:\games\teknoparrot\ElfLdr2\reshade-shaders\Shaders\ by
 * tools/Install-ReshadeBorder.ps1. Keep GAIME_BORDER_PX equal to
 * ScreenBorder.Pixels.
 */

#include "ReShade.fxh"

#ifndef GAIME_BORDER_PX
#define GAIME_BORDER_PX 32
#endif

uniform float BORDER_PX <
    ui_type = "slider";
    ui_min = 1.0;
    ui_max = 128.0;
    ui_step = 1.0;
    ui_label = "Border width (pixels)";
    ui_tooltip = "Thickness of the white frame in backbuffer pixels. Keep it equal to ScreenBorder.Pixels in the tray.";
> = GAIME_BORDER_PX;

float4 PS_GaimeBorder(float4 pos : SV_Position, float2 texcoord : TEXCOORD) : SV_Target
{
    float3 color = tex2D(ReShade::BackBuffer, texcoord).rgb;

    // Distance from the nearest edge, in pixels. min() of the four sides.
    float dx = min(texcoord.x, 1.0 - texcoord.x) * BUFFER_WIDTH;
    float dy = min(texcoord.y, 1.0 - texcoord.y) * BUFFER_HEIGHT;

    if (min(dx, dy) < BORDER_PX)
        color = float3(1.0, 1.0, 1.0);

    return float4(color, 1.0);
}

technique GaimeBorder
{
    pass
    {
        VertexShader = PostProcessVS;
        PixelShader = PS_GaimeBorder;
    }
}
