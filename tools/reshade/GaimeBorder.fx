/*
 * G'AIM'E white reference border for ReShade.
 *
 * Draws a white frame matching the tray gallery and the calibration window
 * (src/GaimeFeeder/ScreenBorder.cs, ScreenBorder.Pixels screen pixels on every
 * edge). Same thickness everywhere, on purpose: Sinden's border is a fixed
 * pixel count too, and a border that changes thickness between screens
 * changes the reference the player aims inside.
 *
 * The thickness is a fraction of the image (BORDER_FX of the width,
 * BORDER_FY of the height), not backbuffer pixels: a game that renders at
 * 1360x768 and is stretched to a 1920x1080 screen would otherwise get a frame
 * 1.4x thicker than the tray's. The tray writes the fractions for the current
 * screen into ReShadePreset.ini (Pixels / screen width and height).
 *
 * FIT = 1 shrinks the game picture into the area inside the frame, so the
 * frame covers nothing; the tray then maps the gun's aim into that smaller
 * area for this game (PicoOutput inset). FIT = 0 draws the frame over the
 * picture's edges.
 *
 * Installed by the tray's setup wizard (Games page, Install border) next to
 * the game's renderer; see docs/white-border.md.
 */

#include "ReShade.fxh"

uniform float BORDER_FX <
    ui_type = "drag";
    ui_min = 0.0; ui_max = 0.1; ui_step = 0.0001;
    ui_label = "Border width (fraction of the image width)";
    ui_tooltip = "Written by the tray: ScreenBorder.Pixels / screen width.";
> = 0.016667;

uniform float BORDER_FY <
    ui_type = "drag";
    ui_min = 0.0; ui_max = 0.1; ui_step = 0.0001;
    ui_label = "Border height (fraction of the image height)";
    ui_tooltip = "Written by the tray: ScreenBorder.Pixels / screen height.";
> = 0.029630;

uniform bool FIT <
    ui_label = "Shrink the game inside the border";
    ui_tooltip = "On: nothing is covered, the tray maps the aim into the smaller picture. Off: the frame covers the picture's edges.";
> = true;

float4 PS_GaimeBorder(float4 pos : SV_Position, float2 texcoord : TEXCOORD) : SV_Target
{
    float2 b = float2(BORDER_FX, BORDER_FY);
    if (any(texcoord < b) || any(texcoord > 1.0 - b))
        return float4(1.0, 1.0, 1.0, 1.0);
    float2 uv = FIT ? (texcoord - b) / (1.0 - 2.0 * b) : texcoord;
    return float4(tex2D(ReShade::BackBuffer, uv).rgb, 1.0);
}

technique GaimeBorder
{
    pass
    {
        VertexShader = PostProcessVS;
        PixelShader = PS_GaimeBorder;
    }
}
