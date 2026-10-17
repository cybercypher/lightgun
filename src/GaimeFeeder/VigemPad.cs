using System;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace GaimeFeeder
{
    // One ViGEm Xbox360 pad per gun (J1/J2). DemulShooter/Teknoparrot read aim
    // from the left stick; trigger = right trigger; A/B/Coin/Start mapped per docs/buttons.md.
    sealed class VigemPad : IDisposable
    {
        readonly ViGEmClient _client;
        readonly IXbox360Controller _pad;

        public VigemPad(int padIndex)
        {
            _client = new ViGEmClient();
            _pad = _client.CreateXbox360Controller();
            _pad.AutoSubmitReport = false;
            _pad.Connect();
        }

        public void Feed(double sx, double sy, int screenW, int screenH,
                         bool inside, GunState g, bool triggerDown)
        {
            if (inside)
            {
                short ax = (short)(sx / Math.Max(1, screenW - 1) * 65535 - 32768);
                short ay = (short)-((sy / Math.Max(1, screenH - 1)) * 65535 - 32768);
                _pad.SetAxisValue(Xbox360Axis.LeftThumbX, ax);
                _pad.SetAxisValue(Xbox360Axis.LeftThumbY, ay);
            }
            _pad.SetButtonState(Xbox360Button.RightShoulder, triggerDown);
            _pad.SetSliderValue(Xbox360Slider.RightTrigger, (byte)(triggerDown ? 255 : 0));
            bool testBtn = DateTime.UtcNow < g.TestBtnUntil;
            _pad.SetButtonState(Xbox360Button.A, g.BtnA || (testBtn && (g.TestBtnMask & 1) != 0));
            _pad.SetButtonState(Xbox360Button.B, g.BtnB || (testBtn && (g.TestBtnMask & 2) != 0));
            _pad.SetButtonState(Xbox360Button.Back, g.BtnCoin || (testBtn && (g.TestBtnMask & 4) != 0));
            _pad.SetButtonState(Xbox360Button.Start, g.BtnStart || (testBtn && (g.TestBtnMask & 8) != 0));
            _pad.SubmitReport();
        }

        public void Dispose()
        {
            try { _pad.Disconnect(); } catch { }
            _client?.Dispose();
        }
    }
}
