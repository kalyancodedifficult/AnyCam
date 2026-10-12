using AnyCam.Core.Session;
using AnyCam.Core.Transport;

namespace AnyCam.Tests;

public class ReconnectTests
{
    private const string PhoneId = "2A392795-A135-4B51-8ADB-AE59016564B1";

    private static readonly UsbEndpoint Usb = new("iPhone (USB)", 7, "00008150-000A1C2E3C10801C", 47200);
    private static readonly NetworkEndpoint WiFi = new("iPhone (2A39)", "192.168.29.55", 47200, PhoneId);
    private static readonly NetworkEndpoint OtherPhone = new("iPad (77F0)", "192.168.29.60", 47200, "77F0C1D2-0000-0000-0000-000000000000");
    private static readonly Dictionary<string, string> UsbIds = new() { [Usb.SerialNumber] = PhoneId };

    [Fact]
    public void FallsBackToWiFiWhenTheCableIsUnplugged()
    {
        var next = CameraSession.ChooseReconnectEndpoint(Usb, PhoneId, [WiFi, OtherPhone], UsbIds);
        Assert.Equal(WiFi, next);
    }

    [Fact]
    public void PrefersUsbWhenTheSamePhoneIsPluggedIn()
    {
        var next = CameraSession.ChooseReconnectEndpoint(WiFi, PhoneId, [WiFi, Usb], UsbIds);
        Assert.Equal(Usb, next);
    }

    [Fact]
    public void NeverSwitchesToAnotherPhone()
    {
        var next = CameraSession.ChooseReconnectEndpoint(Usb, PhoneId, [OtherPhone], UsbIds);
        Assert.Equal(Usb, next);
    }

    [Fact]
    public void KeepsTheEndpointWhenThePhoneIsUnknown()
    {
        var next = CameraSession.ChooseReconnectEndpoint(Usb, null, [WiFi], UsbIds);
        Assert.Equal(Usb, next);
    }

    [Fact]
    public void IgnoresUsbDevicesThatNeverIdentifiedThemselves()
    {
        var unknownUsb = new UsbEndpoint("iPhone (USB)", 9, "00008150-FFFFFFFFFFFFFFFF", 47200);
        var next = CameraSession.ChooseReconnectEndpoint(WiFi, PhoneId, [unknownUsb, WiFi], UsbIds);
        Assert.Equal(WiFi, next);
    }
}
